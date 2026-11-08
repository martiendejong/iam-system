<?php

/**
 * The OIDC callback: state, browser binding, id_token validation, e-mail
 * verification, user matching, provisioning, role sync and the redirect.
 *
 * Every test drives the real start_authorization() + handle_callback() code
 * against the fake IAM. "No session" means wp_set_auth_cookie() never ran.
 */
class CallbackTest extends JengoTestCase {

	/* ---- happy path ----------------------------------------------------- */

	public function test_login_by_verified_email_links_the_subject_syncs_the_role_and_lands_in_wp_admin() {
		$alice = Jengo_Test_World::add_user( 'alice', 'alice@example.com', array( 'subscriber' ) );

		$result = $this->sso_login();

		$this->assertSame( 'https://test.prospergenics.com/wp-admin/', $result['redirect'] );
		$this->assertSessionIssuedFor( $alice->ID );
		$this->assertSame( 'sub-alice', get_user_meta( $alice->ID, Jengo_IAM_SSO_Login::SUB_META_KEY, true ) );
		$this->assertSame( array( 'editor' ), $alice->roles );
		$this->assertNotNull( Jengo_Test_World::cookie_value( Jengo_IAM_SSO_Login::SSO_SESSION_COOKIE ) );
	}

	public function test_the_binding_cookie_is_cleared_after_the_callback() {
		Jengo_Test_World::add_user( 'alice', 'alice@example.com' );

		$this->sso_login();

		$this->assertSame( '', Jengo_Test_World::cookie_value( Jengo_IAM_SSO_Login::STATE_COOKIE ) );
	}

	public function test_redirect_to_inside_the_site_is_kept_and_outside_the_site_is_dropped() {
		Jengo_Test_World::add_user( 'alice', 'alice@example.com' );
		$inside = $this->sso_login( array(), null, array( 'redirect_to' => 'https://test.prospergenics.com/wp-admin/plugins.php' ) );
		$this->assertSame( 'https://test.prospergenics.com/wp-admin/plugins.php', $inside['redirect'] );

		Jengo_Test_World::$auth_cookie = null;
		$outside = $this->sso_login( array(), null, array( 'redirect_to' => 'https://evil.example/phish' ) );
		$this->assertSame( 'https://test.prospergenics.com/wp-admin/', $outside['redirect'] );
	}

	/* ---- state ------------------------------------------------------------ */

	public function test_a_state_value_can_only_be_used_once() {
		Jengo_Test_World::add_user( 'alice', 'alice@example.com' );
		$login = $this->login();
		$flow  = $this->start_flow( $login );
		$code  = $this->iam->issue_code( $flow['location'] );

		$first = $this->run_callback( $login, $code, $flow['params']['state'], $flow['binding'] );
		Jengo_Test_World::$auth_cookie = null;
		// Replay the same state with a perfectly valid fresh code: the plugin itself must refuse it
		// (IAM's own single-use code check must not be what saves us).
		$fresh_code = $this->iam->issue_code( $flow['location'] );
		$second     = $this->run_callback( $login, $fresh_code, $flow['params']['state'], $flow['binding'] );

		$this->assertArrayHasKey( 'redirect', $first );
		$this->assertArrayHasKey( 'die', $second );
		$this->assertNoSession();
	}

	public function test_a_forged_or_unknown_state_never_reaches_the_token_endpoint() {
		Jengo_Test_World::add_user( 'alice', 'alice@example.com' );
		$login = $this->login();
		$flow  = $this->start_flow( $login );
		$code  = $this->iam->issue_code( $flow['location'] );

		$result = $this->run_callback( $login, $code, str_repeat( 'a', 32 ), $flow['binding'] );

		$this->assertArrayHasKey( 'die', $result );
		$this->assertSame( array(), $this->token_posts() );
		$this->assertNoSession();
	}

	public function test_a_malformed_state_is_refused() {
		$login = $this->login();
		$flow  = $this->start_flow( $login );
		$code  = $this->iam->issue_code( $flow['location'] );

		foreach ( array( '', 'short', str_repeat( 'Z', 32 ), $flow['params']['state'] . 'x' ) as $state ) {
			$result = $this->run_callback( $login, $code, $state, $flow['binding'] );
			$this->assertArrayHasKey( 'die', $result );
		}
		$this->assertSame( array(), $this->token_posts() );
	}

	public function test_an_expired_state_is_refused() {
		Jengo_Test_World::add_user( 'alice', 'alice@example.com' );
		$login = $this->login();
		$flow  = $this->start_flow( $login );
		$code  = $this->iam->issue_code( $flow['location'] );

		Jengo_Test_World::$now_offset = 3600;
		$result = $this->run_callback( $login, $code, $flow['params']['state'], $flow['binding'] );

		$this->assertArrayHasKey( 'die', $result );
		$this->assertNoSession();
	}

	public function test_a_missing_code_or_an_iam_error_is_refused() {
		$login = $this->login();
		$flow  = $this->start_flow( $login );

		$no_code = $this->run_callback( $login, '', $flow['params']['state'], $flow['binding'] );
		$this->assertArrayHasKey( 'die', $no_code );

		$error = $this->run_callback( $login, 'x', $flow['params']['state'], $flow['binding'], array( 'error' => 'access_denied' ) );
		$this->assertStringContainsString( 'access_denied', $error['die'] );
		$this->assertSame( array(), $this->token_posts() );
	}

	/* ---- login CSRF: state bound to the browser ---------------------------- */

	public function test_a_callback_from_a_browser_without_the_binding_cookie_is_refused() {
		Jengo_Test_World::add_user( 'alice', 'alice@example.com' );
		$login = $this->login();
		$flow  = $this->start_flow( $login );
		$code  = $this->iam->issue_code( $flow['location'] );

		// The victim's browser never started this flow, so it has no binding cookie.
		$result = $this->run_callback( $login, $code, $flow['params']['state'], null );

		$this->assertArrayHasKey( 'die', $result );
		$this->assertSame( array(), $this->token_posts(), 'The code must not even be exchanged' );
		$this->assertNoSession();
	}

	public function test_a_callback_with_someone_elses_binding_cookie_is_refused() {
		Jengo_Test_World::add_user( 'alice', 'alice@example.com' );
		$login = $this->login();
		$flow  = $this->start_flow( $login );
		$code  = $this->iam->issue_code( $flow['location'] );

		$result = $this->run_callback( $login, $code, $flow['params']['state'], bin2hex( random_bytes( 16 ) ) );

		$this->assertArrayHasKey( 'die', $result );
		$this->assertNoSession();
	}

	public function test_the_binding_secret_itself_is_not_stored_server_side() {
		$flow   = $this->start_flow( $this->login() );
		$stored = get_transient( Jengo_IAM_SSO_Login::STATE_TRANSIENT_PREFIX . $flow['params']['state'] );

		$this->assertNotSame( $flow['binding'], $stored['binding'] );
		$this->assertSame( hash( 'sha256', $flow['binding'] ), $stored['binding'] );
	}

	/* ---- id_token: no login without a validated token ---------------------- */

	public function test_a_token_response_without_an_id_token_is_refused() {
		Jengo_Test_World::add_user( 'boss', 'boss@example.com', array( 'administrator' ) );
		Jengo_Test_World::route( 'POST', 'https://iam.test/connect/token', function () {
			return array( 200, json_encode( array( 'access_token' => 'a', 'token_type' => 'Bearer' ) ) );
		} );

		$result = $this->sso_login();

		$this->assertArrayHasKey( 'die', $result );
		$this->assertNoSession();
	}

	public function test_reviewer_scenario_s2_wrong_issuer_audience_and_expiry_are_each_refused() {
		Jengo_Test_World::add_user( 'boss', 'boss@example.com', array( 'administrator' ) );

		foreach ( array(
			'issuer'   => array( 'iss' => 'https://evil.test/auth/' ),
			'audience' => array( 'aud' => 'someone-else' ),
			'expired'  => array( 'exp' => 1 ),
		) as $label => $override ) {
			$result = $this->sso_login( array_merge( array( 'email' => 'boss@example.com', 'sub' => 'eve-sub' ), $override ) );
			$this->assertArrayHasKey( 'die', $result, "$label must be refused" );
			$this->assertNoSession();
		}
	}

	public function test_a_wrong_nonce_is_refused() {
		Jengo_Test_World::add_user( 'alice', 'alice@example.com' );

		$result = $this->sso_login( array( 'nonce' => 'not-the-one-we-minted' ) );

		$this->assertArrayHasKey( 'die', $result );
		$this->assertNoSession();
	}

	public function test_an_id_token_without_a_nonce_does_not_skip_the_nonce_check() {
		Jengo_Test_World::add_user( 'alice', 'alice@example.com' );

		$result = $this->sso_login( array( 'nonce' => null ) );

		$this->assertArrayHasKey( 'die', $result );
		$this->assertNoSession();
	}

	public function test_a_tampered_signature_is_refused() {
		Jengo_Test_World::add_user( 'alice', 'alice@example.com' );
		$this->iam->tamper = static function ( $jwt ) {
			$parts    = explode( '.', $jwt );
			$parts[2] = strrev( $parts[2] );
			return implode( '.', $parts );
		};

		$result = $this->sso_login();

		$this->assertArrayHasKey( 'die', $result );
		$this->assertNoSession();
	}

	public function test_a_token_signed_by_a_key_that_is_not_in_the_jwks_is_refused() {
		Jengo_Test_World::add_user( 'alice', 'alice@example.com' );
		$this->iam->key = Jengo_Test_Fake_Iam::key( 'attacker' ); // JWKS still advertises... the attacker key under kid-1.
		Jengo_Test_World::route( 'GET', 'https://iam.test/.well-known/jwks', function () {
			return array( 200, json_encode( array( 'keys' => array( Jengo_Test_Fake_Iam::jwk( Jengo_Test_Fake_Iam::key( 'main' ), 'kid-1' ) ) ) ) );
		} );

		$result = $this->sso_login();

		$this->assertArrayHasKey( 'die', $result );
		$this->assertNoSession();
	}

	public function test_an_unreachable_jwks_means_no_login() {
		Jengo_Test_World::add_user( 'alice', 'alice@example.com' );
		$this->iam->jwks_up = false;

		$result = $this->sso_login();

		$this->assertArrayHasKey( 'die', $result );
		$this->assertNoSession();
	}

	public function test_a_refused_token_exchange_means_no_login() {
		Jengo_Test_World::add_user( 'alice', 'alice@example.com' );
		$this->iam->client_secret = 'a-different-secret'; // IAM now rejects ours.
		$this->configure( array( 'client_secret' => 'wrong-secret' ) );

		$result = $this->sso_login();

		$this->assertArrayHasKey( 'die', $result );
		$this->assertNoSession();
	}

	/* ---- e-mail verification ------------------------------------------------- */

	public function test_reviewer_scenario_s1_an_unverified_email_never_logs_into_an_existing_admin() {
		$boss = Jengo_Test_World::add_user( 'boss', 'boss@example.com', array( 'administrator' ) );

		$result = $this->sso_login( array( 'sub' => 'eve-sub', 'email' => 'boss@example.com', 'email_verified' => false ) );

		$this->assertArrayHasKey( 'die', $result );
		$this->assertNoSession();
		$this->assertSame( '', get_user_meta( $boss->ID, Jengo_IAM_SSO_Login::SUB_META_KEY, true ), 'The IAM subject must not be linked to the admin' );
	}

	public function test_a_missing_email_verified_claim_counts_as_unverified() {
		Jengo_Test_World::add_user( 'boss', 'boss@example.com', array( 'administrator' ) );

		$result = $this->sso_login( array( 'sub' => 'eve-sub', 'email' => 'boss@example.com', 'email_verified' => null ) );

		$this->assertArrayHasKey( 'die', $result );
		$this->assertNoSession();
	}

	public function test_email_verified_must_be_the_boolean_true() {
		Jengo_Test_World::add_user( 'boss', 'boss@example.com', array( 'administrator' ) );

		foreach ( array( 'true', 1, 'yes', array( true ) ) as $not_a_boolean ) {
			$result = $this->sso_login( array( 'sub' => 'eve-sub', 'email' => 'boss@example.com', 'email_verified' => $not_a_boolean ) );
			$this->assertArrayHasKey( 'die', $result );
		}
		$this->assertNoSession();
	}

	public function test_an_unverified_email_never_provisions_an_account_either() {
		$this->configure( array( 'auto_provision' => 1 ) );

		$result = $this->sso_login( array( 'sub' => 'new-sub', 'email' => 'new@example.com', 'email_verified' => false ) );

		$this->assertArrayHasKey( 'die', $result );
		$this->assertSame( array(), Jengo_Test_World::$inserted );
		$this->assertNoSession();
	}

	/* ---- matching: subject first, then email ----------------------------------- */

	public function test_a_linked_subject_wins_even_when_the_email_differs() {
		$alice = Jengo_Test_World::add_user( 'alice', 'old-address@example.com' );
		Jengo_Test_World::add_user( 'other', 'alice@example.com' );
		update_user_meta( $alice->ID, Jengo_IAM_SSO_Login::SUB_META_KEY, 'sub-alice' );

		$result = $this->sso_login();

		$this->assertArrayHasKey( 'redirect', $result );
		$this->assertSessionIssuedFor( $alice->ID );
	}

	public function test_a_linked_subject_may_log_in_with_an_unverified_email_because_the_email_is_not_the_key() {
		$alice = Jengo_Test_World::add_user( 'alice', 'alice@example.com' );
		update_user_meta( $alice->ID, Jengo_IAM_SSO_Login::SUB_META_KEY, 'sub-alice' );

		$result = $this->sso_login( array( 'email_verified' => false ) );

		$this->assertArrayHasKey( 'redirect', $result );
		$this->assertSessionIssuedFor( $alice->ID );
	}

	public function test_an_account_already_linked_to_another_iam_identity_is_not_taken_over_by_email() {
		$alice = Jengo_Test_World::add_user( 'alice', 'alice@example.com' );
		update_user_meta( $alice->ID, Jengo_IAM_SSO_Login::SUB_META_KEY, 'sub-the-real-alice' );

		$result = $this->sso_login( array( 'sub' => 'sub-someone-else' ) );

		$this->assertArrayHasKey( 'die', $result );
		$this->assertNoSession();
		$this->assertSame( 'sub-the-real-alice', get_user_meta( $alice->ID, Jengo_IAM_SSO_Login::SUB_META_KEY, true ) );
	}

	public function test_email_matching_is_case_insensitive() {
		$alice = Jengo_Test_World::add_user( 'alice', 'Alice@Example.com' );

		$result = $this->sso_login();

		$this->assertArrayHasKey( 'redirect', $result );
		$this->assertSessionIssuedFor( $alice->ID );
	}

	/* ---- provisioning ------------------------------------------------------------ */

	public function test_auto_provision_is_off_by_default_and_an_unknown_user_is_refused() {
		$result = $this->sso_login( array( 'sub' => 'new-sub', 'email' => 'new@example.com' ) );

		$this->assertArrayHasKey( 'die', $result );
		$this->assertSame( array(), Jengo_Test_World::$inserted );
		$this->assertNoSession();
	}

	public function test_with_auto_provision_on_a_user_holding_a_site_role_is_created_with_that_role() {
		$this->configure( array( 'auto_provision' => 1 ) );

		$result = $this->sso_login( array( 'sub' => 'new-sub', 'email' => 'new.person@example.com', 'name' => 'New Person' ) );

		$this->assertArrayHasKey( 'redirect', $result );
		$this->assertCount( 1, Jengo_Test_World::$inserted );
		$created = Jengo_Test_World::$inserted[0];
		$this->assertSame( 'new.person', $created['user_login'] );
		$this->assertSame( 'editor', $created['role'] );
		$this->assertSame( 'New Person', $created['display_name'] );
		$this->assertGreaterThanOrEqual( 32, strlen( $created['user_pass'] ) );
		$user = get_user_by( 'email', 'new.person@example.com' );
		$this->assertSame( 'new-sub', get_user_meta( $user->ID, Jengo_IAM_SSO_Login::SUB_META_KEY, true ) );
		$this->assertSessionIssuedFor( $user->ID );
	}

	public function test_auto_provision_refuses_a_user_without_any_site_role() {
		$this->configure( array( 'auto_provision' => 1 ) );

		$result = $this->sso_login( array( 'sub' => 'new-sub', 'email' => 'new@example.com', 'role' => array( 'some-other-app:admin' ) ) );

		$this->assertArrayHasKey( 'die', $result );
		$this->assertStringContainsString( 'no access', strtolower( $result['die'] ) );
		$this->assertSame( array(), Jengo_Test_World::$inserted );
	}

	public function test_login_names_are_deduplicated() {
		$this->configure( array( 'auto_provision' => 1 ) );
		Jengo_Test_World::add_user( 'new.person', 'someone-else@example.com' );

		$this->sso_login( array( 'sub' => 'new-sub', 'email' => 'new.person@example.com' ) );

		$this->assertSame( 'new.person2', Jengo_Test_World::$inserted[0]['user_login'] );
	}

	public function test_with_sync_off_new_users_get_the_default_role() {
		$this->configure( array( 'auto_provision' => 1, 'sync_roles' => 0, 'default_role' => 'contributor' ) );

		$this->sso_login( array( 'sub' => 'new-sub', 'email' => 'new@example.com' ) );

		$this->assertSame( 'contributor', Jengo_Test_World::$inserted[0]['role'] );
	}

	public function test_a_default_role_of_administrator_can_never_be_used_for_a_new_account() {
		// Even if the stored option is tampered with, provisioning falls back to subscriber.
		foreach ( array( 'administrator', 'user_admin', 'nonexistent-role' ) as $bad_role ) {
			Jengo_Test_World::$inserted = array();
			$this->configure( array( 'auto_provision' => 1, 'sync_roles' => 0, 'default_role' => $bad_role ) );

			$this->sso_login( array( 'sub' => 'sub-' . $bad_role, 'email' => $bad_role . '@example.com' ) );

			$this->assertSame( 'subscriber', Jengo_Test_World::$inserted[0]['role'], "default_role=$bad_role" );
		}
	}

	/* ---- role sync ------------------------------------------------------------------ */

	public function test_role_sync_follows_iam_on_every_login_including_a_downgrade() {
		$alice = Jengo_Test_World::add_user( 'alice', 'alice@example.com', array( 'administrator' ) );

		$this->sso_login( array( 'role' => array( $this->iam->client_id . ':author' ) ) );

		$this->assertSame( array( 'author' ), $alice->roles );
	}

	public function test_highest_granted_role_wins() {
		$alice = Jengo_Test_World::add_user( 'alice', 'alice@example.com', array( 'subscriber' ) );

		$this->sso_login( array( 'role' => array( $this->iam->client_id . ':subscriber', $this->iam->client_id . ':editor' ) ) );

		$this->assertSame( array( 'editor' ), $alice->roles );
	}

	public function test_existing_users_without_any_mapped_role_keep_their_role() {
		$alice = Jengo_Test_World::add_user( 'alice', 'alice@example.com', array( 'editor' ) );

		$this->sso_login( array( 'role' => array( 'unrelated:role' ) ) );

		$this->assertSame( array( 'editor' ), $alice->roles );
		$this->assertSame( array(), Jengo_Test_World::$role_changes );
	}

	public function test_role_sync_can_be_switched_off() {
		$this->configure( array( 'sync_roles' => 0 ) );
		$alice = Jengo_Test_World::add_user( 'alice', 'alice@example.com', array( 'subscriber' ) );

		$this->sso_login();

		$this->assertSame( array( 'subscriber' ), $alice->roles );
	}

	public function test_the_break_glass_account_is_never_modified() {
		$admin = Jengo_Test_World::add_user( 'jengo-admin', 'alice@example.com', array( 'administrator' ) );

		$this->sso_login( array( 'role' => array( $this->iam->client_id . ':subscriber' ) ) );

		$this->assertSame( array( 'administrator' ), $admin->roles );
	}

	/* ---- the client secret ------------------------------------------------------------ */

	public function test_failure_screens_never_contain_the_client_secret() {
		Jengo_Test_World::add_user( 'alice', 'alice@example.com' );
		$messages = array();

		$this->iam->tamper = static function ( $jwt ) {
			return 'garbage';
		};
		$messages[] = $this->sso_login()['die'];
		$this->iam->tamper = null;

		$messages[] = $this->sso_login( array( 'email_verified' => false, 'sub' => 'x', 'email' => 'nobody@example.com' ) )['die'];

		foreach ( $messages as $message ) {
			$this->assertStringNotContainsString( $this->iam->client_secret, $message );
		}
	}

	public function test_the_client_secret_only_ever_travels_in_the_token_request_body() {
		Jengo_Test_World::add_user( 'alice', 'alice@example.com' );

		$this->sso_login();

		foreach ( Jengo_Test_World::$http_log as $entry ) {
			list( $method, $url, $args ) = $entry;
			$this->assertStringNotContainsString( $this->iam->client_secret, $url );
			if ( 'POST' === $method ) {
				$this->assertSame( $this->iam->client_secret, $args['body']['client_secret'] );
				$this->assertSame( 0, $args['redirection'], 'The token POST must not follow redirects' );
			}
		}
	}
}
