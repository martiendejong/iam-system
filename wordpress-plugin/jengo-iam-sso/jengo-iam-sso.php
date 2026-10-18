<?php
/**
 * Plugin Name:       Jengo IAM SSO
 * Plugin URI:        https://github.com/martiendejong/iam-system
 * Description:       Single sign-on for WordPress via Jengo IAM (OpenID Connect authorization-code flow with PKCE). Phase 1 of JengoWork task 5804.
 * Version:           1.0.0
 * Requires at least: 5.8
 * Requires PHP:      7.4
 * Author:            Jengo
 * License:           Proprietary
 * Text Domain:       jengo-iam-sso
 *
 * No composer dependencies: pure PHP on top of the WordPress HTTP API
 * (wp_remote_get / wp_remote_post) and the transients API.
 */

if ( ! defined( 'ABSPATH' ) ) {
	exit;
}

define( 'JENGO_IAM_SSO_VERSION', '1.0.0' );
define( 'JENGO_IAM_SSO_OPTION', 'jengo_iam_sso_options' );
define( 'JENGO_IAM_SSO_DIR', plugin_dir_path( __FILE__ ) );

require_once JENGO_IAM_SSO_DIR . 'includes/class-jengo-iam-sso-oidc-client.php';
require_once JENGO_IAM_SSO_DIR . 'includes/class-jengo-iam-sso-settings.php';
require_once JENGO_IAM_SSO_DIR . 'includes/class-jengo-iam-sso-login.php';

/**
 * Option defaults. The issuer and client id default to the Jengo IAM instance
 * and the OAuth client seeded by the IAM DatabaseSeeder for task 5804.
 */
function jengo_iam_sso_default_options() {
	return array(
		'issuer'                 => 'https://maendeleo.martiendejong.nl',
		'client_id'              => 'wordpress-test-prospergenics',
		'client_secret'          => '',
		'login_mode'             => 'both', // 'wordpress' | 'iam' | 'both'
		'auto_provision'         => 1,
		'default_role'           => 'subscriber',
		'sync_roles'             => 1,
		'bypass_secret'          => '',
		// Optional endpoint overrides, used when OIDC discovery is unreachable.
		'authorization_endpoint' => '',
		'token_endpoint'         => '',
		'userinfo_endpoint'      => '',
	);
}

/**
 * Current options merged over the defaults.
 */
function jengo_iam_sso_get_options() {
	$options = get_option( JENGO_IAM_SSO_OPTION, array() );
	if ( ! is_array( $options ) ) {
		$options = array();
	}
	return array_merge( jengo_iam_sso_default_options(), $options );
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

// TODO (Phase 2): front-channel logout — end the IAM session when the user
// logs out of WordPress (end-session endpoint + post_logout_redirect_uri),
// and honour IAM-initiated logout. Deliberately out of scope for Phase 1.
