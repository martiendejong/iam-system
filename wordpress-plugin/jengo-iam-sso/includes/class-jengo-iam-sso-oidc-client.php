<?php
/**
 * Minimal OpenID Connect client for Jengo IAM.
 *
 * Authorization-code flow with PKCE (S256 only), client_secret_post at the
 * token endpoint, userinfo as the source of truth for identity/roles.
 * Endpoints come from OIDC discovery (/.well-known/openid-configuration),
 * cached in a transient; configured endpoint overrides and issuer-relative
 * defaults act as fallbacks when discovery is unreachable.
 *
 * Pure PHP + WordPress HTTP API; no composer dependencies.
 */

if ( ! defined( 'ABSPATH' ) ) {
	exit;
}

class Jengo_IAM_SSO_OIDC_Client {

	const DISCOVERY_CACHE_TTL = 12 * HOUR_IN_SECONDS;

	/** @var array Plugin options (see jengo_iam_sso_get_options()). */
	private $options;

	public function __construct( array $options ) {
		$this->options = $options;
	}

	/**
	 * The redirect URI registered at IAM. Must match the OAuth client's
	 * RedirectUris entry byte-for-byte, query string included.
	 */
	public function get_redirect_uri() {
		return add_query_arg( 'action', 'jengo-iam-callback', wp_login_url() );
	}

	/**
	 * Resolve the authorization/token/userinfo endpoints.
	 *
	 * Order: cached discovery document → live discovery fetch → configured
	 * endpoint overrides → issuer-relative defaults (/connect/*).
	 *
	 * @return array{authorization_endpoint:string,token_endpoint:string,userinfo_endpoint:string}
	 */
	public function get_endpoints() {
		$issuer = untrailingslashit( $this->options['issuer'] );

		$cache_key = 'jengo_iam_sso_disco_' . md5( $issuer );
		$discovery = get_transient( $cache_key );

		if ( ! is_array( $discovery ) ) {
			$discovery = $this->fetch_discovery_document( $issuer );
			if ( is_array( $discovery ) ) {
				set_transient( $cache_key, $discovery, self::DISCOVERY_CACHE_TTL );
			}
		}

		$endpoints = array(
			'authorization_endpoint' => $issuer . '/connect/authorize',
			'token_endpoint'         => $issuer . '/connect/token',
			'userinfo_endpoint'      => $issuer . '/connect/userinfo',
		);

		// Configured overrides beat the issuer-relative defaults.
		foreach ( array_keys( $endpoints ) as $key ) {
			if ( ! empty( $this->options[ $key ] ) ) {
				$endpoints[ $key ] = $this->options[ $key ];
			}
		}

		// A valid discovery document beats both.
		if ( is_array( $discovery ) ) {
			foreach ( array_keys( $endpoints ) as $key ) {
				if ( ! empty( $discovery[ $key ] ) && is_string( $discovery[ $key ] ) ) {
					$endpoints[ $key ] = $discovery[ $key ];
				}
			}
		}

		return $endpoints;
	}

	/**
	 * Fetch and sanity-check the OIDC discovery document.
	 *
	 * @return array|null Decoded document, or null on failure.
	 */
	private function fetch_discovery_document( $issuer ) {
		$response = wp_remote_get(
			$issuer . '/.well-known/openid-configuration',
			array( 'timeout' => 10 )
		);

		if ( is_wp_error( $response ) || 200 !== wp_remote_retrieve_response_code( $response ) ) {
			return null;
		}

		$document = json_decode( wp_remote_retrieve_body( $response ), true );
		if ( ! is_array( $document ) || empty( $document['authorization_endpoint'] ) || empty( $document['token_endpoint'] ) ) {
			return null;
		}

		return $document;
	}

	/**
	 * Generate a PKCE pair: high-entropy code_verifier plus its S256 challenge.
	 *
	 * @return array{verifier:string,challenge:string}
	 */
	public static function generate_pkce_pair() {
		$verifier  = self::base64url_encode( random_bytes( 32 ) ); // 43 chars, RFC 7636 compliant.
		$challenge = self::base64url_encode( hash( 'sha256', $verifier, true ) );
		return array(
			'verifier'  => $verifier,
			'challenge' => $challenge,
		);
	}

	/**
	 * Build the IAM authorize URL for the given state/challenge/nonce.
	 */
	public function build_authorize_url( $state, $code_challenge, $nonce ) {
		$endpoints = $this->get_endpoints();

		$params = array(
			'client_id'             => $this->options['client_id'],
			'response_type'         => 'code',
			'scope'                 => 'openid profile email roles',
			'redirect_uri'          => $this->get_redirect_uri(),
			'state'                 => $state,
			'nonce'                 => $nonce,
			'code_challenge'        => $code_challenge,
			'code_challenge_method' => 'S256',
		);

		return $endpoints['authorization_endpoint'] . '?' . http_build_query( $params, '', '&', PHP_QUERY_RFC3986 );
	}

	/**
	 * Exchange an authorization code at the token endpoint
	 * (client_secret_post + PKCE code_verifier).
	 *
	 * @return array|WP_Error Token response (access_token, id_token, ...) or error.
	 */
	public function exchange_code( $code, $code_verifier ) {
		$endpoints = $this->get_endpoints();

		$response = wp_remote_post(
			$endpoints['token_endpoint'],
			array(
				'timeout' => 15,
				'body'    => array(
					'grant_type'    => 'authorization_code',
					'code'          => $code,
					'redirect_uri'  => $this->get_redirect_uri(),
					'client_id'     => $this->options['client_id'],
					'client_secret' => $this->options['client_secret'],
					'code_verifier' => $code_verifier,
				),
			)
		);

		if ( is_wp_error( $response ) ) {
			return $response;
		}

		$body = json_decode( wp_remote_retrieve_body( $response ), true );
		$status = wp_remote_retrieve_response_code( $response );

		if ( 200 !== $status || ! is_array( $body ) || empty( $body['access_token'] ) ) {
			// Never log or expose the response body: it may echo request parameters.
			$error = ( is_array( $body ) && ! empty( $body['error'] ) ) ? sanitize_text_field( $body['error'] ) : 'token_endpoint_error';
			return new WP_Error( 'jengo_iam_sso_token', $error );
		}

		return $body;
	}

	/**
	 * Fetch the userinfo document for an access token. This response is the
	 * source of truth for sub / email / name / roles.
	 *
	 * @return array|WP_Error
	 */
	public function get_userinfo( $access_token ) {
		$endpoints = $this->get_endpoints();

		$response = wp_remote_get(
			$endpoints['userinfo_endpoint'],
			array(
				'timeout' => 15,
				'headers' => array(
					'Authorization' => 'Bearer ' . $access_token,
					'Accept'        => 'application/json',
				),
			)
		);

		if ( is_wp_error( $response ) ) {
			return $response;
		}

		$body = json_decode( wp_remote_retrieve_body( $response ), true );
		if ( 200 !== wp_remote_retrieve_response_code( $response ) || ! is_array( $body ) || empty( $body['sub'] ) ) {
			return new WP_Error( 'jengo_iam_sso_userinfo', 'userinfo_endpoint_error' );
		}

		return $body;
	}

	/**
	 * Decode a JWT payload WITHOUT signature verification. Only used for the
	 * nonce/sub consistency check on an id_token that was received directly
	 * from the token endpoint over TLS (so its origin is already trusted);
	 * identity data itself always comes from the userinfo endpoint.
	 *
	 * @return array|null
	 */
	public static function decode_jwt_payload( $jwt ) {
		if ( ! is_string( $jwt ) ) {
			return null;
		}
		$parts = explode( '.', $jwt );
		if ( 3 !== count( $parts ) ) {
			return null;
		}
		$payload = json_decode( self::base64url_decode( $parts[1] ), true );
		return is_array( $payload ) ? $payload : null;
	}

	public static function base64url_encode( $data ) {
		return rtrim( strtr( base64_encode( $data ), '+/', '-_' ), '=' );
	}

	public static function base64url_decode( $data ) {
		$remainder = strlen( $data ) % 4;
		if ( $remainder ) {
			$data .= str_repeat( '=', 4 - $remainder );
		}
		return base64_decode( strtr( $data, '-_', '+/' ) );
	}
}
