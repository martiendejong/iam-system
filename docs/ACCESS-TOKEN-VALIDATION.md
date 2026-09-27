# Validating IAM access tokens in a resource server

Since task 3481, `/connect/token` issues **access tokens as signed RS256 JWTs** (three dot-separated
parts). A service that is not IAM can verify them offline with the public key IAM publishes; it does
not need a secret and does not call IAM per request.

Refresh tokens and authorization codes are still encrypted and opaque to clients. Only IAM reads them.

## What a resource server must check

| Check | Value |
|---|---|
| Issuer | the `issuer` of `/.well-known/openid-configuration` (live: `https://maendeleo.martiendejong.nl/auth/`, with the trailing slash) |
| Signing key | JWKS at the discovery document's `jwks_uri` (live: `https://maendeleo.martiendejong.nl/.well-known/jwks`), key chosen by the token's `kid` |
| Algorithm | `RS256` only. Reject everything else, including `none` |
| Token type | header `typ` must be `at+jwt`. The id_token is signed with the same key but has a different `typ`; it must not be accepted as an access token |
| Lifetime | `exp` (access tokens live 15 minutes), small clock skew |
| Audience | tokens carry no `aud` yet, so audience validation has to be off |
| Authorization | decide from the claims below, with an allow-list. "Any valid IAM token" is not an authorization rule |

## Claims in an access token

An access token is readable by whoever holds it, so it only carries what a resource server needs.
Name and e-mail stay in the id_token.

| Claim | Meaning |
|---|---|
| `sub` | user tokens: the user id (GUID). Client-credentials tokens: the client id, for example `jengo-agi-svc` |
| `client_id` | the client the token was issued to |
| `role` | user tokens only; one claim per role. Client-credentials tokens have no roles |
| `tenant_id` | user tokens, only when the app's role assignment is tenant-scoped |
| `scope` | space-separated granted scopes |
| `iss`, `iat`, `exp`, `jti`, `oi_*` | housekeeping |

Authorize machine callers on `sub` (or `client_id`), not on `role`.

## ASP.NET Core

```csharp
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = "https://maendeleo.martiendejong.nl/auth/"; // discovery gives issuer + JWKS
        options.MapInboundClaims = false;                                // keep "sub" and "role" as they are
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = false,                                    // no aud in IAM access tokens yet
            ValidateLifetime = true,
            ValidAlgorithms = new[] { SecurityAlgorithms.RsaSha256 },
            ValidTypes = new[] { "at+jwt" },                             // rejects id_tokens
            NameClaimType = "sub",
            RoleClaimType = "role"
        };
    });

// then, for example, an allow-list for the agent service account:
builder.Services.AddAuthorization(o => o.AddPolicy("Agent",
    p => p.RequireAuthenticatedUser().RequireClaim("sub", "jengo-agi-svc")));
```

Any JOSE library works the same way: pick the JWKS key by `kid`, allow only `RS256`, check the `typ`
header and the registered claims.

## Operational notes

- **Cache the JWKS** and re-fetch it once when a token carries an unknown `kid` (key rotation).
- **No offline revocation.** `/connect/revoke` cannot reach a service that validates offline. The
  15-minute lifetime bounds the exposure; if a use case needs immediate revocation it needs a
  different mechanism than this one.
- **Tokens from before the switch** are encrypted (five parts). IAM keeps accepting them until they
  expire (at most 15 minutes after the deploy); an offline validator cannot read them and must treat
  them as invalid.
- **Do not log** access tokens or put them in URLs.
