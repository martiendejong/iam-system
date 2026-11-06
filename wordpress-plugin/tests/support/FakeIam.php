<?php
/**
 * A faithful little fake of Jengo IAM for the plugin tests: RSA signing keys,
 * a discovery document, a JWKS, an authorize-URL parser and a token endpoint
 * that really checks client credentials, redirect_uri and the PKCE S256
 * code_verifier before it hands out a signed id_token.
 */
class Jengo_Test_Fake_Iam {

	public $issuer        = 'https://iam.test/auth/';
	public $client_id     = 'wp-test-client';
	public $client_secret = 'test-secret-value';
	public $kid           = 'kid-1';

	/** @var resource|OpenSSLAsymmetricKey */
	public $key;

	public $discovery_up        = true;
	public $jwks_up             = true;
	public $advertise_end_session = true;
	public $issuer_in_document  = null;
	public $tamper              = null;

	private $codes = array();

	private static $key_cache = array();

	public function __construct() {
		$this->key = self::key( 'main' );
	}

	/** RSA keys are slow to generate; cache them across tests. */
	public static function key( $name ) {
		if ( ! isset( self::$key_cache[ $name ] ) ) {
			$key = openssl_pkey_new(
				array(
					'private_key_bits' => 2048,
					'private_key_type' => OPENSSL_KEYTYPE_RSA,
					'config'           => __DIR__ . '/openssl.cnf',
				)
			);
			if ( false === $key ) {
				throw new RuntimeException( 'openssl_pkey_new failed: ' . openssl_error_string() );
			}
			self::$key_cache[ $name ] = $key;
		}
		return self::$key_cache[ $name ];
	}

	public static function b64( $data ) {
		return rtrim( strtr( base64_encode( $data ), '+/', '-_' ), '=' );
	}

	public static function jwk( $key, $kid ) {
		$details = openssl_pkey_get_details( $key );
		return array(
			'kty' => 'RSA',
			'use' => 'sig',
			'alg' => 'RS256',
			'kid' => $kid,
			'n'   => self::b64( $details['rsa']['n'] ),
			'e'   => self::b64( $details['rsa']['e'] ),
		);
	}

	public static function sign_jwt( array $claims, $key, $kid, array $header_overrides = array() ) {
		$header  = array_merge(
			array(
				'alg' => 'RS256',
				'typ' => 'JWT',
				'kid' => $kid,
			),
			$header_overrides
		);
		$header  = array_filter( $header, static function ( $v ) {
			return null !== $v;
		} );
		$signing = self::b64( json_encode( $header ) ) . '.' . self::b64( json_encode( $claims ) );
		openssl_sign( $signing, $signature, $key, OPENSSL_ALGO_SHA256 );
		return $signing . '.' . self::b64( $signature );
	}

	public function register() {
		$base = untrailingslashit( $this->issuer );
		Jengo_Test_World::route( 'GET', $base . '/.well-known/openid-configuration', function () {
			if ( ! $this->discovery_up ) {
				return new WP_Error( 'http_request_failed', 'connection refused' );
			}
			return array( 200, json_encode( $this->discovery_document() ) );
		} );
		Jengo_Test_World::route( 'GET', 'https://iam.test/.well-known/jwks', function () {
			if ( ! $this->jwks_up ) {
				return array( 500, '' );
			}
			return array( 200, json_encode( array( 'keys' => array( self::jwk( $this->key, $this->kid ) ) ) ) );
		} );
		Jengo_Test_World::route( 'POST', 'https://iam.test/connect/token', array( $this, 'token_endpoint' ) );
	}

	public function discovery_document() {
		$doc = array(
			'issuer'                 => null === $this->issuer_in_document ? $this->issuer : $this->issuer_in_document,
			'authorization_endpoint' => 'https://iam.test/connect/authorize',
			'token_endpoint'         => 'https://iam.test/connect/token',
			'jwks_uri'               => 'https://iam.test/.well-known/jwks',
		);
		if ( $this->advertise_end_session ) {
			$doc['end_session_endpoint'] = 'https://iam.test/connect/logout';
		}
		return $doc;
	}

	/**
	 * What IAM does after the user authenticated: remember a code bound to
	 * the authorize request's parameters and return it.
	 *
	 * @param string $authorize_url   Location the plugin redirected the browser to.
	 * @param array  $claim_overrides Claims to override/add in the id_token (null removes).
	 */
	public function issue_code( $authorize_url, array $claim_overrides = array() ) {
		parse_str( (string) parse_url( $authorize_url, PHP_URL_QUERY ), $params );
		$code               = bin2hex( random_bytes( 8 ) );
		$this->codes[ $code ] = array(
			'params'    => $params,
			'overrides' => $claim_overrides,
			'used'      => false,
		);
		return $code;
	}

	public function default_claims( array $params ) {
		return array(
			'iss'            => $this->issuer,
			'aud'            => $this->client_id,
			'sub'            => 'sub-alice',
			'iat'            => time(),
			'exp'            => time() + 1200,
			'nonce'          => isset( $params['nonce'] ) ? $params['nonce'] : null,
			'email'          => 'alice@example.com',
			'email_verified' => true,
			'name'           => 'Alice Example',
			'role'           => array( $this->client_id . ':editor' ),
		);
	}

	public function token_endpoint( array $args ) {
		$body = $args['body'];
		if ( ( $body['grant_type'] ?? '' ) !== 'authorization_code' || ! isset( $this->codes[ $body['code'] ?? '' ] ) ) {
			return array( 400, json_encode( array( 'error' => 'invalid_grant' ) ) );
		}
		$entry = &$this->codes[ $body['code'] ];
		if ( $entry['used'] ) {
			return array( 400, json_encode( array( 'error' => 'invalid_grant' ) ) );
		}
		$entry['used'] = true;

		if ( ( $body['client_id'] ?? '' ) !== $this->client_id || ! hash_equals( $this->client_secret, (string) ( $body['client_secret'] ?? '' ) ) ) {
			return array( 401, json_encode( array( 'error' => 'invalid_client' ) ) );
		}
		if ( ( $body['redirect_uri'] ?? '' ) !== ( $entry['params']['redirect_uri'] ?? null ) ) {
			return array( 400, json_encode( array( 'error' => 'invalid_grant' ) ) );
		}
		$expected_challenge = self::b64( hash( 'sha256', (string) ( $body['code_verifier'] ?? '' ), true ) );
		if ( ! hash_equals( (string) ( $entry['params']['code_challenge'] ?? '' ), $expected_challenge ) ) {
			return array( 400, json_encode( array( 'error' => 'invalid_grant', 'error_description' => 'PKCE verification failed' ) ) );
		}

		$claims = array_merge( $this->default_claims( $entry['params'] ), $entry['overrides'] );
		$claims = array_filter( $claims, static function ( $v ) {
			return null !== $v;
		} );
		$id_token = self::sign_jwt( $claims, $this->key, $this->kid );
		if ( is_callable( $this->tamper ) ) {
			$id_token = call_user_func( $this->tamper, $id_token );
		}

		return array(
			200,
			json_encode(
				array(
					'access_token' => 'access-token-not-used',
					'token_type'   => 'Bearer',
					'id_token'     => $id_token,
				)
			),
		);
	}
}
