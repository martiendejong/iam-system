<?php

/**
 * Login modes (WordPress / IAM / Both) and the guarantees around IAM-only mode:
 * emergency bypass (secret URL, wp-config constant, WP-CLI), fallback to the
 * form when IAM is unreachable, no redirect loop after logout.
 */
class ModeAndBypassTest extends JengoTestCase {

	private function login_form_message( Jengo_IAM_SSO_Login $login ) {
		return $login->render_login_button( '' );
	}

	private function enter_wp_login( Jengo_IAM_SSO_Login $login, array $get = array(), $method = 'GET' ) {
		$_GET                      = $get;
		$_REQUEST                  = $get;
		$_SERVER['REQUEST_METHOD'] = $method;
		$login->maybe_force_iam_login();
	}

	/* ---- defaults ------------------------------------------------------------- */

	public function test_defaults_are_mode_both_auto_provision_off_and_no_client_preset() {
		$defaults = jengo_iam_sso_default_options();

		$this->assertSame( 'both', $defaults['login_mode'] );
		$this->assertSame( 0, $defaults['auto_provision'] );
		$this->assertSame( 0, $defaults['logout_iam'] );
		$this->assertSame( '', $defaults['client_id'] );
		$this->assertSame( '', $defaults['client_secret'] );
		$this->assertNotSame( 'administrator', $defaults['default_role'] );
	}

	public function test_an_unknown_stored_mode_falls_back_to_both() {
		$this->configure( array( 'login_mode' => 'something-else' ) );

		$this->assertSame( 'both', $this->login()->effective_login_mode() );
	}

	/* ---- mode: both ------------------------------------------------------------ */

	public function test_both_shows_the_iam_button_and_leaves_the_form_alone() {
		$login = $this->login();

		$this->enter_wp_login( $login ); // must not redirect
		$html = $this->login_form_message( $login );

		$this->assertStringContainsString( 'Log in with Jengo IAM', $html );
		$this->assertStringContainsString( 'action=jengo-iam-auth', $html );
		$this->assertStringContainsString( 'or use your WordPress credentials below', $html );
		$this->assertSame( '', $this->capture( array( $login, 'maybe_hide_password_form' ) ) );
	}

	public function test_both_does_not_refuse_password_logins() {
		$login = $this->login();
		$this->enter_wp_login( $login, array(), 'POST' );
		$user = Jengo_Test_World::add_user( 'alice', 'a@example.com' );

		$this->assertSame( $user, $login->block_password_login( $user, 'alice', 'pw' ) );
	}

	public function test_the_button_keeps_redirect_to() {
		$_REQUEST = array( 'redirect_to' => 'https://test.prospergenics.com/wp-admin/edit.php' );

		$html = $this->login_form_message( $this->login() );

		$this->assertStringContainsString( 'redirect_to=', $html );
	}

	public function test_the_button_only_appears_on_the_login_screen_not_on_lost_password() {
		$_REQUEST = array( 'action' => 'lostpassword' );

		$this->assertStringNotContainsString( 'Log in with Jengo IAM', $this->login_form_message( $this->login() ) );
	}

	/* ---- mode: WordPress only ------------------------------------------------------ */

	public function test_wordpress_only_has_no_button_no_redirect_and_no_blocking() {
		$this->configure( array( 'login_mode' => 'wordpress' ) );
		$login = $this->login();

		$this->enter_wp_login( $login ); // must not redirect
		$user = Jengo_Test_World::add_user( 'alice', 'a@example.com' );

		$this->assertSame( '', $this->login_form_message( $login ) );
		$this->assertSame( $user, $login->block_password_login( $user, 'alice', 'pw' ) );
		$this->assertSame( array(), Jengo_Test_World::$http_log, 'No IAM traffic at all in WordPress-only mode' );
	}

	/* ---- mode: IAM only ------------------------------------------------------------- */

	public function test_iam_only_redirects_wp_login_to_the_iam_authorize_endpoint() {
		$this->configure( array( 'login_mode' => 'iam' ) );
		$login = $this->login();

		$location = $this->redirect_of( function () use ( $login ) {
			$this->enter_wp_login( $login );
		} );

		$this->assertStringStartsWith( 'https://iam.test/connect/authorize?', $location );
		$this->assertNotEmpty( Jengo_Test_World::cookie_value( Jengo_IAM_SSO_Login::STATE_COOKIE ) );
	}

	public function test_iam_only_refuses_password_logins_and_hides_the_form() {
		$this->configure( array( 'login_mode' => 'iam' ) );
		$login = $this->login();
		$this->enter_wp_login( $login, array( 'loggedout' => 'true' ) ); // signed-out page: shown, not redirected
		$user = Jengo_Test_World::add_user( 'alice', 'a@example.com' );

		$result = $login->block_password_login( $user, 'alice', 'pw' );

		$this->assertInstanceOf( WP_Error::class, $result );
		$this->assertSame( 'jengo_iam_sso_iam_only', $result->get_error_code() );
		$this->assertStringContainsString( '#loginform', $this->capture( array( $login, 'maybe_hide_password_form' ) ) );
		$this->assertStringNotContainsString( 'or use your WordPress credentials', $this->login_form_message( $login ) );
	}

	public function test_iam_only_post_to_wp_login_is_not_redirected_but_the_password_is_refused() {
		$this->configure( array( 'login_mode' => 'iam' ) );
		$login = $this->login();
		$this->enter_wp_login( $login, array(), 'POST' ); // no redirect
		$user = Jengo_Test_World::add_user( 'alice', 'a@example.com' );

		$this->assertInstanceOf( WP_Error::class, $login->block_password_login( $user, 'alice', 'pw' ) );
	}

	public function test_iam_only_blocking_ignores_non_user_authenticate_results() {
		$this->configure( array( 'login_mode' => 'iam' ) );
		$login = $this->login();
		$this->enter_wp_login( $login, array( 'loggedout' => 'true' ) );

		$this->assertNull( $login->block_password_login( null, '', '' ) );
		$error = new WP_Error( 'x' );
		$this->assertSame( $error, $login->block_password_login( $error, '', '' ) );
	}

	public function test_iam_only_does_not_redirect_the_signed_out_page_so_there_is_no_silent_re_login_loop() {
		$this->configure( array( 'login_mode' => 'iam' ) );
		$login = $this->login();

		$this->enter_wp_login( $login, array( 'loggedout' => 'true' ) ); // would throw Jengo_Test_Redirect if it redirected

		$this->assertStringContainsString( 'Log in with Jengo IAM', $this->login_form_message( $login ) );
	}

	public function test_iam_only_lets_logout_postpass_and_the_plugins_own_actions_through() {
		$this->configure( array( 'login_mode' => 'iam' ) );
		$login = $this->login();

		foreach ( array( 'logout', 'postpass', 'jengo-iam-callback', 'jengo-iam-auth' ) as $action ) {
			$this->enter_wp_login( $login, array( 'action' => $action ) ); // none may redirect to IAM here
		}
		$this->addToAssertionCount( 1 ); // none of the four actions redirected to IAM
	}

	/* ---- bypass: secret URL ---------------------------------------------------------- */

	public function test_the_bypass_secret_shows_the_normal_form_in_iam_only_mode() {
		$this->configure( array( 'login_mode' => 'iam' ) );
		$login = $this->login();

		$this->enter_wp_login( $login, array( 'jengo_sso_bypass' => 'bypass-secret-1234567890' ) ); // no redirect

		$user = Jengo_Test_World::add_user( 'admin', 'a@example.com', array( 'administrator' ) );
		$this->assertSame( $user, $login->block_password_login( $user, 'admin', 'pw' ), 'Password login works under bypass' );
		$this->assertSame( '', $this->capture( array( $login, 'maybe_hide_password_form' ) ) );
		$this->assertNotNull( Jengo_Test_World::cookie_value( Jengo_IAM_SSO_Login::BYPASS_COOKIE ) );
	}

	public function test_the_bypass_cookie_keeps_the_form_available_for_the_follow_up_post() {
		$this->configure( array( 'login_mode' => 'iam' ) );
		$first = $this->login();
		$this->enter_wp_login( $first, array( 'jengo_sso_bypass' => 'bypass-secret-1234567890' ) );
		$cookie = Jengo_Test_World::cookie_value( Jengo_IAM_SSO_Login::BYPASS_COOKIE );

		$_COOKIE = array( Jengo_IAM_SSO_Login::BYPASS_COOKIE => $cookie );
		$second  = $this->login();
		$this->enter_wp_login( $second, array(), 'POST' );
		$_COOKIE = array( Jengo_IAM_SSO_Login::BYPASS_COOKIE => $cookie );
		$this->enter_wp_login( $this->login(), array() ); // GET with cookie: no redirect either

		$user = Jengo_Test_World::add_user( 'admin', 'a@example.com' );
		$this->assertSame( $user, $second->block_password_login( $user, 'admin', 'pw' ) );
	}

	public function test_a_wrong_bypass_secret_does_not_bypass() {
		$this->configure( array( 'login_mode' => 'iam' ) );
		$login = $this->login();

		$location = $this->redirect_of( function () use ( $login ) {
			$this->enter_wp_login( $login, array( 'jengo_sso_bypass' => 'guess' ) );
		} );

		$this->assertStringStartsWith( 'https://iam.test/connect/authorize', $location );
		$this->assertNull( Jengo_Test_World::cookie_value( Jengo_IAM_SSO_Login::BYPASS_COOKIE ) );
	}

	public function test_a_forged_bypass_cookie_does_not_bypass() {
		$this->configure( array( 'login_mode' => 'iam' ) );
		$_COOKIE = array( Jengo_IAM_SSO_Login::BYPASS_COOKIE => 'forged' );
		$login   = $this->login();

		$this->redirect_of( function () use ( $login ) {
			$this->enter_wp_login( $login );
		} );
		$this->addToAssertionCount( 1 );
	}

	public function test_an_empty_bypass_secret_never_matches() {
		$this->configure( array( 'login_mode' => 'iam', 'bypass_secret' => '' ) );
		$login = $this->login();

		$this->redirect_of( function () use ( $login ) {
			$this->enter_wp_login( $login, array( 'jengo_sso_bypass' => '' ) );
		} );
		$this->addToAssertionCount( 1 );
	}

	/* ---- bypass: wp-config constant ---------------------------------------------------- */

	public function test_the_wp_config_constant_turns_the_plugin_off_for_logins() {
		$this->configure( array( 'login_mode' => 'iam' ) );
		$login                  = $this->login();
		$login->bypass_constant = true;

		$this->assertSame( 'wordpress', $login->effective_login_mode() );
		$this->enter_wp_login( $login ); // no redirect
		$user = Jengo_Test_World::add_user( 'admin', 'a@example.com' );
		$this->assertSame( $user, $login->block_password_login( $user, 'admin', 'pw' ) );
		$this->assertSame( '', $this->login_form_message( $login ), 'No button either' );
	}

	/* ---- bypass: WP-CLI (details in CliTest) -------------------------------------------- */

	public function test_switching_the_mode_through_the_option_takes_effect_immediately() {
		$this->configure( array( 'login_mode' => 'iam' ) );
		$login = $this->login();
		$this->assertSame( 'iam', $login->effective_login_mode() );

		$this->configure( array( 'login_mode' => 'wordpress' ) );

		$this->assertSame( 'wordpress', $login->effective_login_mode() );
	}

	/* ---- IAM unreachable: fall back to the form -------------------------------------------- */

	public function test_iam_only_falls_back_to_the_form_with_a_notice_when_iam_is_unreachable() {
		$this->configure( array( 'login_mode' => 'iam' ) );
		$this->iam->discovery_up = false;
		$login                   = $this->login();

		$this->enter_wp_login( $login ); // must NOT redirect into a dead host

		$user = Jengo_Test_World::add_user( 'admin', 'a@example.com', array( 'administrator' ) );
		$this->assertSame( $user, $login->block_password_login( $user, 'admin', 'pw' ), 'Password login must work while IAM is down' );
		$html = $this->login_form_message( $login );
		$this->assertStringContainsString( 'not reachable', $html );
		$this->assertStringNotContainsString( 'action=jengo-iam-auth', $html );
	}

	public function test_a_discovery_document_for_a_different_issuer_counts_as_unreachable() {
		$this->configure( array( 'login_mode' => 'iam' ) );
		$this->iam->issuer_in_document = 'https://evil.test/auth/';
		$login                         = $this->login();

		$this->enter_wp_login( $login ); // no redirect

		$this->assertStringContainsString( 'not reachable', $this->login_form_message( $login ) );
	}

	public function test_a_discovery_document_with_a_plain_http_endpoint_counts_as_unreachable() {
		foreach ( array( 'authorization_endpoint', 'token_endpoint', 'jwks_uri' ) as $field ) {
			Jengo_Test_World::$transients = array();
			Jengo_Test_World::route( 'GET', 'https://iam.test/auth/.well-known/openid-configuration', function () use ( $field ) {
				$doc           = $this->iam->discovery_document();
				$doc[ $field ] = 'http://iam.test/insecure';
				return array( 200, json_encode( $doc ) );
			} );

			$this->assertFalse( ( new Jengo_IAM_SSO_OIDC_Client( jengo_iam_sso_get_options() ) )->is_available(), $field );
		}
	}

	public function test_a_discovery_document_missing_a_required_endpoint_counts_as_unreachable() {
		foreach ( array( 'authorization_endpoint', 'token_endpoint', 'jwks_uri', 'issuer' ) as $field ) {
			Jengo_Test_World::$transients = array();
			Jengo_Test_World::route( 'GET', 'https://iam.test/auth/.well-known/openid-configuration', function () use ( $field ) {
				$doc = $this->iam->discovery_document();
				unset( $doc[ $field ] );
				return array( 200, json_encode( $doc ) );
			} );

			$this->assertFalse( ( new Jengo_IAM_SSO_OIDC_Client( jengo_iam_sso_get_options() ) )->is_available(), $field );
		}
	}

	public function test_an_http_issuer_setting_is_never_used() {
		$this->configure( array( 'issuer' => 'http://iam.test/auth' ) );

		$this->assertFalse( ( new Jengo_IAM_SSO_OIDC_Client( jengo_iam_sso_get_options() ) )->is_available() );
		$this->assertSame( array(), Jengo_Test_World::$http_log );
	}

	public function test_both_mode_hides_the_button_and_explains_when_iam_is_unreachable() {
		$this->iam->discovery_up = false;

		$html = $this->login_form_message( $this->login() );

		$this->assertStringNotContainsString( 'action=jengo-iam-auth', $html );
		$this->assertStringContainsString( 'not reachable', $html );
	}

	public function test_the_button_failure_page_is_a_clear_message_not_a_redirect_into_nowhere() {
		$this->iam->discovery_up = false;

		$message = $this->die_message_of( array( $this->login(), 'start_authorization' ) );

		$this->assertStringContainsString( 'not reachable', $message );
	}

	public function test_a_failed_discovery_is_cached_briefly_so_a_down_iam_does_not_slow_every_page() {
		$this->iam->discovery_up = false;
		$login                   = $this->login();

		$this->login_form_message( $login );
		$this->login_form_message( $this->login() );
		$this->login_form_message( $this->login() );

		$discovery_calls = array_filter( Jengo_Test_World::$http_log, static function ( $e ) {
			return false !== strpos( $e[1], 'openid-configuration' );
		} );
		$this->assertCount( 1, $discovery_calls );
	}

	/* ---- IAM goes down AFTER discovery was cached (review round 2 finding) ------------------------- */

	public function test_iam_only_falls_back_to_the_form_when_iam_goes_down_after_discovery_was_cached() {
		$this->configure( array( 'login_mode' => 'iam' ) );

		// IAM is healthy: wp-login.php redirects, and the discovery document gets cached (12 h).
		$location = $this->redirect_of( function () {
			$this->enter_wp_login( $this->login() );
		} );
		$this->assertStringStartsWith( 'https://iam.test/connect/authorize?', $location );

		// IAM goes down; the cached document still names its endpoints.
		$this->iam->discovery_up = false;
		Jengo_Test_World::$now_offset += Jengo_IAM_SSO_OIDC_Client::REACHABILITY_TTL + 1;

		$login = $this->login();
		$this->enter_wp_login( $login ); // must NOT redirect into the dead host

		$user = Jengo_Test_World::add_user( 'admin', 'a@example.com', array( 'administrator' ) );
		$this->assertSame( $user, $login->block_password_login( $user, 'admin', 'pw' ), 'Password login must work while IAM is down' );
		$this->assertStringContainsString( 'not reachable', $this->login_form_message( $login ) );
	}

	public function test_the_reachability_probe_is_remembered_for_a_minute_and_iam_coming_back_redirects_again() {
		$this->configure( array( 'login_mode' => 'iam' ) );

		$this->redirect_of( function () {
			$this->enter_wp_login( $this->login() );
		} );
		$this->iam->discovery_up = false;
		Jengo_Test_World::$now_offset += Jengo_IAM_SSO_OIDC_Client::REACHABILITY_TTL + 1;

		$count_discovery_gets = static function () {
			return count(
				array_filter(
					Jengo_Test_World::$http_log,
					static function ( $e ) {
						return false !== strpos( $e[1], 'openid-configuration' );
					}
				)
			);
		};

		$this->enter_wp_login( $this->login() ); // probe fails
		$after_first = $count_discovery_gets();
		$this->enter_wp_login( $this->login() ); // remembered, no new request
		$this->enter_wp_login( $this->login() );
		$this->assertSame( $after_first, $count_discovery_gets(), 'A failed probe is remembered for the cache window' );

		// IAM is back; once the remembered failure expires the redirect returns.
		$this->iam->discovery_up = true;
		Jengo_Test_World::$now_offset += Jengo_IAM_SSO_OIDC_Client::REACHABILITY_TTL + 1;
		$location = $this->redirect_of( function () {
			$this->enter_wp_login( $this->login() );
		} );
		$this->assertStringStartsWith( 'https://iam.test/connect/authorize?', $location );
	}

	/* ---- "WordPress only" and the kill switch really switch SSO off (review round 2 finding) ------- */

	public function test_wordpress_only_mode_refuses_the_sso_start_and_callback_endpoints() {
		$this->configure( array( 'login_mode' => 'wordpress' ) );

		$start = $this->die_message_of( array( $this->login(), 'start_authorization' ) );
		$this->assertStringContainsString( 'switched off', $start );

		$_GET     = array( 'code' => 'abc', 'state' => str_repeat( 'a', 32 ) );
		$_REQUEST = $_GET;
		$callback = $this->die_message_of( array( $this->login(), 'handle_callback' ) );
		$this->assertStringContainsString( 'switched off', $callback );
		$this->assertSame( array(), $this->token_posts(), 'The token endpoint is never contacted' );
	}

	public function test_a_flow_started_before_the_switch_cannot_be_completed_after_it() {
		$alice = Jengo_Test_World::add_user( 'alice', 'alice@example.com', array( 'subscriber' ) );
		$login = $this->login();
		$flow  = $this->start_flow( $login );
		$code  = $this->iam->issue_code( $flow['location'] );

		$this->configure( array( 'login_mode' => 'wordpress' ) ); // admin flips the switch mid-flow
		$result = $this->run_callback( $this->login(), $code, $flow['params']['state'], $flow['binding'] );

		$this->assertArrayHasKey( 'die', $result );
		$this->assertNoSession();
		$this->assertSame( array(), $this->token_posts() );
		$this->assertSame( array( 'subscriber' ), $alice->roles );
	}

	public function test_the_wp_config_kill_switch_refuses_a_complete_sso_login() {
		Jengo_Test_World::add_user( 'alice', 'alice@example.com', array( 'subscriber' ) );
		$login                  = $this->login();
		$login->bypass_constant = true;

		$message = $this->die_message_of( array( $login, 'start_authorization' ) );

		$this->assertStringContainsString( 'switched off', $message );
		$this->assertNoSession();
	}

	/* ---- not configured: never lock anyone out ----------------------------------------------- */

	public function test_an_unconfigured_plugin_behaves_like_wordpress_only_even_if_mode_is_iam() {
		$this->configure( array( 'login_mode' => 'iam', 'client_secret' => '' ) );
		$login = $this->login();

		$this->assertSame( 'wordpress', $login->effective_login_mode() );
		$this->enter_wp_login( $login ); // no redirect
		$this->assertSame( '', $this->login_form_message( $login ) );
	}

	public function test_the_auth_action_on_an_unconfigured_plugin_explains_instead_of_redirecting() {
		$this->configure( array( 'client_secret' => '' ) );

		$message = $this->die_message_of( array( $this->login(), 'start_authorization' ) );

		$this->assertStringContainsString( 'not configured', $message );
	}

	/* ---- helpers --------------------------------------------------------------------------------- */

	private function capture( callable $fn ) {
		ob_start();
		call_user_func( $fn );
		return (string) ob_get_clean();
	}
}
