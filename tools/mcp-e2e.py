#!/usr/bin/env python3
"""End-to-end check of the IAM MCP connector against a running IAM (docs/MCP.md). Standard library only.

Walks exactly what ChatGPT / Claude.ai do: 401 challenge, discovery, dynamic client registration, sign-in, consent,
PKCE code exchange, tools/list, a tool call, refresh. Needs one IAM account that is an IAM admin (so the admin tools
can be proven too). Credentials come from environment variables and are never printed; neither are tokens.

  IAM_BASE=https://iam-acc.martiendejong.nl IAM_EMAIL=... IAM_PASSWORD=... python3 mcp-e2e.py
  optional: IAM_PREFIX=/auth (prod path prefix of the admin API), SSO_CLIENT_ID, SSO_REDIRECT_URI

Exit code 0 only when every check passed. Not for production load: it creates one dynamic client and one authorization.
"""
import base64
import hashlib
import html
import http.cookiejar
import json
import os
import re
import secrets
import sys
import urllib.error
import urllib.parse
import urllib.request

BASE = os.environ["IAM_BASE"].rstrip("/")
PREFIX = os.environ.get("IAM_PREFIX", "").rstrip("/")
EMAIL = os.environ.get("IAM_EMAIL", "")
PASSWORD = os.environ.get("IAM_PASSWORD", "")
REDIRECT = "https://chatgpt.com/connector/oauth/abc123"

results = []


def check(name, ok, detail=""):
    results.append(bool(ok))
    print(("PASS " if ok else "FAIL ") + name + (f" ({detail})" if detail else ""))
    return ok


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs):
        return None


jar = http.cookiejar.CookieJar()
opener = urllib.request.build_opener(NoRedirect, urllib.request.HTTPCookieProcessor(jar))


def call(method, url, data=None, headers=None, raw=False):
    headers = dict(headers or {})
    body = None
    if isinstance(data, dict):
        if headers.get("Content-Type") == "application/json":
            body = json.dumps(data).encode()
        else:
            body = urllib.parse.urlencode(data).encode()
            headers.setdefault("Content-Type", "application/x-www-form-urlencoded")
    elif isinstance(data, (bytes, str)):
        body = data.encode() if isinstance(data, str) else data
    req = urllib.request.Request(url, data=body, method=method, headers=headers)
    try:
        resp = opener.open(req, timeout=40)
        status, hdrs, text = resp.status, resp.headers, resp.read().decode("utf-8", "replace")
    except urllib.error.HTTPError as e:
        status, hdrs, text = e.code, e.headers, e.read().decode("utf-8", "replace")
    return status, hdrs, text


def jget(text):
    try:
        return json.loads(text)
    except Exception:
        return {}


def mcp(token, method, params=None, rid=1):
    body = json.dumps({"jsonrpc": "2.0", "id": rid, "method": method, "params": params or {}})
    status, hdrs, text = call("POST", BASE + "/mcp", body, {
        "Content-Type": "application/json", "Accept": "application/json, text/event-stream",
        "Authorization": "Bearer " + token})
    if status != 200:
        return status, {}
    if not text.lstrip().startswith("{"):
        text = next((l[5:].strip() for l in text.splitlines() if l.startswith("data:")), "{}")
    return status, jget(text)


def tool(token, name, args=None):
    status, rpc = mcp(token, "tools/call", {"name": name, "arguments": args or {}}, 7)
    res = rpc.get("result", {})
    text = (res.get("content") or [{}])[0].get("text", "")
    return status, bool(res.get("isError")), text


# 1. the 401 challenge on both verbs
for verb in ("GET", "POST"):
    s, h, _ = call(verb, BASE + "/mcp", "{}" if verb == "POST" else None, {"Content-Type": "application/json"} if verb == "POST" else {})
    www = h.get("WWW-Authenticate", "")
    check(f"{verb} /mcp without token -> 401 + Bearer resource_metadata", s == 401 and "Bearer" in www and "resource_metadata=" in www, f"{s}")

# 2. discovery
s, _, t = call("GET", BASE + "/.well-known/oauth-protected-resource/mcp")
prm = jget(t)
auth_servers = prm.get("authorization_servers") or []
check("protected resource metadata names an authorization server", s == 200 and len(auth_servers) == 1, f"{s} {auth_servers}")
check("resource metadata scopes are mcp + offline_access", set(prm.get("scopes_supported", [])) == {"mcp", "offline_access"}, str(prm.get("scopes_supported")))

issuer = auth_servers[0] if auth_servers else BASE + "/"
parsed = urllib.parse.urlparse(issuer)
issuer_path = parsed.path.rstrip("/")
rfc8414 = f"{parsed.scheme}://{parsed.netloc}/.well-known/oauth-authorization-server{issuer_path}"
s, _, t = call("GET", rfc8414)
as_meta = jget(t)
check("RFC 8414 authorization server metadata (issuer-path form)", s == 200 and as_meta.get("registration_endpoint", "").endswith("/connect/register")
      and "S256" in as_meta.get("code_challenge_methods_supported", []), f"{s} {rfc8414}")
oidc = f"{issuer.rstrip('/')}/.well-known/openid-configuration"
s, _, t = call("GET", oidc)
oidc_meta = jget(t)
check("OIDC discovery has registration_endpoint, S256, none", s == 200 and "registration_endpoint" in oidc_meta
      and "S256" in oidc_meta.get("code_challenge_methods_supported", [])
      and "none" in oidc_meta.get("token_endpoint_auth_methods_supported", []), f"{s} {oidc}")
reg_endpoint = oidc_meta.get("registration_endpoint") or as_meta.get("registration_endpoint") or BASE + "/connect/register"
auth_endpoint = oidc_meta.get("authorization_endpoint") or BASE + "/connect/authorize"
token_endpoint = oidc_meta.get("token_endpoint") or BASE + "/connect/token"

# 3. dynamic client registration
s, _, t = call("POST", reg_endpoint, {"redirect_uris": [REDIRECT], "client_name": "IAM e2e check", "token_endpoint_auth_method": "none"},
               {"Content-Type": "application/json"})
reg = jget(t)
client_id = reg.get("client_id", "")
check("DCR chatgpt.com/connector/oauth/abc123 -> 201 public client", s == 201 and client_id.startswith("mcp-") and "client_secret" not in reg, str(s))
for uri in ("https://claude.ai/api/mcp/auth_callback", "https://claude.com/api/mcp/auth_callback",
            "https://chatgpt.com/connector_platform_oauth_redirect", "https://platform.openai.com/apps-manage/oauth",
            "http://localhost:6274/oauth/callback"):
    s, _, _ = call("POST", reg_endpoint, {"redirect_uris": [uri], "client_name": "IAM e2e check"}, {"Content-Type": "application/json"})
    check(f"DCR {uri} -> 201", s == 201, str(s))
for uri in ("https://evil.example/cb", "https://chatgpt.com/connector/oauth/a/b"):
    s, _, _ = call("POST", reg_endpoint, {"redirect_uris": [uri]}, {"Content-Type": "application/json"})
    check(f"DCR {uri} -> 400", s == 400, str(s))
s, _, _ = call("POST", reg_endpoint, {"redirect_uris": [REDIRECT], "grant_types": ["client_credentials"]}, {"Content-Type": "application/json"})
check("DCR client_credentials grant -> 400", s == 400, str(s))
s, _, _ = call("POST", reg_endpoint, {"redirect_uris": [REDIRECT], "scope": "openid roles"}, {"Content-Type": "application/json"})
check("DCR wider scope -> 400", s == 400, str(s))

# 4. existing SSO client still redirects to the login page (OpenIddict path for ordinary apps is intact)
sso_client = os.environ.get("SSO_CLIENT_ID")
sso_redirect = os.environ.get("SSO_REDIRECT_URI")
if sso_client and sso_redirect:
    ch = base64.urlsafe_b64encode(hashlib.sha256(b"x" * 43).digest()).rstrip(b"=").decode()
    q = urllib.parse.urlencode({"client_id": sso_client, "response_type": "code", "redirect_uri": sso_redirect, "scope": "openid profile email",
                                "state": "s", "code_challenge": ch, "code_challenge_method": "S256"})
    s, h, _ = call("GET", auth_endpoint + "?" + q)
    check(f"existing app SSO ({sso_client}) /connect/authorize -> 302 to IAM login", s == 302 and "login" in h.get("Location", ""), f"{s}")

if not (EMAIL and PASSWORD):
    print("no IAM_EMAIL/IAM_PASSWORD: stopping before the sign-in part")
    sys.exit(0 if all(results) else 1)

# 5. normal IAM login (what every app uses) then the consent flow
s, _, t = call("POST", BASE + PREFIX + "/api/auth/login", {"email": EMAIL, "password": PASSWORD}, {"Content-Type": "application/json"})
check("normal IAM login (POST /api/auth/login) -> 200", s == 200, str(s))
if s != 200:
    sys.exit(1)

verifier = secrets.token_urlsafe(48)
challenge = base64.urlsafe_b64encode(hashlib.sha256(verifier.encode()).digest()).rstrip(b"=").decode()
q = urllib.parse.urlencode({"client_id": client_id, "response_type": "code", "redirect_uri": REDIRECT, "scope": "mcp offline_access",
                            "state": "e2e-state", "code_challenge": challenge, "code_challenge_method": "S256"})
s, h, page = call("GET", auth_endpoint + "?" + q)
check("authorize shows the consent page (200 html, read-only wording)", s == 200 and "read-only" in page and "cannot" in page.lower(), str(s))
fields = {html.unescape(k): html.unescape(v) for k, v in re.findall(r'<input type="hidden" name="([^"]*)" value="([^"]*)">', page)}
check("consent carries an approval token", "mcp_consent" in fields)

s, h, _ = call("POST", auth_endpoint, dict(fields, mcp_decision="deny"))
check("Cancel -> redirect with access_denied, no code", s == 302 and "error=access_denied" in h.get("Location", "") and "code=" not in h.get("Location", ""), str(s))

s, h, _ = call("POST", auth_endpoint, dict(fields, mcp_decision="allow"))
loc = h.get("Location", "")
code = urllib.parse.parse_qs(urllib.parse.urlparse(loc).query).get("code", [""])[0]
check("Allow -> redirect to the client with code and state", s == 302 and loc.startswith(REDIRECT) and code and "state=e2e-state" in loc, str(s))

s, _, t = call("POST", token_endpoint, {"grant_type": "authorization_code", "code": code, "redirect_uri": REDIRECT,
                                          "client_id": client_id, "code_verifier": verifier})
tok = jget(t)
access, refresh = tok.get("access_token", ""), tok.get("refresh_token", "")
check("code + PKCE exchange -> access and refresh token, no id_token", s == 200 and access and refresh and "id_token" not in tok, str(s))

claims = {}
if access.count(".") == 2:
    seg = access.split(".")[1]
    claims = jget(base64.urlsafe_b64decode(seg + "=" * (-len(seg) % 4)).decode())
scopes = str(claims.get("scope", "")).split() if not isinstance(claims.get("scope"), list) else claims["scope"]
aud = claims.get("aud") if isinstance(claims.get("aud"), list) else [claims.get("aud")]
check("access token: audience iam_mcp, scope mcp, no openid", "iam_mcp" in aud and "mcp" in scopes and "openid" not in scopes, f"aud={aud} scope={scopes}")
check("access token carries no role / tenant / email / name claims", not any(k in claims for k in ("role", "roles", "tenant_id", "email", "name")))

# 6. the MCP calls
s, rpc = mcp(access, "initialize", {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "e2e", "version": "1"}})
check("initialize", s == 200 and rpc.get("result", {}).get("serverInfo", {}).get("name") == "Jengo IAM", str(s))
s, rpc = mcp(access, "tools/list", rid=2)
names = sorted(t["name"] for t in rpc.get("result", {}).get("tools", []))
check("tools/list = the six read-only tools", names == ["get_user", "list_audit_events", "list_clients", "list_sessions", "list_users", "whoami"], str(names))
s, err, text = tool(access, "whoami")
who = jget(text)
check("whoami returns the signed-in admin", s == 200 and not err and who.get("email", "").lower() == EMAIL.lower() and who.get("isIamAdmin") is True, f"isIamAdmin={who.get('isIamAdmin')}")
s, err, text = tool(access, "list_users", {"limit": 5})
check("list_users (admin) returns rows, no secrets", not err and jget(text).get("count", 0) >= 1 and "passwordHash" not in text and "$2a$" not in text and "$2b$" not in text,
      f"count={jget(text).get('count')}")
s, err, text = tool(access, "list_clients")
check("list_clients (admin) returns rows, no secrets", not err and jget(text).get("count", 0) >= 1 and "secret" not in text.lower(), f"count={jget(text).get('count')}")
s, err, text = tool(access, "list_sessions")
check("list_sessions returns data, no token hash", not err and "tokenHash" not in text.lower(), f"returned={jget(text).get('returned')}")
s, err, text = tool(access, "list_audit_events", {"limit": 5})
check("list_audit_events returns rows (incl. the audit rows of this session)", not err and jget(text).get("count", 0) >= 1, f"count={jget(text).get('count')}")
s, rpc = mcp(access, "tools/call", {"name": "send_invitation", "arguments": {}}, 9)
check("mutating API-key tools are not reachable with an OAuth token", "result" not in rpc, str(s))

# 7. refresh
s, _, t = call("POST", token_endpoint, {"grant_type": "refresh_token", "refresh_token": refresh, "client_id": client_id})
tok2 = jget(t)
check("refresh token grant -> new access token", s == 200 and tok2.get("access_token"), str(s))
s, err, text = tool(tok2.get("access_token", "x"), "whoami")
check("refreshed token works at /mcp", s == 200 and not err, str(s))
s, _, _ = call("POST", BASE + "/mcp", "{}", {"Content-Type": "application/json", "Authorization": "Bearer aaa.bbb.ccc"})
check("garbage JWT -> 401", s == 401, str(s))

print(f"\n{sum(results)}/{len(results)} checks passed")
sys.exit(0 if all(results) else 1)
