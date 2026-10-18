=== Jengo IAM SSO ===
Contributors: jengo
Tags: sso, openid-connect, oidc, login, single sign-on
Requires at least: 5.8
Tested up to: 6.7
Requires PHP: 7.4
Stable tag: 1.1.0
License: Proprietary

Single sign-on for WordPress via Jengo IAM (OpenID Connect authorization-code flow with PKCE). Login mode: WordPress only, Jengo IAM only, or Both.

== Description ==

Lets users sign in to WordPress with their Jengo IAM account.

* OIDC authorization-code flow with PKCE (S256), client_secret_post token exchange. Endpoints come from the IAM discovery document; there are no guessed fallback URLs.
* No login without a validated id_token: RS256 signature checked against the IAM JWKS, plus issuer, audience, expiry and nonce.
* The OAuth state is single-use and bound to the browser that started the login (HttpOnly cookie), which stops login-CSRF.
* Three login modes: WordPress only (no button), Jengo IAM only (wp-login.php redirects to IAM and password logins are refused), or Both (default: the normal form plus a "Log in with Jengo IAM" button).
* IAM-only mode cannot lock you out: secret bypass URL, wp-config.php constant, WP-CLI, and an automatic fallback to the form when IAM is unreachable.
* An account is matched by IAM subject first, then by e-mail address, and only when IAM reports email_verified = true. Optional auto-provisioning (off by default); the default role can never be administrator.
* IAM-role to WordPress-role map (optional map lines plus the "{client id}:{wp role}" naming convention), re-synced on every login.
* Optional: also end the IAM session when the user logs out of WordPress (needs an IAM with an end_session endpoint).
* No composer dependencies at runtime; needs the OpenSSL PHP extension.

See INSTALL.md in the plugin folder for the install guide.

== Installation ==

1. Upload the plugin zip (Plugins, Add New, Upload) or copy the jengo-iam-sso folder to wp-content/plugins/.
2. Activate it (this generates the emergency bypass secret).
3. Have the IAM administrator register an OAuth client for this site (redirect URI https://YOUR-SITE/wp-login.php?action=jengo-iam-callback) and give you the client id and secret.
4. Settings, Jengo IAM SSO: fill in issuer, client id and secret, save. Check that the page says "IAM connection: OK".
5. Test with login mode "Both" before switching to "IAM only". Note the emergency bypass URL on the settings page.

== Frequently Asked Questions ==

= I set login mode to "IAM only" and cannot get in. =

Use the emergency bypass URL from the settings page (wp-login.php?jengo_sso_bypass=YOUR-SECRET). Without it: add define( 'JENGO_IAM_SSO_BYPASS', true ); to wp-config.php, or run "wp jengo-iam-sso set-mode wordpress". If IAM itself is down the normal form is shown automatically.

= Why does a new user get "no access granted in Jengo IAM"? =

Auto-provisioning only creates accounts for users who hold a mapped role in IAM for this site. Grant one in the IAM access matrix and try again.

= Why is my e-mail address refused? =

An account is only matched or created from an e-mail address that Jengo IAM reports as verified.

== Changelog ==

= 1.1.0 =
* Security: the id_token is mandatory and fully validated (signature against the IAM JWKS, iss, aud, exp, nonce, sub); login by e-mail requires email_verified = true; the OAuth state is bound to the browser.
* IAM-only mode: signed-out page no longer loops back into IAM, password logins are refused, automatic fallback to the form when IAM is unreachable, wp-config constant and WP-CLI bypass.
* Auto-provision is off by default and the default role can never be administrator.
* New: IAM-role to WordPress-role map, optional "also log out of IAM", client secret via wp-config.php, connection check on the settings page.
* Removed: guessed endpoint fallbacks and endpoint override fields (discovery is required).

= 1.0.0 =
* Initial release: OIDC login with PKCE, login modes, auto-provisioning, role sync, emergency bypass.
