<?php

/**
 * PKCE (RFC 7636): S256 only, verifier/challenge shape, and the authorize
 * request actually carries the challenge.
 */
class PkceTest extends JengoTestCase {

	public function test_challenge_is_base64url_sha256_of_the_verifier_rfc7636_appendix_b() {
		// RFC 7636 Appendix B test vector.
		$verifier  = 'dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk';
		$challenge = Jengo_IAM_SSO_OIDC_Client::base64url_encode( hash( 'sha256', $verifier, true ) );

		$this->assertSame( 'E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM', $challenge );
	}

	public function test_generated_pair_is_consistent_and_rfc_shaped() {
		$pair = Jengo_IAM_SSO_OIDC_Client::generate_pkce_pair();

		$this->assertMatchesRegularExpression( '/^[A-Za-z0-9_-]{43,128}$/', $pair['verifier'] );
		$this->assertSame(
			Jengo_IAM_SSO_OIDC_Client::base64url_encode( hash( 'sha256', $pair['verifier'], true ) ),
			$pair['challenge']
		);
		$this->assertDoesNotMatchRegularExpression( '/[+\/=]/', $pair['challenge'] );
	}

	public function test_every_pair_is_fresh() {
		$a = Jengo_IAM_SSO_OIDC_Client::generate_pkce_pair();
		$b = Jengo_IAM_SSO_OIDC_Client::generate_pkce_pair();

		$this->assertNotSame( $a['verifier'], $b['verifier'] );
	}

	public function test_authorize_request_uses_s256_code_flow_with_state_nonce_and_exact_redirect_uri() {
		$flow = $this->start_flow( $this->login() );

		$this->assertStringStartsWith( 'https://iam.test/connect/authorize?', $flow['location'] );
		$this->assertSame( 'S256', $flow['params']['code_challenge_method'] );
		$this->assertNotEmpty( $flow['params']['code_challenge'] );
		$this->assertSame( 'code', $flow['params']['response_type'] );
		$this->assertSame( $this->iam->client_id, $flow['params']['client_id'] );
		$this->assertSame( 'https://test.prospergenics.com/wp-login.php?action=jengo-iam-callback', $flow['params']['redirect_uri'] );
		$this->assertMatchesRegularExpression( '/^[a-f0-9]{32}$/', $flow['params']['state'] );
		$this->assertMatchesRegularExpression( '/^[a-f0-9]{32}$/', $flow['params']['nonce'] );
		$this->assertStringContainsString( 'openid', $flow['params']['scope'] );
		$this->assertStringNotContainsString( 'plain', $flow['location'] );
	}

	public function test_the_verifier_is_kept_server_side_and_never_sent_to_iam_in_the_authorize_request() {
		$flow   = $this->start_flow( $this->login() );
		$stored = get_transient( Jengo_IAM_SSO_Login::STATE_TRANSIENT_PREFIX . $flow['params']['state'] );

		$this->assertIsArray( $stored );
		$this->assertStringNotContainsString( $stored['verifier'], $flow['location'] );
		$this->assertSame(
			$flow['params']['code_challenge'],
			Jengo_IAM_SSO_OIDC_Client::base64url_encode( hash( 'sha256', $stored['verifier'], true ) )
		);
	}

	public function test_token_exchange_sends_the_verifier_and_a_wrong_verifier_is_refused_by_iam() {
		$login = $this->login();
		$flow  = $this->start_flow( $login );
		$code  = $this->iam->issue_code( $flow['location'], array() );

		// Corrupt the stored verifier: IAM's PKCE check must fail, so no login.
		$key            = Jengo_IAM_SSO_Login::STATE_TRANSIENT_PREFIX . $flow['params']['state'];
		$stored         = get_transient( $key );
		$stored['verifier'] = 'x' . $stored['verifier'];
		set_transient( $key, $stored, 600 );

		$result = $this->run_callback( $login, $code, $flow['params']['state'], $flow['binding'] );

		$this->assertArrayHasKey( 'die', $result );
		$this->assertNoSession();
	}
}
