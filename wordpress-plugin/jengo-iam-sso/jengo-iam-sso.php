<?php
/**
 * Plugin Name:       Jengo IAM SSO
 * Plugin URI:        https://github.com/martiendejong/iam-system
 * Description:       Single sign-on for WordPress via Jengo IAM (OpenID Connect authorization-code flow with PKCE). Login mode: WordPress only, Jengo IAM only, or Both.
 * Version:           1.1.0
 * Requires at least: 5.8
 * Requires PHP:      7.4
 * Author:            Jengo
 * License:           Proprietary
 * Text Domain:       jengo-iam-sso
 *
 * No composer dependencies at runtime: pure PHP on top of the WordPress HTTP
 * API, the transients API and the OpenSSL extension (id_token signature check).
 */

if ( ! defined( 'ABSPATH' ) ) {
	exit;
}

define( 'JENGO_IAM_SSO_VERSION', '1.1.0' );
define( 'JENGO_IAM_SSO_OPTION', 'jengo_iam_sso_options' );
define( 'JENGO_IAM_SSO_DIR', plugin_dir_path( __FILE__ ) );

require_once JENGO_IAM_SSO_DIR . 'includes/class-jengo-iam-sso-token-validator.php';
require_once JENGO_IAM_SSO_DIR . 'includes/class-jengo-iam-sso-role-mapper.php';
require_once JENGO_IAM_SSO_DIR . 'includes/class-jengo-iam-sso-oidc-client.php';
require_once JENGO_IAM_SSO_DIR . 'includes/class-jengo-iam-sso-settings.php';
require_once JENGO_IAM_SSO_DIR . 'includes/class-jengo-iam-sso-login.php';
require_once JENGO_IAM_SSO_DIR . 'includes/class-jengo-iam-sso-cli.php';

/**
 * Option defaults.
 *
 * - issuer: the Jengo IAM instance; must equal the "issuer" in its discovery
 *   document (trailing slash ignored).
 * - client_id: empty on purpose, every site registers its own OAuth client.
 * - login_mode: 'wordpress' | 'iam' | 'both' (default 'both').
 * - auto_provision: OFF by default; when on, only IAM users that hold a mapped
 *   role for this site get an account.
 * - role_map: optional extra "iam_role = wp_role" lines (one per line).
 * - logout_iam: also end the IAM session when the user logs out of WordPress.
 */
function jengo_iam_sso_default_options() {
	return array(
		'issuer'         => 'https://maendeleo.martiendejong.nl/auth',
		'client_id'      => '',
		'client_secret'  => '',
		'login_mode'     => 'both',
		'auto_provision' => 0,
		'default_role'   => 'subscriber',
		'sync_roles'     => 1,
		'role_map'       => '',
		'logout_iam'     => 0,
		'bypass_secret'  => '',
	);
}

/**
 * Current options merged over the defaults. A client secret defined in
 * wp-config.php (JENGO_IAM_SSO_CLIENT_SECRET) wins over the stored one, so the
 * secret never has to live in the database.
 */
function jengo_iam_sso_get_options() {
	$options = get_option( JENGO_IAM_SSO_OPTION, array() );
	if ( ! is_array( $options ) ) {
		$options = array();
	}
	$options = array_merge( jengo_iam_sso_default_options(), $options );

	if ( defined( 'JENGO_IAM_SSO_CLIENT_SECRET' ) && is_string( JENGO_IAM_SSO_CLIENT_SECRET ) && '' !== JENGO_IAM_SSO_CLIENT_SECRET ) {
		$options['client_secret'] = JENGO_IAM_SSO_CLIENT_SECRET;
	}

	return $options;
}

/**
 * Set a cookie with the attributes the plugin always wants (HttpOnly,
 * SameSite=Lax so it survives the redirect back from IAM, Secure over https).
 * Declared with a function_exists guard so the unit tests can record cookies.
 */
if ( ! function_exists( 'jengo_iam_sso_set_cookie' ) ) {
	function jengo_iam_sso_set_cookie( $name, $value, $expires ) {
		setcookie(
			$name,
			$value,
			array(
				'expires'  => $expires,
				'path'     => defined( 'COOKIEPATH' ) ? COOKIEPATH : '/',
				'secure'   => is_ssl(),
				'httponly' => true,
				'samesite' => 'Lax',
			)
		);
	}
}

/**
 * Activation: seed defaults and auto-generate the emergency bypass secret.
 * The bypass secret lets an administrator reach the normal WordPress login
 * form even when login mode is 'iam' (prevents admin lockout):
 * wp-login.php?jengo_sso_bypass=<secret>
 */
function jengo_iam_sso_activate() {
	$options = jengo_iam_sso_get_options();
	if ( empty( $options['bypass_secret'] ) ) {
		$options['bypass_secret'] = bin2hex( random_bytes( 20 ) );
	}
	update_option( JENGO_IAM_SSO_OPTION, $options, false );
}
register_activation_hook( __FILE__, 'jengo_iam_sso_activate' );

/**
 * Safety net: if the plugin files were dropped in place without the activation
 * hook running (manual deploy), still make sure a bypass secret exists before
 * anyone can switch login mode to 'iam'.
 */
function jengo_iam_sso_ensure_bypass_secret() {
	$options = jengo_iam_sso_get_options();
	if ( empty( $options['bypass_secret'] ) ) {
		$options['bypass_secret'] = bin2hex( random_bytes( 20 ) );
		update_option( JENGO_IAM_SSO_OPTION, $options, false );
	}
}
add_action( 'admin_init', 'jengo_iam_sso_ensure_bypass_secret', 1 );

// Wire up the plugin.
new Jengo_IAM_SSO_Settings();
new Jengo_IAM_SSO_Login();
Jengo_IAM_SSO_CLI::register();
