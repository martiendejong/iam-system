# Jengo IAM SSO: install guide

Adds a "Log in with Jengo IAM" button to a WordPress site (OpenID Connect,
authorization code flow with PKCE). Per site you need two things: **a client
registration at IAM** and **the plugin settings**.

| Login mode | Behaviour |
|---|---|
| **Both** (default) | Normal WordPress form plus the IAM button. |
| **WordPress only** | No button, and the IAM sign-in endpoints refuse too: IAM login is fully off. |
| **IAM only** | `wp-login.php` redirects to IAM; password logins are refused. Emergency routes below. |

Requirements: WordPress 5.8+, PHP 7.4+ with the OpenSSL extension, outbound
HTTPS to the IAM host.

## 1. Register the site at IAM

The IAM admin API cannot create OAuth clients yet, so IAM registers them at
startup from configuration. On the IAM host add an entry to
`WordPressSso:Sites` (an untracked appsettings override, or environment
variables) and restart IAM once:

```json
"WordPressSso": {
  "Sites": [
    {
      "ClientId": "wordpress-example-com",
      "DisplayName": "WordPress (example.com)",
      "SiteUrl": "https://example.com/",
      "Secret": "<long random secret, only on the IAM host, never in git>"
    }
  ]
}
```

As environment variables: `WordPressSso__Sites__0__ClientId`,
`WordPressSso__Sites__0__SiteUrl`, `WordPressSso__Sites__0__Secret`, ...

On startup IAM then creates, if missing:

- a confidential OAuth client with PKCE required, redirect URI
  `<SiteUrl>/wp-login.php?action=jengo-iam-callback` and post-logout URI
  `<SiteUrl>/` (registered exactly, query string included);
- the app-role catalog `{ClientId}:administrator|editor|author|subscriber`
  (category `app:{ClientId}`). **Only users holding one of these roles can sign
  in to this site at all**, which is what keeps IAM users without a role out.

`test.prospergenics.com` is built in: set `Clients:WordPressTestProspergenics:Secret`
on the IAM host and restart. Nothing is registered for a site without a secret
(the IAM log says which key is missing). If the site moves to another host, add
it as a new entry; a changed `SiteUrl` does not rewrite an existing client.

Then grant users a role in the IAM **access matrix**, e.g.
`wordpress-example-com:editor`. (To get a column for the site in the matrix UI,
add an entry for it to the `AccessMatrix` section of IAM's appsettings, like the
existing `wordpress-test-prospergenics` one.)

## 2. Install the plugin

Build the zip (or take the one attached to the task): `composer build-zip`
in `wordpress-plugin/` writes `dist/jengo-iam-sso-<version>.zip`.

- WP admin: Plugins, Add New, Upload Plugin, choose the zip, Activate; or
- WP-CLI: `wp plugin install jengo-iam-sso-1.1.1.zip --activate`.

Activation generates the emergency bypass secret.

## 3. Configure (Settings, Jengo IAM SSO)

| Setting | Value |
|---|---|
| Issuer URL | `https://maendeleo.martiendejong.nl/auth` (must equal the `issuer` in `<issuer>/.well-known/openid-configuration`, trailing slash ignored) |
| Client ID / secret | From step 1. The secret is write-only; alternatively `define( 'JENGO_IAM_SSO_CLIENT_SECRET', '...' );` in wp-config.php keeps it out of the database. |
| Login mode | Start with **Both**. |
| Auto-provision | Off by default. Turn on only to let IAM users with a role for this site get an account on first login (needs a verified e-mail address). |
| Default role | For new accounts when role sync is off. Administrator and other user-management roles are not offered. |
| Role map | Optional `iam_role = wp_role` lines (first line wins). `{ClientId}:administrator|editor|author|contributor|subscriber` map automatically and rank below these lines. |
| Also log out of IAM | Ending the WordPress session also ends the IAM session (IAM must advertise `end_session_endpoint`). |

The page shows **IAM connection: OK** when the discovery document is found and
its issuer matches. Register exactly the two URIs it lists.

## 4. Test every mode

1. **Both**: log out, open `wp-login.php`: form plus button. Click the button,
   sign in at IAM, you land in wp-admin as the matching user (matched by IAM
   subject first, then by *verified* e-mail address). Check the user's role.
2. **WordPress only**: no button; the normal form works.
3. **IAM only**: `wp-login.php` goes straight to IAM. After a WordPress logout you
   see the signed-out page with only the IAM button (no redirect loop). With
   `?jengo_sso_bypass=<secret>` the normal form works.
4. Take IAM down (or set a wrong issuer) in IAM-only mode: the normal form
   appears with a notice instead of a redirect into a dead host.

Do not script `wp-login.php` against Vimexx-hosted sites; one manual login per
mode is enough.

## Emergency access (IAM-only mode, nobody can log in)

In this order of convenience:

1. **Bypass URL**: `https://SITE/wp-login.php?jengo_sso_bypass=<secret>` (shown on
   the settings page; `wp jengo-iam-sso bypass-url` prints it).
2. **wp-config.php**: add `define( 'JENGO_IAM_SSO_BYPASS', true );` to switch IAM
   sign-in off (button gone, sign-in and callback endpoints refuse) without touching
   the database. Remove it afterwards.
3. **WP-CLI**: `wp jengo-iam-sso set-mode wordpress`.
4. If IAM is unreachable the form is shown automatically. IAM is checked live on
   `wp-login.php` visits (the answer is remembered for a minute), so this also works
   when IAM goes down while its discovery document is still cached.

## What is checked on every login

Single-use `state` bound to the browser by an HttpOnly cookie; PKCE; the
`id_token` is mandatory and must have a valid RS256 signature from the IAM
JWKS, the right issuer and audience, be unexpired, and carry the nonce of this
login; an account is only matched by e-mail (or created) when `email_verified`
is `true`; an account already linked to a different IAM subject is never taken
over. Failure screens never show tokens or the client secret and nothing is
written to a log.

## Troubleshooting

| Symptom | Likely cause |
|---|---|
| "IAM connection: not reachable or invalid (issuer_mismatch)" | Issuer URL differs from the discovery document's `issuer`. Use the value above. |
| IAM shows "invalid redirect_uri" | The client was registered for another host or path; add a site entry for this exact `SiteUrl`. |
| IAM shows "No access to this application" | The user holds no `{ClientId}:...` role: grant one in the access matrix. |
| "Your e-mail address is not verified in Jengo IAM" | Confirm the e-mail address in IAM first. |
| "This WordPress account is already linked to a different Jengo IAM identity" | The account was linked to another IAM user; delete usermeta `jengo_iam_sub` for it if the link is wrong. |
| Logout does not end the IAM session | Option off, IAM too old to advertise `end_session_endpoint`, or the session did not come from SSO. |
