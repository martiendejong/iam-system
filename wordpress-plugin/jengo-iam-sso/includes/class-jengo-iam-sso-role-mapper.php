<?php
/**
 * IAM role -> WordPress role mapping.
 *
 * Two sources, merged:
 *  1. the optional per-site map (settings textarea, one "iam_role = wp_role"
 *     per line, first line = highest priority), and
 *  2. the built-in naming convention "{client_id}:{wp_role}" for the five
 *     standard WordPress roles (what the IAM app-role catalog for this site
 *     creates), ranked administrator > editor > author > contributor > subscriber.
 *
 * Explicit map entries rank ahead of convention roles. Roles that do not exist
 * on this site are ignored.
 */

if ( ! defined( 'ABSPATH' ) ) {
	exit;
}

class Jengo_IAM_SSO_Role_Mapper {

	/** Standard WordPress roles in precedence order (highest first). */
	const STANDARD_ROLES = array( 'administrator', 'editor', 'author', 'contributor', 'subscriber' );

	/** Capabilities that make a role "privileged" (never a default role for new users). */
	const PRIVILEGED_CAPS = array( 'manage_options', 'promote_users', 'edit_users', 'delete_users', 'install_plugins' );

	/**
	 * Parse the settings textarea into iam_role (lower-case) => wp_role pairs,
	 * preserving line order. Blank lines and "#" comments are skipped.
	 *
	 * @return array<string,string>
	 */
	public static function parse_map( $text ) {
		$map = array();
		if ( ! is_string( $text ) || '' === trim( $text ) ) {
			return $map;
		}

		foreach ( preg_split( '/\r\n|\r|\n/', $text ) as $line ) {
			$line = trim( $line );
			if ( '' === $line || '#' === $line[0] ) {
				continue;
			}
			$pair = explode( '=', $line, 2 );
			if ( 2 !== count( $pair ) ) {
				continue;
			}
			$iam_role = strtolower( trim( $pair[0] ) );
			$wp_role  = sanitize_key( trim( $pair[1] ) );
			if ( '' === $iam_role || '' === $wp_role ) {
				continue;
			}
			if ( ! isset( $map[ $iam_role ] ) ) {
				$map[ $iam_role ] = $wp_role;
			}
		}

		return $map;
	}

	/**
	 * Roles granted to the user by IAM for this site, highest priority first,
	 * de-duplicated.
	 *
	 * @param array  $claims    Validated id_token claims ("role" and/or "roles").
	 * @param string $client_id The OAuth client id of this site.
	 * @param string $map_text  Raw role map setting.
	 * @return string[]
	 */
	public static function map_roles( array $claims, $client_id, $map_text = '' ) {
		$granted = array();
		foreach ( array( 'role', 'roles' ) as $key ) {
			if ( isset( $claims[ $key ] ) ) {
				foreach ( (array) $claims[ $key ] as $iam_role ) {
					if ( is_string( $iam_role ) && '' !== $iam_role ) {
						$granted[ strtolower( $iam_role ) ] = true;
					}
				}
			}
		}

		$ordered = array();

		// 1. Explicit map, in line order.
		foreach ( self::parse_map( $map_text ) as $iam_role => $wp_role ) {
			if ( isset( $granted[ $iam_role ] ) && null !== get_role( $wp_role ) && ! in_array( $wp_role, $ordered, true ) ) {
				$ordered[] = $wp_role;
			}
		}

		// 2. Naming convention "{client_id}:{wp_role}".
		$prefix = strtolower( (string) $client_id ) . ':';
		if ( ':' !== $prefix ) {
			foreach ( self::STANDARD_ROLES as $wp_role ) {
				if ( isset( $granted[ $prefix . $wp_role ] ) && null !== get_role( $wp_role ) && ! in_array( $wp_role, $ordered, true ) ) {
					$ordered[] = $wp_role;
				}
			}
		}

		return $ordered;
	}

	/**
	 * Whether a WordPress role can manage the site (administrator or any role
	 * carrying a user/plugin/option management capability).
	 */
	public static function is_privileged_role( $role_name ) {
		if ( 'administrator' === $role_name ) {
			return true;
		}
		$role = get_role( (string) $role_name );
		if ( null === $role ) {
			return false;
		}
		foreach ( self::PRIVILEGED_CAPS as $capability ) {
			if ( $role->has_cap( $capability ) ) {
				return true;
			}
		}
		return false;
	}
}
