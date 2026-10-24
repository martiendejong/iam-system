# Jengo IAM SSO (WordPress plugin)

Single sign-on for WordPress via Jengo IAM: OpenID Connect authorization code
flow with PKCE (S256), with a **WordPress / IAM / Both** login mode (JengoWork
task 5804, board 44). Install and configure it with [INSTALL.md](INSTALL.md).

Pure PHP, no runtime dependencies: WordPress HTTP API, transients and the
OpenSSL extension.

## Behaviour in one page

- **Discovery only**: endpoints come from `{issuer}/.well-known/openid-configuration`
  (cached 12h, a failure for 60s). The document's `issuer` must equal the
  configured issuer and every endpoint must be https. There are no guessed
  fallback URLs; when discovery fails the login form is shown instead.
- **Login modes**: `both` (default) = form + button, `wordpress` = no button,
  `iam` = `wp-login.php` redirects to IAM and password logins are refused.
  IAM-only never locks you out: bypass URL, `JENGO_IAM_SSO_BYPASS` constant,
  WP-CLI (`wp jengo-iam-sso ...`), automatic fallback when IAM is unreachable,
  and the signed-out page is not redirected (no silent re-login loop).
- **Callback** (`wp-login.php?action=jengo-iam-callback`): `state` is single-use
  and bound to the browser by an HttpOnly cookie; code exchange with
  `client_secret_post` + PKCE; the **id_token is mandatory** and validated
  (RS256 signature against the JWKS, `iss`, `aud`/`azp`, `exp`/`iat`, `nonce`,
  `sub`). Identity, roles and `email_verified` are read from that validated
  token; there is no userinfo call.
- **Matching**: IAM subject (usermeta `jengo_iam_sub`) first, then e-mail, and
  only when `email_verified === true`. An account linked to another subject is
  never taken over. Auto-provisioning is **off by default**, needs a mapped IAM
  role for the site, and never defaults to administrator.
- **Roles**: optional `iam_role = wp_role` map plus the convention
  `{client id}:{wp role}`; highest priority wins, re-synced on every login.
  The `jengo-admin` account is never modified.
- **Logout**: optional "also log out of IAM" sends an interactive logout on
  to IAM's `end_session_endpoint` and back to the site home page.

## Development

```
composer install
composer test        # PHPUnit: PKCE, validation, modes, matching, provisioning, role map, bypass, CLI
composer build-zip   # dist/jengo-iam-sso-<version>.zip (needs PowerShell)
```

The suite runs the real plugin classes against WordPress stubs
(`tests/stubs/wordpress.php`) and a fake IAM (`tests/support/FakeIam.php`: RSA
keys, discovery, JWKS, a token endpoint that really checks client credentials,
`redirect_uri` and the PKCE verifier). Keep plugin code PHP 7.4 compatible
(a test greps for PHP 8-only syntax).

Release checklist: bump the version in the plugin header, `JENGO_IAM_SSO_VERSION`
and `readme.txt` (a test checks they agree), add a changelog entry, `composer test`,
`composer build-zip`.

## Files

- `jengo-iam-sso.php`: bootstrap, option defaults, activation (bypass secret).
- `includes/class-jengo-iam-sso-oidc-client.php`: discovery, JWKS, PKCE, token
  exchange, end-session URL.
- `includes/class-jengo-iam-sso-token-validator.php`: id_token validation.
- `includes/class-jengo-iam-sso-login.php`: modes, bypass, callback, matching,
  provisioning, role sync, logout.
- `includes/class-jengo-iam-sso-role-mapper.php`: role map and privileged-role check.
- `includes/class-jengo-iam-sso-settings.php`: settings page (`manage_options`).
- `includes/class-jengo-iam-sso-cli.php`: WP-CLI emergency commands.

## IAM side

Registered by IAM at startup (`src/IAM.API/Workers/WordPressSsoSeeder.cs`) from
configuration, see INSTALL.md section 1. IAM also advertises
`end_session_endpoint` (`/connect/logout`, clients opt in with the end_session
permission) and puts `email_verified` (a JSON boolean) in the id_token.
