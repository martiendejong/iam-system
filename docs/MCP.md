# IAM as an MCP connector for ChatGPT and Claude.ai

IAM exposes a **read-only** MCP server at `/mcp` that can be added as a custom connector in ChatGPT and Claude.ai.
Sign-in is the normal IAM login: the assistant sends you to IAM, you sign in, you read a plain consent page and press
Allow. Nothing in this document replaces `docs/mcp-server.md`, which describes the separate API-key tool set
(invitations and role changes for Jengo's own agents); the two never see each other's tools.

| Environment | Connector URL |
|---|---|
| prod | `https://maendeleo.martiendejong.nl/mcp` |
| acc | `https://iam-acc.martiendejong.nl/mcp` |
| test | `https://iam-test.martiendejong.nl/mcp` |

## Design decision: IAM is the authorization server, `/mcp` is a protected resource

Two options were evaluated.

**A. A second token layer** (what the app-level connectors of JengoKnowledge, JengoWork and the others do): `/mcp` hosts
its own OAuth endpoints, mints its own tokens and stores its own clients. That needs a token store, a signing secret,
its own refresh and revocation logic, and a consent flow that has to re-authenticate the user against IAM anyway.
For an app that has no OAuth server of its own it is the right call. IAM already is one.

**B. Chosen: no second layer.** The existing OpenIddict server mints the tokens. The MCP endpoint only validates them.
Everything that was added is small and sits next to code that already exists:

| Piece | File | What it does |
|---|---|---|
| Dynamic client registration (RFC 7591) | `Mcp/OAuth/DynamicClientRegistrationController.cs` | `POST /connect/register` creates a public PKCE OpenIddict application from a strict profile |
| Authorization server metadata (RFC 8414) | `Mcp/OAuth/McpAuthorizationServerMetadataController.cs` + a handler in `Program.cs` | `registration_endpoint` and `none` added to the OIDC discovery document; the issuer-path variants of `/.well-known/oauth-authorization-server` |
| Protected resource metadata (RFC 9728) | `Mcp/IamMcpExtensions.cs` (the template already serves the document) | `authorization_servers` now points at IAM itself, `scopes_supported` is `mcp offline_access` |
| Consent | `Mcp/OAuth/McpConsentService.cs` + a branch in `AuthorizationController.Authorize` | explicit consent page for dynamically registered clients |
| Token validation | `Mcp/OAuth/IamMcpOAuthTokenValidator.cs` | `IMcpBearerTokenValidator` of the shared MCP template |
| Tools | `Mcp/OAuth/IamMcpOAuthToolProvider.cs` | six read-only tools |
| Scope `mcp` | `Workers/DatabaseSeeder.cs` | maps scope `mcp` to audience `iam_mcp` |

No new tables, no migration, no new secret, no new signing key. The new scope row is created by the existing seeder at
startup (additive, idempotent). Dynamic clients are ordinary rows in `OpenIddictApplications`.

## The flow

1. The client calls `/mcp` without a token and gets `401` with
   `WWW-Authenticate: Bearer resource_metadata="https://<host>/.well-known/oauth-protected-resource/mcp"`. This is
   the same for `GET` and `POST`.
2. It reads the protected resource metadata, which names IAM (`Jwt:Issuer`) as authorization server and offers the scopes
   `mcp` and `offline_access`.
3. It reads the authorization server metadata (OIDC discovery, or `/.well-known/oauth-authorization-server[/auth]`), which
   advertises `registration_endpoint`, `code_challenge_methods_supported: [S256]` and the `none` auth method.
4. It registers itself: `POST /connect/register` returns `201` with a `client_id` (prefix `mcp-`), no secret.
5. It sends the user to `/connect/authorize` (authorization code, PKCE S256). The user signs in at IAM as usual
   (password, passkey, MFA, tenant IP rules: unchanged), then sees the consent page and presses Allow.
6. It exchanges the code at `/connect/token` (public client, `code_verifier`), receives an access token (30 minutes) and a
   refresh token (the existing sliding 90 day policy), and calls `/mcp` with `Authorization: Bearer <access token>`.

## What a dynamically registered client can and cannot be

Fixed on the server, whatever the registration request says:

* public client, no secret, PKCE required, grant types `authorization_code` and `refresh_token` only, response type `code`
  only. `client_credentials`, `password` and device grants are refused at registration and by OpenIddict's own
  per-application permission check at the token endpoint;
* scopes `mcp` and `offline_access` only. A request for `roles`, `profile`, `taskmanager_api`, `iam_resolver` or any other
  scope is refused with `invalid_scope`. (`openid` is exempt from OpenIddict's permission check; a client that sends it
  anyway still gets exactly `mcp offline_access`, no id token, and the scope list of the token shows it.)
* redirect URIs from an allowlist (`McpRedirectUriPolicy`): `https://chatgpt.com/connector/oauth/<one id segment>`,
  `https://chatgpt.com/connector_platform_oauth_redirect`, `https://platform.openai.com/apps-manage/oauth`,
  `https://claude.ai/api/mcp/auth_callback`, `https://claude.com/api/mcp/auth_callback`, and loopback
  (`http://localhost[:port]/path`, `http://127.0.0.1[:port]/path`) for local clients. No wildcards on hosts, no query, no
  fragment, at most five per client, and one bad URI rejects the whole request;
* registrations are throttled: 30 per IP per hour and 1000 dynamic clients in total (`Mcp:OAuth:RegistrationsPerIpPerHour`,
  `Mcp:OAuth:MaxDynamicClients`);
* the client name is reduced to plain text and only shown HTML-encoded, with the redirect host next to it.

### Why these tokens are not a general IAM session

The access token of a dynamic client is a signed JWT with **audience `iam_mcp`**, scope `mcp offline_access`, and
**only the subject claim**. The authorization endpoint and the token endpoint strip `role`, `tenant_id`, `name` and `email`
from it (`StripToSubject`), and no id token is issued. So:

* `/mcp` accepts only tokens with scope `mcp` and audience `iam_mcp` from a dynamic client; an ordinary IAM login token
  (even of a SuperAdmin) is `401` there (test `OrdinaryIamLoginToken_IsNotAcceptedAtMcp`);
* the other IAM resource endpoints that accept OpenIddict tokens need other scopes (`iam_resolver`) or return only what the
  granted scopes allow (`/connect/userinfo` returns the subject and nothing else for scope `mcp`);
* a Jengo app that validated IAM tokens without checking audience or scope would still get a token with no roles and no
  tenant, so there is nothing for it to authorize on. Apps that map users by `sub` alone and grant access to any valid IAM
  token are the one theoretical gap; they are already exposed to every other IAM client token, and IAM's app-role gate
  (`{clientId}:{role}` catalogs) does not apply to a client that has no catalog.

Roles and admin status are never read from the token. They are looked up in the database on every tool call.

## Consent text

The consent page says in plain language what the connector can read, what it cannot do, that what it reads goes to the
company behind the assistant, and how access ends. The Allow button is bound to a time-limited (10 minutes) data protection
token over (user, client, redirect URI, code challenge, state, scope), so a hostile page cannot forge the approval, and a
token cannot be replayed for another request or carried to another user's session (tests
`Consent_ForgedOrReplayedToken_DoesNotIssueACode`, `Consent_Denied_...`). Cancel returns `access_denied` to the client.

## Tools

All six are annotated `readOnlyHint: true`. There is no tool that creates, changes, deletes, resets or revokes anything.
Fields that must never leave IAM are not in the projections at all (password hash, verification and reset tokens, two-factor
secret, phone number, refresh token hash, session tokens, device fingerprints, audit `Details`, client secrets), so a filter
bug cannot leak them. The tests plant sentinel values in each of those columns and assert they never appear in any output.

| Tool | Who | Returns |
|---|---|---|
| `whoami` | any connected user | id, name, email, status, two-factor on/off, last login, roles with tenant, `isIamAdmin`, connected client id |
| `list_users` | IAM admin | up to 100 users: name, email, active/locked/two-factor flags, last login, roles. Filters: `search`, `tenantId`, `includeInactive`, `limit` |
| `get_user` | IAM admin | one user by `email` or `id` with role assignments |
| `list_clients` | IAM admin | registered OAuth applications: client id, name, type, redirect URIs, grant types, scopes, PKCE, `dynamicallyRegistered`. Never secrets |
| `list_sessions` | self, or admin for anyone | active sign-in sessions (time, IP, user agent, expiry) and trusted devices (name, last used, expiry). An admin without arguments gets all active sessions and the total |
| `list_audit_events` | IAM admin | recent audit and login events: time, action, resource, user, IP address. Filters: `action`, `userId`/`email`, `sinceHours` (max 720), `limit` |

"IAM admin" means an active `SuperAdmin` or `SystemAdmin` role assignment (not expired), read from the database on each
call. Everything else gets `Forbidden`. Each tool call writes an audit row (`mcp.oauth.tool`, resource `mcp:<tool>`, the
client id) so an administrator can see what a connected assistant looked at; this row has no parameters.

## Token validation, per request

`IamMcpOAuthTokenValidator` runs OpenIddict's own validation (signature, issuer, lifetime) and then requires: scope
`mcp`; audience `iam_mcp`; the client is a still-existing dynamically registered client; the user exists, is active and
not locked out; the OpenIddict authorization behind the token is still valid. Deactivating the user, locking the user, or
revoking the authorization (or deleting the client) ends access on the next call, not after the 30 minute token lifetime.
Refreshing re-checks the user, the tenant IP allowlist and the app-role gate exactly as for every other client.

## Configuration

| Setting | Default | Effect |
|---|---|---|
| `Mcp:OAuth:Enabled` | `true` | **Kill switch.** `false` (env `Mcp__OAuth__Enabled=false`, then restart): `/connect/register` and the issuer-path metadata answer 404, OAuth tokens are refused at `/mcp`, `authorization_servers` is no longer advertised. Existing API-key access and every other IAM client are unaffected |
| `Mcp:OAuth:AuthorizationServer` | `Jwt:Issuer` with one trailing slash | the authorization server URL advertised in the resource metadata |
| `Mcp:OAuth:RegistrationsPerIpPerHour` | 30 | DCR throttle |
| `Mcp:OAuth:MaxDynamicClients` | 1000 | DCR cap |

`Mcp:Keys` and the API-key tool set are unchanged.

## Deployment notes

* No migration. The scope `mcp` is seeded at startup; the discovery document gains `registration_endpoint`, `none` in
  `token_endpoint_auth_methods_supported` and `mcp` in `scopes_supported` (additive).
* The reverse proxy must pass `/mcp`, `/connect/*` and `/.well-known/*` to IAM on the host root (the prod Caddy already
  sends everything not under another path to IAM; `/auth/*` is stripped).
* Rolling back is replacing the image. Dynamic clients left behind are inert rows; remove them with
  `DELETE FROM "OpenIddictApplications" WHERE "Properties" LIKE '%iam_dcr%'` (after the authorizations and tokens that
  reference them: `OpenIddictTokens`, `OpenIddictAuthorizations`).
* ChatGPT caches the tool list per connector: after changing tools, use "Reconnect" on the connector.

## Residual risks and things to know

* Anyone with an active IAM account can complete the connector flow for themselves. They get `whoami` and their own
  sessions, nothing else, unless they are an IAM admin.
* An administrator who connects ChatGPT or Claude sends user lists, client lists and audit events to that provider as part
  of the conversation. The consent text says so. Whether that is acceptable for a given tenant is a policy decision.
* DCR is anonymous by nature. The allowlist, the fixed profile and the throttles bound it; the table of dynamic clients is
  visible to admins through `list_clients` and the admin UI.
* Access tokens are not checked against `OpenIddictTokens` (no token entry validation), so revoking a single access token
  has no effect before it expires. Revoke the authorization instead (it is checked on every call), or deactivate the user.
