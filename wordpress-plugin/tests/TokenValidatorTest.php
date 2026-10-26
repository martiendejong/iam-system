<?php

/**
 * id_token validation against a test JWKS: signature, algorithm, issuer,
 * audience, expiry, nonce, subject, key rotation.
 */
class TokenValidatorTest extends JengoTestCase {

	const NOW = 1800000000;

	private function claims( array $overrides = array() ) {
		$claims = array_merge(
			array(
				'iss'   => 'https://iam.test/auth/',
				'aud'   => 'wp-test-client',
				'sub'   => 'sub-alice',
				'iat'   => self::NOW - 10,
				'exp'   => self::NOW + 1200,
				'nonce' => 'nonce-abc',
			),
			$overrides
		);
		return array_filter( $claims, static function ( $v ) {
			return null !== $v;
		} );
	}

	private function jwks( ?array $keys = null ) {
		$keys = $keys ?? array( Jengo_Test_Fake_Iam::jwk( Jengo_Test_Fake_Iam::key( 'main' ), 'kid-1' ) );
		return function ( $force ) use ( $keys ) {
			return array( 'keys' => $keys );
		};
	}

	private function validate( $jwt, $provider = null, array $expected = array() ) {
		return Jengo_IAM_SSO_Token_Validator::validate_id_token(
			$jwt,
			$provider ?? $this->jwks(),
			array_merge(
				array(
					'issuer'    => 'https://iam.test/auth/',
					'client_id' => 'wp-test-client',
					'nonce'     => 'nonce-abc',
				),
				$expected
			),
			self::NOW
		);
	}

	private function sign( array $claims, $key = 'main', $kid = 'kid-1', array $header = array() ) {
		return Jengo_Test_Fake_Iam::sign_jwt( $claims, Jengo_Test_Fake_Iam::key( $key ), $kid, $header );
	}

	private function assertRejected( $result, $reason ) {
		$this->assertInstanceOf( WP_Error::class, $result, 'Token must be rejected' );
		$this->assertSame( 'id_token_' . $reason, $result->get_error_code() );
	}

	public function test_a_correct_token_is_accepted_and_returns_its_claims() {
		$result = $this->validate( $this->sign( $this->claims() ) );

		$this->assertIsArray( $result );
		$this->assertSame( 'sub-alice', $result['sub'] );
	}

	public function test_signature_from_another_key_is_rejected() {
		$this->assertRejected( $this->validate( $this->sign( $this->claims(), 'attacker' ) ), 'bad_signature' );
	}

	public function test_tampered_payload_is_rejected() {
		$jwt   = $this->sign( $this->claims() );
		$parts = explode( '.', $jwt );
		$parts[1] = Jengo_Test_Fake_Iam::b64( json_encode( $this->claims( array( 'sub' => 'sub-boss' ) ) ) );

		$this->assertRejected( $this->validate( implode( '.', $parts ) ), 'bad_signature' );
	}

	public function test_alg_none_is_rejected() {
		$jwt = Jengo_Test_Fake_Iam::b64( json_encode( array( 'alg' => 'none', 'typ' => 'JWT' ) ) ) . '.'
			. Jengo_Test_Fake_Iam::b64( json_encode( $this->claims() ) ) . '.' . Jengo_Test_Fake_Iam::b64( 'x' );

		$this->assertRejected( $this->validate( $jwt ), 'unsupported_alg' );
	}

	public function test_hmac_signed_with_the_public_key_as_secret_is_rejected() {
		$pem = Jengo_IAM_SSO_Token_Validator::jwk_to_pem( Jengo_Test_Fake_Iam::jwk( Jengo_Test_Fake_Iam::key( 'main' ), 'kid-1' ) );
		$signing = Jengo_Test_Fake_Iam::b64( json_encode( array( 'alg' => 'HS256', 'typ' => 'JWT', 'kid' => 'kid-1' ) ) ) . '.'
			. Jengo_Test_Fake_Iam::b64( json_encode( $this->claims() ) );
		$jwt = $signing . '.' . Jengo_Test_Fake_Iam::b64( hash_hmac( 'sha256', $signing, $pem, true ) );

		$this->assertRejected( $this->validate( $jwt ), 'unsupported_alg' );
	}

	public function test_wrong_issuer_is_rejected() {
		$this->assertRejected( $this->validate( $this->sign( $this->claims( array( 'iss' => 'https://evil.test/auth/' ) ) ) ), 'bad_issuer' );
	}

	public function test_missing_issuer_is_rejected() {
		$this->assertRejected( $this->validate( $this->sign( $this->claims( array( 'iss' => null ) ) ) ), 'bad_issuer' );
	}

	public function test_issuer_trailing_slash_difference_is_tolerated() {
		$result = $this->validate( $this->sign( $this->claims( array( 'iss' => 'https://iam.test/auth' ) ) ) );

		$this->assertIsArray( $result );
	}

	public function test_wrong_audience_is_rejected() {
		$this->assertRejected( $this->validate( $this->sign( $this->claims( array( 'aud' => 'some-other-client' ) ) ) ), 'bad_audience' );
	}

	public function test_missing_audience_is_rejected() {
		$this->assertRejected( $this->validate( $this->sign( $this->claims( array( 'aud' => null ) ) ) ), 'bad_audience' );
	}

	public function test_audience_list_containing_the_client_is_accepted_only_with_matching_azp() {
		$multi = $this->claims( array( 'aud' => array( 'wp-test-client', 'other' ) ) );

		$this->assertRejected( $this->validate( $this->sign( $multi ) ), 'bad_azp' );
		$this->assertIsArray( $this->validate( $this->sign( array_merge( $multi, array( 'azp' => 'wp-test-client' ) ) ) ) );
	}

	public function test_expired_token_is_rejected() {
		$this->assertRejected( $this->validate( $this->sign( $this->claims( array( 'exp' => self::NOW - 3600 ) ) ) ), 'expired' );
	}

	public function test_exp_1970_is_rejected() {
		$this->assertRejected( $this->validate( $this->sign( $this->claims( array( 'exp' => 1 ) ) ) ), 'expired' );
	}

	public function test_missing_exp_is_rejected() {
		$this->assertRejected( $this->validate( $this->sign( $this->claims( array( 'exp' => null ) ) ) ), 'expired' );
	}

	public function test_small_clock_skew_on_exp_is_tolerated() {
		$result = $this->validate( $this->sign( $this->claims( array( 'exp' => self::NOW - 30 ) ) ) );

		$this->assertIsArray( $result );
	}

	public function test_token_issued_in_the_far_future_is_rejected() {
		$this->assertRejected( $this->validate( $this->sign( $this->claims( array( 'iat' => self::NOW + 7200 ) ) ) ), 'not_yet_valid' );
	}

	public function test_wrong_nonce_is_rejected() {
		$this->assertRejected( $this->validate( $this->sign( $this->claims( array( 'nonce' => 'replayed' ) ) ) ), 'bad_nonce' );
	}

	public function test_missing_nonce_is_rejected() {
		$this->assertRejected( $this->validate( $this->sign( $this->claims( array( 'nonce' => null ) ) ) ), 'bad_nonce' );
	}

	public function test_missing_or_empty_subject_is_rejected() {
		$this->assertRejected( $this->validate( $this->sign( $this->claims( array( 'sub' => null ) ) ) ), 'missing_sub' );
		$this->assertRejected( $this->validate( $this->sign( $this->claims( array( 'sub' => '' ) ) ) ), 'missing_sub' );
	}

	public function test_malformed_tokens_are_rejected() {
		foreach ( array( '', 'abc', 'a.b', 'a.b.c.d', '!!.!!.!!', str_repeat( 'a', 20000 ) ) as $junk ) {
			$this->assertRejected( $this->validate( $junk ), 'malformed' );
		}
	}

	public function test_unknown_kid_is_rejected_after_one_refresh() {
		$calls = array();
		$provider = function ( $force ) use ( &$calls ) {
			$calls[] = $force;
			return array( 'keys' => array( Jengo_Test_Fake_Iam::jwk( Jengo_Test_Fake_Iam::key( 'main' ), 'kid-1' ) ) );
		};

		$result = $this->validate( $this->sign( $this->claims(), 'main', 'kid-unknown' ), $provider );

		$this->assertRejected( $result, 'unknown_key' );
		$this->assertSame( array( false, true ), $calls, 'JWKS is refetched exactly once for an unknown kid' );
	}

	public function test_key_rotation_is_handled_by_a_forced_refresh() {
		$old = Jengo_Test_Fake_Iam::jwk( Jengo_Test_Fake_Iam::key( 'main' ), 'kid-1' );
		$new = Jengo_Test_Fake_Iam::jwk( Jengo_Test_Fake_Iam::key( 'rotated' ), 'kid-2' );
		$provider = function ( $force ) use ( $old, $new ) {
			return array( 'keys' => $force ? array( $old, $new ) : array( $old ) );
		};

		$result = $this->validate( $this->sign( $this->claims(), 'rotated', 'kid-2' ), $provider );

		$this->assertIsArray( $result );
	}

	public function test_token_without_kid_is_checked_against_the_only_signing_key() {
		$result = $this->validate( $this->sign( $this->claims(), 'main', 'kid-1', array( 'kid' => null ) ) );

		$this->assertIsArray( $result );
	}

	public function test_token_without_kid_is_rejected_when_the_key_choice_is_ambiguous() {
		$keys = array(
			Jengo_Test_Fake_Iam::jwk( Jengo_Test_Fake_Iam::key( 'main' ), 'kid-1' ),
			Jengo_Test_Fake_Iam::jwk( Jengo_Test_Fake_Iam::key( 'rotated' ), 'kid-2' ),
		);

		$result = $this->validate( $this->sign( $this->claims(), 'main', 'kid-1', array( 'kid' => null ) ), $this->jwks( $keys ) );

		$this->assertRejected( $result, 'unknown_key' );
	}

	public function test_unreachable_jwks_means_no_login() {
		$provider = function () {
			return new WP_Error( 'jengo_iam_sso_jwks', 'jwks_unavailable' );
		};

		$this->assertRejected( $this->validate( $this->sign( $this->claims() ), $provider ), 'jwks_unavailable' );
	}

	public function test_non_signing_and_non_rs256_keys_are_ignored() {
		$jwk = Jengo_Test_Fake_Iam::jwk( Jengo_Test_Fake_Iam::key( 'main' ), 'kid-1' );
		$enc = array_merge( $jwk, array( 'use' => 'enc' ) );
		$alg = array_merge( $jwk, array( 'alg' => 'RS512' ) );

		$this->assertRejected( $this->validate( $this->sign( $this->claims() ), $this->jwks( array( $enc ) ) ), 'unknown_key' );
		$this->assertRejected( $this->validate( $this->sign( $this->claims() ), $this->jwks( array( $alg ) ) ), 'unknown_key' );
	}

	public function test_a_modulus_smaller_than_2048_bits_is_not_usable() {
		$jwk      = Jengo_Test_Fake_Iam::jwk( Jengo_Test_Fake_Iam::key( 'main' ), 'kid-1' );
		$jwk['n'] = Jengo_Test_Fake_Iam::b64( random_bytes( 128 ) );

		$this->assertRejected( $this->validate( $this->sign( $this->claims() ), $this->jwks( array( $jwk ) ) ), 'unusable_key' );
	}

	public function test_jwk_to_pem_matches_what_openssl_exports_for_the_same_key() {
		$key = Jengo_Test_Fake_Iam::key( 'main' );
		$pem = Jengo_IAM_SSO_Token_Validator::jwk_to_pem( Jengo_Test_Fake_Iam::jwk( $key, 'kid-1' ) );

		$this->assertNotNull( $pem );
		$this->assertSame(
			preg_replace( '/\s+/', '', openssl_pkey_get_details( $key )['key'] ),
			preg_replace( '/\s+/', '', $pem )
		);
	}

	public function test_b64url_decode_is_strict() {
		$this->assertFalse( Jengo_IAM_SSO_Token_Validator::b64url_decode( 'ab+/' ) );
		$this->assertFalse( Jengo_IAM_SSO_Token_Validator::b64url_decode( 'ab=' ) );
		$this->assertFalse( Jengo_IAM_SSO_Token_Validator::b64url_decode( 'a' ) );
		$this->assertSame( 'hi', Jengo_IAM_SSO_Token_Validator::b64url_decode( 'aGk' ) );
	}
}
