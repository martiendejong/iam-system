<?php
/**
 * Minimal OpenID Connect client for Jengo IAM.
 *
 * Authorization-code flow with PKCE (S256 only), client_secret_post at the
 * token endpoint. Everything is driven by the OIDC discovery document
 * ({issuer}/.well-known/openid-configuration): there are deliberately NO
 * guessed fallback URLs, so a login never redirects into a host the discovery
 * document did not vouch for. When discovery is unreachable or invalid,
 * is_available() is false and callers fall back to the WordPress form.
 *
 * Pure PHP + WordPress HTTP API; no composer dependencies.
 */

if ( ! defined( 'ABSPATH' ) ) {
	exit;
}

class Jengo_IAM_SSO_OIDC_Client {

	const DISCOVERY_CACHE_TTL   = 12 * HOUR_IN_SECONDS;
	const DISCOVERY_FAILURE_TTL = 60;
	const REACHABILITY_TTL      = 60;
	const JWKS_CACHE_TTL        = HOUR_IN_SECONDS;
	const JWKS_REFETCH_LOCK_TTL = 30;

	/** @var array Plugin options (see jengo_iam_sso_get_options()). */
	private $options;

	/** @var array|WP_Error|null Discovery result memoised for this request. */
	private $discovery = null;

	public function __construct( array $options ) {
		$this->options = $options;
	}

	/**
	 * The redirect URI registered at IAM. Must match the OAuth client's
	 * RedirectUris entry byte-for-byte, query string included.
	 */
	public static function redirect_uri() {
		return add_query_arg( 'action', 'jengo-iam-callback', wp_login_url() );
	}

	/**
	 * The post-logout redirect URI (the site home page). Must be registered in
	 * the IAM client's PostLogoutRedirectUris to be honoured.
	 */
	public static function post_logout_redirect_uri() {
		return home_url( '/' );
	}

	public function get_issuer() {
		return untrailingslashit( isset( $this->options['issuer'] ) ? (string) $this->options['issuer'] : '' );
	}

	/**
	 * Discovery document reduced to the fields the plugin uses, or WP_Error.
	 *
	 * Validated: HTTP 200, JSON, "issuer" equals the configured issuer (trailing
	 * slash ignored, OIDC Discovery 4.3), and the authorization, token and JWKS
	 * endpoints are https URLs. A failure is cached for a minute so a down IAM
	 * does not slow every login page down.
	 *
	 * @return array|WP_Error
	 */
	public function get_discovery() {
		if ( null !== $this->discovery ) {
			return $this->discovery;
		}

		$issuer = $this->get_issuer();
		if ( '' === $issuer || 0 !== strpos( $issuer, 'https://' ) ) {
			return $this->discovery = new WP_Error( 'jengo_iam_sso_discovery', 'issuer_not_https' );
		}

		$cache_key = 'jengo_iam_sso_disco_' . md5( $issuer );
		$cached    = get_transient( $cache_key );
		if ( is_array( $cached ) ) {
			return $this->discovery = $cached;
		}
		if ( false !== get_transient( $cache_key . '_fail' ) ) {
			return $this->discovery = new WP_Error( 'jengo_iam_sso_discovery', 'discovery_unavailable' );
		}

		$document = $this->fetch_discovery_document( $issuer );
		if ( is_wp_error( $document ) ) {
			set_transient( $cache_key . '_fail', 1, self::DISCOVERY_FAILURE_TTL );
			set_transient( $this->reachability_key(), '0', self::REACHABILITY_TTL );
			return $this->discovery = $document;
		}

		set_transient( $cache_key, $document, self::DISCOVERY_CACHE_TTL );
		set_transient( $this->reachability_key(), '1', self::REACHABILITY_TTL );
		return $this->discovery = $document;
	}

	/**
	 * Whether a usable discovery document is known (configuration level). This
	 * can stay true for hours after IAM went down, because the document itself
	 * is cached; use is_reachable() before sending a visitor to IAM.
	 */
	public function is_available() {
		return ! is_wp_error( $this->get_discovery() );
	}

	/**
	 * Whether IAM answers right now. The cached discovery document says where
	 * IAM lives, not that it is up, so IAM-only mode (which redirects every
	 * wp-login.php visit) asks this instead: a live fetch of the discovery
	 * document, remembered for a minute so a login page view costs at most one
	 * request per minute and a down IAM never slows pages down.
	 */
	public function is_reachable() {
		if ( is_wp_error( $this->get_discovery() ) ) {
			return false;
		}

		$known = get_transient( $this->reachability_key() );
		if ( false !== $known ) {
			return '1' === $known;
		}

		$live = $this->fetch_discovery_document( $this->get_issuer() );
		$up   = ! is_wp_error( $live );
		set_transient( $this->reachability_key(), $up ? '1' : '0', self::REACHABILITY_TTL );
		return $up;
	}

	private function reachability_key() {
		return 'jengo_iam_sso_up_' . md5( $this->get_issuer() );
	}

	/**
	 * Drop cached discovery/JWKS state for the configured issuer (settings save).
	 */
	public function flush_cache() {
		$issuer    = $this->get_issuer();
		$cache_key = 'jengo_iam_sso_disco_' . md5( $issuer );
		$document  = get_transient( $cache_key );
		delete_transient( $cache_key );
		delete_transient( $cache_key . '_fail' );
		delete_transient( $this->reachability_key() );
		if ( is_array( $document ) && ! empty( $document['jwks_uri'] ) ) {
			delete_transient( 'jengo_iam_sso_jwks_' . md5( $document['jwks_uri'] ) );
		}
		$this->discovery = null;
	}

	/**
	 * @return array|WP_Error
	 */
	private function fetch_discovery_document( $issuer ) {
		$response = wp_remote_get(
			$issuer . '/.well-known/openid-configuration',
			array(
				'timeout'     => 5,
				'redirection' => 0,
			)
		);

		if ( is_wp_error( $response ) || 200 !== (int) wp_remote_retrieve_response_code( $response ) ) {
			return new WP_Error( 'jengo_iam_sso_discovery', 'discovery_unavailable' );
		}

		$document = json_decode( wp_remote_retrieve_body( $response ), true );
		if ( ! is_array( $document ) ) {
			return new WP_Error( 'jengo_iam_sso_discovery', 'discovery_invalid' );
		}

		if ( empty( $document['issuer'] ) || ! is_string( $document['issuer'] ) || untrailingslashit( $document['issuer'] ) !== $issuer ) {
			return new WP_Error( 'jengo_iam_sso_discovery', 'issuer_mismatch' );
		}

		$clean = array( 'issuer' => $document['issuer'] );
		foreach ( array( 'authorization_endpoint', 'token_endpoint', 'jwks_uri' ) as $required ) {
			if ( empty( $document[ $required ] ) || ! is_string( $document[ $required ] ) || 0 !== strpos( $document[ $required ], 'https://' ) ) {
				return new WP_Error( 'jengo_iam_sso_discovery', 'discovery_invalid' );
			}
			$clean[ $required ] = $document[ $required ];
		}

		// Optional: only present once the IAM server supports RP-initiated logout.
		$clean['end_session_endpoint'] = '';
		if ( ! empty( $document['end_session_endpoint'] ) && is_string( $document['end_session_endpoint'] ) && 0 === strpos( $document['end_session_endpoint'], 'https://' ) ) {
			$clean['end_session_endpoint'] = $document['end_session_endpoint'];
		}

		return $clean;
	}

	/**
	 * The IAM signing keys. Cached; a forced refresh is rate limited so an
	 * unknown kid cannot be used to hammer the JWKS endpoint.
	 *
	 * @param bool $force_refresh Ignore the cache (key rotation).
	 * @return array|WP_Error Decoded JWKS.
	 */
	public function get_jwks( $force_refresh = false ) {
		$discovery = $this->get_discovery();
		if ( is_wp_error( $discovery ) ) {
			return $discovery;
		}

		$cache_key = 'jengo_iam_sso_jwks_' . md5( $discovery['jwks_uri'] );
		$cached    = get_transient( $cache_key );

		if ( is_array( $cached ) && ! $force_refresh ) {
			return $cached;
		}
		if ( $force_refresh && is_array( $cached ) && false !== get_transient( $cache_key . '_lock' ) ) {
			return $cached;
		}

		$response = wp_remote_get(
			$discovery['jwks_uri'],
			array(
				'timeout'     => 10,
				'redirection' => 0,
			)
		);
		if ( is_wp_error( $response ) || 200 !== (int) wp_remote_retrieve_response_code( $response ) ) {
			return is_array( $cached ) ? $cached : new WP_Error( 'jengo_iam_sso_jwks', 'jwks_unavailable' );
		}

		$jwks = json_decode( wp_remote_retrieve_body( $response ), true );
		if ( ! is_array( $jwks ) || empty( $jwks['keys'] ) || ! is_array( $jwks['keys'] ) ) {
			return is_array( $cached ) ? $cached : new WP_Error( 'jengo_iam_sso_jwks', 'jwks_invalid' );
		}

		set_transient( $cache_key, $jwks, self::JWKS_CACHE_TTL );
		set_transient( $cache_key . '_lock', 1, self::JWKS_REFETCH_LOCK_TTL );
		return $jwks;
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
	 *
	 * @return string|WP_Error
	 */
	public function build_authorize_url( $state, $code_challenge, $nonce ) {
		$discovery = $this->get_discovery();
		if ( is_wp_error( $discovery ) ) {
			return $discovery;
		}

		$params = array(
			'client_id'             => $this->options['client_id'],
			'response_type'         => 'code',
			'scope'                 => 'openid profile email roles',
			'redirect_uri'          => self::redirect_uri(),
			'state'                 => $state,
			'nonce'                 => $nonce,
			'code_challenge'        => $code_challenge,
			'code_challenge_method' => 'S256',
		);

		return self::append_query( $discovery['authorization_endpoint'], $params );
	}

	/**
	 * Exchange an authorization code at the token endpoint
	 * (client_secret_post + PKCE code_verifier). The id_token is mandatory.
	 *
	 * @return array|WP_Error Token response or error.
	 */
	public function exchange_code( $code, $code_verifier ) {
		$discovery = $this->get_discovery();
		if ( is_wp_error( $discovery ) ) {
			return $discovery;
		}

		$response = wp_remote_post(
			$discovery['token_endpoint'],
			array(
				'timeout'     => 15,
				'redirection' => 0,
				'body'        => array(
					'grant_type'    => 'authorization_code',
					'code'          => $code,
					'redirect_uri'  => self::redirect_uri(),
					'client_id'     => $this->options['client_id'],
					'client_secret' => $this->options['client_secret'],
					'code_verifier' => $code_verifier,
				),
			)
		);

		if ( is_wp_error( $response ) ) {
			return new WP_Error( 'jengo_iam_sso_token', 'token_endpoint_unreachable' );
		}

		$body   = json_decode( wp_remote_retrieve_body( $response ), true );
		$status = (int) wp_remote_retrieve_response_code( $response );

		if ( 200 !== $status || ! is_array( $body ) || empty( $body['id_token'] ) || ! is_string( $body['id_token'] ) ) {
			// Never log or expose the response body: it may echo request parameters.
			$error = ( is_array( $body ) && ! empty( $body['error'] ) && is_string( $body['error'] ) ) ? sanitize_text_field( $body['error'] ) : 'token_endpoint_error';
			return new WP_Error( 'jengo_iam_sso_token', $error );
		}

		return $body;
	}

	/**
	 * RP-initiated logout URL, or '' when IAM does not advertise an
	 * end_session_endpoint (older IAM versions) or is unreachable.
	 */
	public function build_end_session_url( $post_logout_redirect_uri ) {
		$discovery = $this->get_discovery();
		if ( is_wp_error( $discovery ) || empty( $discovery['end_session_endpoint'] ) ) {
			return '';
		}

		return self::append_query(
			$discovery['end_session_endpoint'],
			array(
				'client_id'                => $this->options['client_id'],
				'post_logout_redirect_uri' => $post_logout_redirect_uri,
			)
		);
	}

	private static function append_query( $url, array $params ) {
		return $url . ( false === strpos( $url, '?' ) ? '?' : '&' ) . http_build_query( $params, '', '&', PHP_QUERY_RFC3986 );
	}

	public static function base64url_encode( $data ) {
		return rtrim( strtr( base64_encode( $data ), '+/', '-_' ), '=' );
	}
}
