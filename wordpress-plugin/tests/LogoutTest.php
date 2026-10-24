<?php

/**
 * "Also log out of IAM": only for sessions that came from SSO, only for an
 * interactive logout, and silently skipped when IAM has no end_session endpoint.
 */
class LogoutTest extends JengoTestCase {

	private function logout( array $cookies, array $request = array( 'action' => 'logout' ) ) {
		$_REQUEST = $request;
		$_COOKIE  = $cookies;
		$login    = $this->login();
		try {
			$login->maybe_end_iam_session( 1 );
		} catch ( Jengo_Test_Redirect $redirect ) {
			return $redirect->location;
		}
		return null;
	}

	public function test_with_the_option_on_an_sso_session_continues_to_the_iam_end_session_endpoint() {
		$this->configure( array( 'logout_iam' => 1 ) );

		$location = $this->logout( array( Jengo_IAM_SSO_Login::SSO_SESSION_COOKIE => '1' ) );

		$this->assertNotNull( $location, 'Expected a redirect to IAM' );
		$this->assertStringStartsWith( 'https://iam.test/connect/logout?', $location );
		parse_str( (string) parse_url( $location, PHP_URL_QUERY ), $params );
		$this->assertSame( $this->iam->client_id, $params['client_id'] );
		$this->assertSame( 'https://test.prospergenics.com/', $params['post_logout_redirect_uri'] );
	}

	public function test_the_sso_session_marker_is_cleared_on_logout() {
		$this->configure( array( 'logout_iam' => 1 ) );

		$this->logout( array( Jengo_IAM_SSO_Login::SSO_SESSION_COOKIE => '1' ) );

		$this->assertSame( '', Jengo_Test_World::cookie_value( Jengo_IAM_SSO_Login::SSO_SESSION_COOKIE ) );
	}

	public function test_with_the_option_off_logout_stays_local() {
		$this->configure( array( 'logout_iam' => 0 ) );

		$this->assertNull( $this->logout( array( Jengo_IAM_SSO_Login::SSO_SESSION_COOKIE => '1' ) ) );
	}

	public function test_a_session_that_did_not_come_from_sso_never_leaves_for_iam() {
		$this->configure( array( 'logout_iam' => 1 ) );

		$this->assertNull( $this->logout( array() ) );
	}

	public function test_a_programmatic_logout_is_not_redirected() {
		$this->configure( array( 'logout_iam' => 1 ) );

		$this->assertNull( $this->logout( array( Jengo_IAM_SSO_Login::SSO_SESSION_COOKIE => '1' ), array() ) );
	}

	public function test_an_iam_without_end_session_endpoint_is_skipped_quietly() {
		$this->configure( array( 'logout_iam' => 1 ) );
		$this->iam->advertise_end_session = false;

		$this->assertNull( $this->logout( array( Jengo_IAM_SSO_Login::SSO_SESSION_COOKIE => '1' ) ) );
	}

	public function test_an_unreachable_iam_never_blocks_the_local_logout() {
		$this->configure( array( 'logout_iam' => 1 ) );
		$this->iam->discovery_up = false;

		$this->assertNull( $this->logout( array( Jengo_IAM_SSO_Login::SSO_SESSION_COOKIE => '1' ) ) );
	}

	public function test_the_end_session_endpoint_must_be_https_to_be_used() {
		$this->configure( array( 'logout_iam' => 1 ) );
		Jengo_Test_World::route( 'GET', 'https://iam.test/auth/.well-known/openid-configuration', function () {
			$doc                         = $this->iam->discovery_document();
			$doc['end_session_endpoint'] = 'http://iam.test/connect/logout';
			return array( 200, json_encode( $doc ) );
		} );

		$this->assertNull( $this->logout( array( Jengo_IAM_SSO_Login::SSO_SESSION_COOKIE => '1' ) ) );
	}

	public function test_the_logout_hook_is_registered() {
		$hooks = Jengo_Test_World::$hooks['wp_logout'] ?? array();

		$this->assertNotEmpty( $hooks );
	}
}
