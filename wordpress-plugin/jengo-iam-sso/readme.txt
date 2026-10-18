=== Jengo IAM SSO ===
Contributors: jengo
Tags: sso, openid-connect, oidc, login, single sign-on
Requires at least: 5.8
Tested up to: 6.7
Requires PHP: 7.4
Stable tag: 1.0.0
License: Proprietary

Single sign-on for WordPress via Jengo IAM (OpenID Connect authorization-code flow with PKCE).

== Description ==

Lets users sign in to WordPress with their Jengo IAM account (issuer: https://maendeleo.martiendejong.nl by default).

* OIDC authorization-code flow with PKCE (S256), client_secret_post token exchange.
* Endpoints via OIDC discovery with transient caching; configurable fallbacks.
* Three login modes: WordPress only, IAM only (with an emergency bypass URL against admin lockout), or both (default: the normal form plus a "Log in with Jengo IAM" button).
* User matching by IAM subject (usermeta jengo_iam_sub) first, then by email; optional auto-provisioning of new users.
* Role mapping: IAM roles named "{client id}:administrator|editor|author|contributor|subscriber" map to the matching WordPress role, optionally re-synced on every login. Users with no mapped IAM role are refused auto-provisioning ("no access granted in Jengo IAM"); pre-existing WordPress users keep their role. The 'jengo-admin' account is never modified.
* No composer dependencies; WordPress HTTP API only.

Logout synchronisation (front-channel logout) is planned for Phase 2.

== Installation ==

1. Copy the jengo-iam-sso folder to wp-content/plugins/.
2. Activate the plugin (this generates the emergency bypass secret).
3. In Jengo IAM, make sure the OAuth client exists with redirect URI
   https://YOUR-SITE/wp-login.php?action=jengo-iam-callback
   (for test.prospergenics.com this is seeded by the IAM DatabaseSeeder as client
   "wordpress-test-prospergenics" when Clients:WordPressTestProspergenics:Secret is configured).
4. Grant users a role for the app in the IAM access matrix (e.g. wordpress-test-prospergenics:editor).
5. In WordPress go to Settings → Jengo IAM SSO, fill in issuer, client ID and client secret, and save.
6. Test the flow with login mode "both" before switching to "IAM only". Note the emergency bypass URL shown on the settings page.

== Frequently Asked Questions ==

= I set login mode to "IAM only" and IAM is down. How do I get in? =

Use the emergency bypass URL from the settings page:
wp-login.php?jengo_sso_bypass=YOUR-SECRET — it shows the normal WordPress login form.

= Why does a new user get "no access granted in Jengo IAM"? =

Auto-provisioning only creates accounts for users who hold at least one mapped
role ("{client id}:administrator/editor/author/contributor/subscriber") in IAM.
Grant one in the IAM access matrix and try again.

== Changelog ==

= 1.0.0 =
* Initial release (Phase 1, JengoWork task 5804): OIDC login with PKCE, login modes, auto-provisioning, role sync, emergency bypass.
