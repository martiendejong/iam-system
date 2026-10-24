<?php
/**
 * id_token validation (OpenID Connect Core 3.1.3.7).
 *
 * A login is only ever completed from an id_token that passed every check in
 * validate_id_token(): RS256 signature against the IAM JWKS, issuer, audience
 * (and azp when several audiences), expiry / issued-at, nonce and a subject.
 * Pure PHP + the OpenSSL extension, no JOSE library.
 */

if ( ! defined( 'ABSPATH' ) ) {
	exit;
}

class Jengo_IAM_SSO_Token_Validator {

	/** Clock skew tolerated on exp / iat / nbf, in seconds. */
	const LEEWAY = 120;

	/** Upper bound on the compact JWT size we are willing to parse. */
	const MAX_TOKEN_LENGTH = 16384;

	/** Smallest RSA modulus we accept from a JWKS, in bytes (2048 bits). */
	const MIN_MODULUS_BYTES = 256;

	/**
	 * Validate an id_token and return its claims.
	 *
	 * @param string   $jwt           Compact serialization (header.payload.signature).
	 * @param callable $jwks_provider function( bool $force_refresh ): array|WP_Error
	 *                                returning a decoded JWKS ("keys" => [...]).
	 *                                Called a second time with true when the
	 *                                token's kid is not in the cached set.
	 * @param array    $expected      issuer, client_id, nonce (all required).
	 * @param int|null $now           Unix time; defaults to time() (tests inject it).
	 *
	 * @return array|WP_Error Claims on success; WP_Error whose code is
	 *                        'id_token_<reason>' otherwise.
	 */
	public static function validate_id_token( $jwt, $jwks_provider, array $expected, $now = null ) {
		$now = null === $now ? time() : (int) $now;

		if ( ! is_string( $jwt ) || '' === $jwt || strlen( $jwt ) > self::MAX_TOKEN_LENGTH ) {
			return self::error( 'malformed' );
		}

		$parts = explode( '.', $jwt );
		if ( 3 !== count( $parts ) ) {
			return self::error( 'malformed' );
		}

		$header_raw  = self::b64url_decode( $parts[0] );
		$payload_raw = self::b64url_decode( $parts[1] );
		$signature   = self::b64url_decode( $parts[2] );
		if ( false === $header_raw || false === $payload_raw || false === $signature || '' === $signature ) {
			return self::error( 'malformed' );
		}

		$header = json_decode( $header_raw, true );
		$claims = json_decode( $payload_raw, true );
		if ( ! is_array( $header ) || ! is_array( $claims ) ) {
			return self::error( 'malformed' );
		}

		// Only RS256 is accepted: never "none", never an HMAC (which would let a
		// public key double as the shared secret).
		if ( ! isset( $header['alg'] ) || 'RS256' !== $header['alg'] ) {
			return self::error( 'unsupported_alg' );
		}

		$kid = isset( $header['kid'] ) && is_string( $header['kid'] ) ? $header['kid'] : '';

		$key = self::find_key( $jwks_provider, $kid );
		if ( is_wp_error( $key ) ) {
			return $key;
		}

		$pem = self::jwk_to_pem( $key );
		if ( null === $pem ) {
			return self::error( 'unusable_key' );
		}

		if ( ! function_exists( 'openssl_verify' ) ) {
			return self::error( 'openssl_missing' );
		}

		$verified = openssl_verify( $parts[0] . '.' . $parts[1], $signature, $pem, OPENSSL_ALGO_SHA256 );
		if ( 1 !== $verified ) {
			return self::error( 'bad_signature' );
		}

		// Signature is good: the claims are what IAM issued. Check they are
		// meant for this site, this flow and this moment.
		$issuer = isset( $expected['issuer'] ) ? (string) $expected['issuer'] : '';
		if ( '' === $issuer || ! isset( $claims['iss'] ) || ! is_string( $claims['iss'] )
			|| untrailingslashit( $claims['iss'] ) !== untrailingslashit( $issuer ) ) {
			return self::error( 'bad_issuer' );
		}

		$client_id = isset( $expected['client_id'] ) ? (string) $expected['client_id'] : '';
		$audiences = isset( $claims['aud'] ) ? $claims['aud'] : array();
		$audiences = is_array( $audiences ) ? $audiences : array( $audiences );
		foreach ( $audiences as $audience ) {
			if ( ! is_string( $audience ) ) {
				return self::error( 'bad_audience' );
			}
		}
		if ( '' === $client_id || ! in_array( $client_id, $audiences, true ) ) {
			return self::error( 'bad_audience' );
		}
		if ( count( $audiences ) > 1 && ( ! isset( $claims['azp'] ) || $claims['azp'] !== $client_id ) ) {
			return self::error( 'bad_azp' );
		}

		if ( ! isset( $claims['exp'] ) || ! is_numeric( $claims['exp'] ) || (int) $claims['exp'] + self::LEEWAY < $now ) {
			return self::error( 'expired' );
		}
		if ( isset( $claims['iat'] ) && ( ! is_numeric( $claims['iat'] ) || (int) $claims['iat'] - self::LEEWAY > $now ) ) {
			return self::error( 'not_yet_valid' );
		}
		if ( isset( $claims['nbf'] ) && ( ! is_numeric( $claims['nbf'] ) || (int) $claims['nbf'] - self::LEEWAY > $now ) ) {
			return self::error( 'not_yet_valid' );
		}

		$nonce = isset( $expected['nonce'] ) ? (string) $expected['nonce'] : '';
		if ( '' === $nonce || ! isset( $claims['nonce'] ) || ! is_string( $claims['nonce'] ) || ! hash_equals( $nonce, $claims['nonce'] ) ) {
			return self::error( 'bad_nonce' );
		}

		if ( ! isset( $claims['sub'] ) || ! is_string( $claims['sub'] ) || '' === $claims['sub'] || strlen( $claims['sub'] ) > 255 ) {
			return self::error( 'missing_sub' );
		}

		return $claims;
	}

	/**
	 * Pick the signing key for a token: by kid when the token names one,
	 * otherwise the only RSA signing key in the set. Re-fetches the JWKS once
	 * when the key is unknown (key rotation).
	 *
	 * @return array|WP_Error
	 */
	private static function find_key( $jwks_provider, $kid ) {
		for ( $attempt = 0; $attempt < 2; $attempt++ ) {
			$jwks = call_user_func( $jwks_provider, 1 === $attempt );
			if ( is_wp_error( $jwks ) || ! is_array( $jwks ) || empty( $jwks['keys'] ) || ! is_array( $jwks['keys'] ) ) {
				if ( 1 === $attempt ) {
					return self::error( 'jwks_unavailable' );
				}
				continue;
			}

			$candidates = array();
			foreach ( $jwks['keys'] as $candidate ) {
				if ( ! is_array( $candidate ) || ! isset( $candidate['kty'] ) || 'RSA' !== $candidate['kty'] ) {
					continue;
				}
				if ( isset( $candidate['use'] ) && 'sig' !== $candidate['use'] ) {
					continue;
				}
				if ( isset( $candidate['alg'] ) && 'RS256' !== $candidate['alg'] ) {
					continue;
				}
				$candidates[] = $candidate;
			}

			foreach ( $candidates as $candidate ) {
				if ( '' !== $kid && isset( $candidate['kid'] ) && $candidate['kid'] === $kid ) {
					return $candidate;
				}
			}
			if ( '' === $kid && 1 === count( $candidates ) ) {
				return $candidates[0];
			}
		}

		return self::error( 'unknown_key' );
	}

	/**
	 * Convert an RSA JWK (n, e) to a PEM SubjectPublicKeyInfo, or null when the
	 * key is malformed or smaller than 2048 bits.
	 */
	public static function jwk_to_pem( array $jwk ) {
		if ( empty( $jwk['n'] ) || empty( $jwk['e'] ) || ! is_string( $jwk['n'] ) || ! is_string( $jwk['e'] ) ) {
			return null;
		}

		$modulus  = self::b64url_decode( $jwk['n'] );
		$exponent = self::b64url_decode( $jwk['e'] );
		if ( false === $modulus || false === $exponent || '' === $modulus || '' === $exponent ) {
			return null;
		}
		if ( strlen( ltrim( $modulus, "\x00" ) ) < self::MIN_MODULUS_BYTES ) {
			return null;
		}

		$rsa_public_key = "\x30" . self::der_length( strlen( self::der_integer( $modulus ) . self::der_integer( $exponent ) ) )
			. self::der_integer( $modulus ) . self::der_integer( $exponent );

		// AlgorithmIdentifier: rsaEncryption (1.2.840.113549.1.1.1) with NULL parameters.
		$algorithm = hex2bin( '300d06092a864886f70d0101010500' );
		$bit_string = "\x03" . self::der_length( strlen( $rsa_public_key ) + 1 ) . "\x00" . $rsa_public_key;
		$spki       = "\x30" . self::der_length( strlen( $algorithm ) + strlen( $bit_string ) ) . $algorithm . $bit_string;

		return "-----BEGIN PUBLIC KEY-----\n" . chunk_split( base64_encode( $spki ), 64, "\n" ) . "-----END PUBLIC KEY-----\n";
	}

	private static function der_length( $length ) {
		if ( $length < 0x80 ) {
			return chr( $length );
		}
		$bytes = '';
		while ( $length > 0 ) {
			$bytes  = chr( $length & 0xff ) . $bytes;
			$length >>= 8;
		}
		return chr( 0x80 | strlen( $bytes ) ) . $bytes;
	}

	private static function der_integer( $bytes ) {
		$bytes = ltrim( $bytes, "\x00" );
		if ( '' === $bytes ) {
			$bytes = "\x00";
		}
		if ( ord( $bytes[0] ) & 0x80 ) {
			$bytes = "\x00" . $bytes;
		}
		return "\x02" . self::der_length( strlen( $bytes ) ) . $bytes;
	}

	/**
	 * Strict base64url decode; false on any character outside the alphabet.
	 *
	 * @return string|false
	 */
	public static function b64url_decode( $data ) {
		if ( ! is_string( $data ) || '' === $data || ! preg_match( '/^[A-Za-z0-9_-]+$/', $data ) ) {
			return false;
		}
		$remainder = strlen( $data ) % 4;
		if ( 1 === $remainder ) {
			return false;
		}
		if ( $remainder ) {
			$data .= str_repeat( '=', 4 - $remainder );
		}
		return base64_decode( strtr( $data, '-_', '+/' ), true );
	}

	private static function error( $reason ) {
		return new WP_Error( 'id_token_' . $reason, $reason );
	}
}
