# Jengo IAM SSO (WordPress plugin)

Single sign-on for WordPress via Jengo IAM — OpenID Connect authorization-code
flow with PKCE (S256). Phase 1 of JengoWork task 5804 (board 44).

Pure PHP, no composer dependencies: the plugin only uses the WordPress HTTP API
(`wp_remote_get` / `wp_remote_post`) and transients.

## Features

- **OIDC code flow + PKCE (S256)** against a configurable issuer
  (default `https://maendeleo.martiendejong.nl`). Endpoints are read from
  `{issuer}/.well-known/openid-configuration` (cached 12h in a transient) with
  configurable endpoint overrides as fallback, then issuer-relative defaults
  (`/connect/authorize`, `/connect/token`, `/connect/userinfo`).
- **Login modes** (Settings → Jengo IAM SSO):
  - `both` (default): the normal wp-login form plus a prominent
    "Log in with Jengo IAM" button.
  - `iam`: `wp-login.php` redirects straight to the IAM authorize endpoint.
    The callback, logout and postpass actions keep working, and
    `wp-login.php?jengo_sso_bypass=<secret>` shows the normal form
    (the secret is auto-generated on activation — no admin lockout).
  - `wordpress`: SSO inactive on the login form.
- **Callback** on `wp-login.php?action=jengo-iam-callback`: single-use state
  (short-lived transient holding the PKCE verifier, nonce and redirect_to),
  token exchange with `client_secret_post` + PKCE verifier, then
  `/connect/userinfo` as the **source of truth** for `sub`, `email`, `name`
  and the roles claim. Nonce and subject consistency are checked against the
  id_token when present.
- **User matching**: (1) WP user with usermeta `jengo_iam_sub` equal to the IAM
  subject, (2) WP user by email; the subject is stored in usermeta afterwards.
  Optional auto-provisioning (login = sanitized email local part, deduplicated).
- **Role mapping**: IAM roles named
  `{client id}:administrator|editor|author|contributor|subscriber` map to the
  corresponding WP role. With role sync on (default) the WP role is set from
  IAM on every SSO login. A user with **no** mapped IAM role is refused
  auto-provisioning with "no access granted in Jengo IAM"; a pre-existing WP
  user keeps their current role. The `jengo-admin` account is never touched.

## Installation

1. Copy `jengo-iam-sso/` into `wp-content/plugins/` and activate it
   (activation generates the emergency bypass secret).
2. Go to **Settings → Jengo IAM SSO** and configure issuer, client ID
   (default `wordpress-test-prospergenics`) and the client secret.
3. Keep login mode on `both` until the flow is verified end-to-end.

## IAM configuration (what the IAM side of this PR provides)

For `test.prospergenics.com` everything is seeded by the IAM `DatabaseSeeder`:

1. **Client secret**: set the IAM configuration key
   `Clients:WordPressTestProspergenics:Secret` (e.g. via appsettings
   overrides/environment on the IAM host — never committed to git). Without it
   the client is not seeded (a warning is logged).
2. On IAM startup the seeder registers OAuth client
   `wordpress-test-prospergenics` — confidential, authorization code + refresh
   grant, PKCE required, scopes `openid profile email roles`, redirect URI
   exactly:
   `https://test.prospergenics.com/wp-login.php?action=jengo-iam-callback`
3. The seeder also creates the app-role catalog
   (`wordpress-test-prospergenics:administrator|editor|author|subscriber`,
   category `app:wordpress-test-prospergenics`). Seeding the catalog activates
   IAM's app-role gate: only users holding at least one of these roles can
   sign in to the app at all.
4. Grant roles per user in the IAM **access matrix** admin UI (the app column
   comes from the `AccessMatrix` manifest in `appsettings.json`; base access =
   `wordpress-test-prospergenics:subscriber`, with administrator/editor/author
   as fine-grained permissions).

For any **other** WordPress site, register an equivalent IAM client with that
site's `https://SITE/wp-login.php?action=jengo-iam-callback` redirect URI and
register its role catalog via `POST /api/app-roles/register`.

## Testing the flow

1. Seed the client (set the secret config key, restart IAM once — or create
   the client via the IAM OAuth clients admin UI).
2. In the IAM access matrix, grant your test user e.g.
   `wordpress-test-prospergenics:editor`.
3. Configure the plugin, click "Log in with Jengo IAM", authenticate at IAM,
   and verify: you land back in wp-admin, a WP user exists with usermeta
   `jengo_iam_sub`, and the WP role matches the IAM role.
4. Revoke the role in IAM and verify a fresh login is refused.

## Limitations (Phase 1)

- **No logout synchronisation**: logging out of WordPress does not end the IAM
  session (front-channel logout is Phase 2; see the TODO in
  `jengo-iam-sso.php`).
- No per-site user-mapping admin page yet (Phase 2).
- The id_token signature is not verified locally; the plugin relies on the
  direct TLS channel to the token endpoint and treats the userinfo response as
  the source of truth (standard for confidential code-flow clients without a
  JOSE library).

## Files

- `jengo-iam-sso.php` — bootstrap, options, activation (bypass secret).
- `includes/class-jengo-iam-sso-oidc-client.php` — discovery, PKCE, token
  exchange, userinfo.
- `includes/class-jengo-iam-sso-login.php` — login modes, callback, user
  matching/provisioning, role sync.
- `includes/class-jengo-iam-sso-settings.php` — settings page
  (`manage_options`).
