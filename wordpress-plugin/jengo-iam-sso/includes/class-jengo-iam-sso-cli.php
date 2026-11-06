<?php
/**
 * WP-CLI commands: the server-side emergency route when nobody can reach the
 * login form (IAM-only mode with IAM misconfigured or down, bypass URL lost).
 *
 *   wp jengo-iam-sso status
 *   wp jengo-iam-sso set-mode wordpress|iam|both
 *   wp jengo-iam-sso bypass-url
 *   wp jengo-iam-sso regenerate-bypass
 *
 * Shell access to the server already means full control of the site, so these
 * commands deliberately need nothing else; they never print the client secret.
 */

if ( ! defined( 'ABSPATH' ) ) {
	exit;
}

class Jengo_IAM_SSO_CLI {

	const MODES = array( 'wordpress', 'iam', 'both' );

	public static function register() {
		if ( defined( 'WP_CLI' ) && WP_CLI && class_exists( 'WP_CLI' ) ) {
			WP_CLI::add_command( 'jengo-iam-sso', 'Jengo_IAM_SSO_CLI' );
		}
	}

	/**
	 * Show login mode, configuration state and IAM reachability.
	 *
	 * @subcommand status
	 */
	public function status( $args = array(), $assoc_args = array() ) {
		$options = jengo_iam_sso_get_options();
		$oidc    = new Jengo_IAM_SSO_OIDC_Client( $options );
		$disco   = $oidc->get_discovery();

		WP_CLI::line( 'login mode:     ' . $options['login_mode'] );
		WP_CLI::line( 'issuer:         ' . $options['issuer'] );
		WP_CLI::line( 'client id:      ' . ( '' !== $options['client_id'] ? $options['client_id'] : '(not set)' ) );
		WP_CLI::line( 'client secret:  ' . ( '' !== $options['client_secret'] ? 'set' : '(not set)' ) );
		WP_CLI::line( 'auto-provision: ' . ( ! empty( $options['auto_provision'] ) ? 'on' : 'off' ) );
		WP_CLI::line( 'IAM discovery:  ' . ( is_wp_error( $disco ) ? 'FAILED (' . $disco->get_error_message() . ')' : 'ok' ) );
	}

	/**
	 * Set the login mode. "wordpress" is the safe emergency choice: plain
	 * WordPress login form, no IAM button.
	 *
	 * ## OPTIONS
	 *
	 * <mode>
	 * : wordpress, iam or both.
	 *
	 * @subcommand set-mode
	 */
	public function set_mode( $args = array(), $assoc_args = array() ) {
		$mode = isset( $args[0] ) ? strtolower( (string) $args[0] ) : '';
		if ( ! in_array( $mode, self::MODES, true ) ) {
			WP_CLI::error( 'Mode must be one of: ' . implode( ', ', self::MODES ) );
			return;
		}

		$options               = jengo_iam_sso_get_options();
		$options['login_mode'] = $mode;
		if ( 'iam' === $mode && empty( $options['bypass_secret'] ) ) {
			$options['bypass_secret'] = bin2hex( random_bytes( 20 ) );
		}
		update_option( JENGO_IAM_SSO_OPTION, $options, false );

		WP_CLI::success( 'Login mode is now "' . $mode . '".' );
	}

	/**
	 * Print the emergency bypass URL (shows the WordPress form in IAM-only mode).
	 *
	 * @subcommand bypass-url
	 */
	public function bypass_url( $args = array(), $assoc_args = array() ) {
		$options = jengo_iam_sso_get_options();
		if ( empty( $options['bypass_secret'] ) ) {
			WP_CLI::error( 'No bypass secret yet. Run: wp jengo-iam-sso regenerate-bypass' );
			return;
		}
		WP_CLI::line( add_query_arg( 'jengo_sso_bypass', $options['bypass_secret'], wp_login_url() ) );
	}

	/**
	 * Generate a new emergency bypass secret (invalidates the old URL).
	 *
	 * @subcommand regenerate-bypass
	 */
	public function regenerate_bypass( $args = array(), $assoc_args = array() ) {
		$options                  = jengo_iam_sso_get_options();
		$options['bypass_secret'] = bin2hex( random_bytes( 20 ) );
		update_option( JENGO_IAM_SSO_OPTION, $options, false );
		WP_CLI::success( 'New bypass secret stored. Run "wp jengo-iam-sso bypass-url" to see the URL.' );
	}
}
