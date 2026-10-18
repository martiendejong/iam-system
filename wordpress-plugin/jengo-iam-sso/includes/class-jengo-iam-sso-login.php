<?php
/**
 * Login-flow glue: the "Log in with Jengo IAM" button, the forced-IAM login
 * mode (with emergency bypass), the OIDC callback, user matching/provisioning
 * and IAM → WordPress role mapping.
 */

if ( ! defined( 'ABSPATH' ) ) {
	exit;
}

class Jengo_IAM_SSO_Login {

	const STATE_TRANSIENT_PREFIX = 'jengo_iam_sso_state_';
	const STATE_TTL              = 10 * MINUTE_IN_SECONDS;
	const BYPASS_COOKIE          = 'jengo_iam_sso_bypass';
	const BYPASS_COOKIE_TTL      = 15 * MINUTE_IN_SECONDS;
	const SUB_META_KEY           = 'jengo_iam_sub';

	/**
	 * WordPress roles the IAM app-role catalog can map onto, in precedence
	 * order (highest first). IAM roles are named "{client_id}:{wp_role}".
	 */
	const MAPPABLE_ROLES = array( 'administrator', 'editor', 'author', 'contributor', 'subscriber' );

	/** The one account role sync must never touch (break-glass admin). */
	const PROTECTED_LOGIN = 'jengo-admin';

	public function __construct() {
		add_action( 'login_init', array( $this, 'maybe_force_iam_login' ), 1 );
		add_action( 'login_form_jengo-iam-auth', array( $this, 'start_authorization' ) );
		add_action( 'login_form_jengo-iam-callback', array( $this, 'handle_callback' ) );
		add_filter( 'login_message', array( $this, 'render_login_button' ) );
	}

	private function options() {
		return jengo_iam_sso_get_options();
	}

	private function oidc() {
		return new Jengo_IAM_SSO_OIDC_Client( $this->options() );
	}

	/**
	 * Whether the plugin is configured well enough to attempt a login.
	 */
	private function is_configured() {
		$options = $this->options();
		return ! empty( $options['issuer'] ) && ! empty( $options['client_id'] ) && ! empty( $options['client_secret'] );
	}

	/* ---------------------------------------------------------------------
	 * Login mode behaviour
	 * ------------------------------------------------------------------ */

	/**
	 * 'iam' mode: wp-login.php redirects straight to the IAM authorize
	 * endpoint — except for the plugin's own actions, logout/postpass, POSTs
	 * (credential submissions under bypass), and the emergency bypass secret.
	 * The bypass prevents admin lockout when IAM is down or misconfigured:
	 * wp-login.php?jengo_sso_bypass=<secret> shows the normal form.
	 */
	public function maybe_force_iam_login() {
		$options = $this->options();

		if ( 'iam' !== $options['login_mode'] || ! $this->is_configured() ) {
			return;
		}

		// Never intercept form submissions (e.g. the bypassed login form POST).
		if ( isset( $_SERVER['REQUEST_METHOD'] ) && 'GET' !== strtoupper( sanitize_text_field( wp_unslash( $_SERVER['REQUEST_METHOD'] ) ) ) ) {
			return;
		}

		// Plugin actions and flows that must keep working without IAM.
		$action = isset( $_REQUEST['action'] ) ? sanitize_key( wp_unslash( $_REQUEST['action'] ) ) : 'login';
		$exempt = array( 'jengo-iam-callback', 'jengo-iam-auth', 'logout', 'postpass' );
		if ( in_array( $action, $exempt, true ) ) {
			return;
		}

		if ( $this->is_bypass_active( $options ) ) {
			return;
		}

		$this->start_authorization();
	}

	/**
	 * Emergency bypass check: a matching ?jengo_sso_bypass=<secret> query
	 * parameter, or the short-lived cookie it sets (so the subsequently
	 * POSTed login form and follow-up GETs keep working without carrying
	 * the secret around in every URL).
	 */
	private function is_bypass_active( array $options ) {
		if ( empty( $options['bypass_secret'] ) ) {
			return false;
		}

		$cookie_value = wp_hash( $options['bypass_secret'] . '|jengo-iam-sso-bypass' );

		if ( isset( $_GET['jengo_sso_bypass'] ) ) {
			$provided = sanitize_text_field( wp_unslash( $_GET['jengo_sso_bypass'] ) );
			if ( hash_equals( $options['bypass_secret'], $provided ) ) {
				setcookie( self::BYPASS_COOKIE, $cookie_value, time() + self::BYPASS_COOKIE_TTL, '/', '', is_ssl(), true );
				$_COOKIE[ self::BYPASS_COOKIE ] = $cookie_value;
				return true;
			}
			return false;
		}

		if ( isset( $_COOKIE[ self::BYPASS_COOKIE ] ) ) {
			$cookie = sanitize_text_field( wp_unslash( $_COOKIE[ self::BYPASS_COOKIE ] ) );
			return hash_equals( $cookie_value, $cookie );
		}

		return false;
	}

	/**
	 * 'both' mode (default): the normal wp-login form plus a prominent
	 * "Log in with Jengo IAM" button above it. Also shown on the bypassed
	 * form in 'iam' mode; fully absent in 'wordpress' mode.
	 */
	public function render_login_button( $message ) {
		$options = $this->options();

		if ( 'wordpress' === $options['login_mode'] || ! $this->is_configured() ) {
			return $message;
		}

		$url = add_query_arg( 'action', 'jengo-iam-auth', wp_login_url() );
		if ( isset( $_REQUEST['redirect_to'] ) ) {
			$url = add_query_arg( 'redirect_to', rawurlencode( esc_url_raw( wp_unslash( $_REQUEST['redirect_to'] ) ) ), $url );
		}

		$button  = '<div style="margin:0 0 20px;padding:20px;background:#fff;border:1px solid #c3c4c7;box-shadow:0 1px 3px rgba(0,0,0,.04);text-align:center;">';
		$button .= '<a href="' . esc_url( $url ) . '" class="button button-primary button-large" style="width:100%;">'
			. esc_html__( 'Log in with Jengo IAM', 'jengo-iam-sso' ) . '</a>';
		$button .= '<p style="margin:12px 0 0;color:#646970;">' . esc_html__( 'or use your WordPress credentials below', 'jengo-iam-sso' ) . '</p>';
		$button .= '</div>';

		return $message . $button;
	}

	/* ---------------------------------------------------------------------
	 * Authorization request
	 * ------------------------------------------------------------------ */

	/**
	 * Start the OIDC flow: mint state + PKCE pair + nonce, persist them in a
	 * short-lived transient keyed by the state value, redirect to IAM.
	 */
	public function start_authorization() {
		if ( ! $this->is_configured() ) {
			$this->fail( __( 'Jengo IAM SSO is not configured yet (issuer, client ID and client secret are required).', 'jengo-iam-sso' ) );
		}

		$state = bin2hex( random_bytes( 16 ) );
		$nonce = bin2hex( random_bytes( 16 ) );
		$pkce  = Jengo_IAM_SSO_OIDC_Client::generate_pkce_pair();

		$redirect_to = '';
		if ( isset( $_REQUEST['redirect_to'] ) ) {
			$redirect_to = esc_url_raw( wp_unslash( $_REQUEST['redirect_to'] ) );
		}

		set_transient(
			self::STATE_TRANSIENT_PREFIX . $state,
			array(
				'verifier'    => $pkce['verifier'],
				'nonce'       => $nonce,
				'redirect_to' => $redirect_to,
				'created'     => time(),
			),
			self::STATE_TTL
		);

		// wp_safe_redirect() would strip the external IAM host; the authorize
		// URL is built by us from the configured issuer, not from user input.
		wp_redirect( $this->oidc()->build_authorize_url( $state, $pkce['challenge'], $nonce ) ); // phpcs:ignore WordPress.Security.SafeRedirect
		exit;
	}

	/* ---------------------------------------------------------------------
	 * Callback
	 * ------------------------------------------------------------------ */

	/**
	 * wp-login.php?action=jengo-iam-callback — the registered redirect URI.
	 * Validates state (single-use), exchanges the code, fetches userinfo and
	 * signs the matched/provisioned WordPress user in.
	 */
	public function handle_callback() {
		if ( ! $this->is_configured() ) {
			$this->fail( __( 'Jengo IAM SSO is not configured.', 'jengo-iam-sso' ) );
		}

		if ( isset( $_GET['error'] ) ) {
			$this->fail(
				sprintf(
					/* translators: %s: OAuth error code returned by the identity provider. */
					__( 'Jengo IAM returned an error: %s', 'jengo-iam-sso' ),
					sanitize_text_field( wp_unslash( $_GET['error'] ) )
				)
			);
		}

		$code  = isset( $_GET['code'] ) ? sanitize_text_field( wp_unslash( $_GET['code'] ) ) : '';
		$state = isset( $_GET['state'] ) ? sanitize_text_field( wp_unslash( $_GET['state'] ) ) : '';

		if ( '' === $code || '' === $state || ! preg_match( '/^[a-f0-9]{32}$/', $state ) ) {
			$this->fail( __( 'Invalid sign-in response (missing code or state).', 'jengo-iam-sso' ) );
		}

		// State is single-use: load and delete in one go. A replayed or
		// expired state never reaches the token endpoint.
		$stored = get_transient( self::STATE_TRANSIENT_PREFIX . $state );
		delete_transient( self::STATE_TRANSIENT_PREFIX . $state );

		if ( ! is_array( $stored ) || empty( $stored['verifier'] ) || empty( $stored['nonce'] ) ) {
			$this->fail( __( 'Your sign-in attempt expired or was already used. Please try again.', 'jengo-iam-sso' ) );
		}

		$oidc   = $this->oidc();
		$tokens = $oidc->exchange_code( $code, $stored['verifier'] );
		if ( is_wp_error( $tokens ) ) {
			$this->fail( __( 'Could not exchange the authorization code with Jengo IAM.', 'jengo-iam-sso' ) );
		}

		// Userinfo is the source of truth for identity and roles.
		$userinfo = $oidc->get_userinfo( $tokens['access_token'] );
		if ( is_wp_error( $userinfo ) ) {
			$this->fail( __( 'Could not fetch your profile from Jengo IAM.', 'jengo-iam-sso' ) );
		}

		$sub = sanitize_text_field( (string) $userinfo['sub'] );
		if ( '' === $sub ) {
			$this->fail( __( 'Jengo IAM did not return a subject identifier.', 'jengo-iam-sso' ) );
		}

		// Nonce / subject consistency: when an id_token is present its nonce
		// must match the one minted with this state, and its sub must equal
		// the userinfo sub (both received over TLS from the token endpoint).
		if ( ! empty( $tokens['id_token'] ) ) {
			$payload = Jengo_IAM_SSO_OIDC_Client::decode_jwt_payload( $tokens['id_token'] );
			if ( null === $payload ) {
				$this->fail( __( 'Invalid identity token received from Jengo IAM.', 'jengo-iam-sso' ) );
			}
			if ( empty( $payload['nonce'] ) || ! hash_equals( $stored['nonce'], (string) $payload['nonce'] ) ) {
				$this->fail( __( 'Sign-in security check failed (nonce mismatch). Please try again.', 'jengo-iam-sso' ) );
			}
			if ( empty( $payload['sub'] ) || ! hash_equals( (string) $payload['sub'], $sub ) ) {
				$this->fail( __( 'Sign-in security check failed (subject mismatch). Please try again.', 'jengo-iam-sso' ) );
			}
		}

		$email = isset( $userinfo['email'] ) ? sanitize_email( (string) $userinfo['email'] ) : '';
		$name  = isset( $userinfo['name'] ) ? sanitize_text_field( (string) $userinfo['name'] ) : '';

		$mapped_roles = $this->map_iam_roles( $userinfo );

		$user = $this->match_user( $sub, $email );

		if ( ! $user ) {
			$options = $this->options();

			if ( empty( $options['auto_provision'] ) ) {
				$this->fail( __( 'No WordPress account is linked to your Jengo IAM identity, and automatic account creation is disabled.', 'jengo-iam-sso' ) );
			}

			// Auto-provision applies but IAM granted none of the mappable
			// roles for this site: refuse, do not create an account.
			if ( empty( $mapped_roles ) ) {
				$this->fail( __( 'You have no access granted in Jengo IAM for this site. Ask an administrator to grant you a role in the IAM access matrix.', 'jengo-iam-sso' ) );
			}

			$user = $this->provision_user( $email, $name, $mapped_roles, $options );
		}

		// Link the IAM subject for next time (idempotent).
		update_user_meta( $user->ID, self::SUB_META_KEY, $sub );

		$this->sync_roles( $user, $mapped_roles );

		// Sign in.
		wp_set_current_user( $user->ID, $user->user_login );
		wp_set_auth_cookie( $user->ID, false, is_ssl() );
		do_action( 'wp_login', $user->user_login, $user );

		$redirect_to = ! empty( $stored['redirect_to'] ) ? $stored['redirect_to'] : admin_url();
		wp_safe_redirect( wp_validate_redirect( $redirect_to, admin_url() ) );
		exit;
	}

	/* ---------------------------------------------------------------------
	 * User matching, provisioning, role mapping
	 * ------------------------------------------------------------------ */

	/**
	 * Matching order: (1) user already linked via the jengo_iam_sub usermeta,
	 * (2) user with the same (verified-at-IAM) email address.
	 *
	 * @return WP_User|null
	 */
	private function match_user( $sub, $email ) {
		$by_sub = get_users(
			array(
				'meta_key'    => self::SUB_META_KEY,
				'meta_value'  => $sub,
				'number'      => 1,
				'count_total' => false,
			)
		);
		if ( ! empty( $by_sub ) ) {
			return $by_sub[0];
		}

		if ( '' !== $email ) {
			$by_email = get_user_by( 'email', $email );
			if ( $by_email instanceof WP_User ) {
				return $by_email;
			}
		}

		return null;
	}

	/**
	 * Create a WordPress user for a first-time IAM login. Login name is the
	 * sanitized local part of the email address, deduplicated with a numeric
	 * suffix; the password is random and never communicated (SSO-only unless
	 * the user resets it).
	 *
	 * @return WP_User
	 */
	private function provision_user( $email, $name, array $mapped_roles, array $options ) {
		if ( '' === $email || ! is_email( $email ) ) {
			$this->fail( __( 'Jengo IAM did not return a valid email address, so no account could be created.', 'jengo-iam-sso' ) );
		}

		$local = sanitize_user( strtolower( substr( $email, 0, strpos( $email, '@' ) ) ), true );
		if ( '' === $local ) {
			$local = 'jengo-user';
		}

		$login  = $local;
		$suffix = 2;
		while ( username_exists( $login ) ) {
			$login = $local . $suffix;
			$suffix++;
		}

		// With sync enabled the mapped IAM role wins; otherwise new users get
		// the configured default role.
		$role = ( ! empty( $options['sync_roles'] ) && ! empty( $mapped_roles ) )
			? $mapped_roles[0]
			: $options['default_role'];

		$user_id = wp_insert_user(
			array(
				'user_login'   => $login,
				'user_pass'    => wp_generate_password( 32, true, true ),
				'user_email'   => $email,
				'display_name' => '' !== $name ? $name : $login,
				'role'         => $role,
			)
		);

		if ( is_wp_error( $user_id ) ) {
			$this->fail( __( 'Your WordPress account could not be created. Please contact an administrator.', 'jengo-iam-sso' ) );
		}

		return get_user_by( 'id', $user_id );
	}

	/**
	 * Extract the WordPress roles granted via IAM. IAM app roles are named
	 * "{client_id}:{role}"; only the standard WP role names map. Returns the
	 * mapped roles in precedence order (administrator first).
	 *
	 * The userinfo endpoint emits the roles array under the OIDC "role" claim
	 * when the "roles" scope is granted; "roles" is accepted as an alias.
	 *
	 * @return string[]
	 */
	private function map_iam_roles( array $userinfo ) {
		$options = $this->options();
		$prefix  = strtolower( $options['client_id'] ) . ':';

		$claim = array();
		foreach ( array( 'role', 'roles' ) as $key ) {
			if ( isset( $userinfo[ $key ] ) ) {
				$claim = array_merge( $claim, (array) $userinfo[ $key ] );
			}
		}

		$granted = array();
		foreach ( $claim as $iam_role ) {
			if ( ! is_string( $iam_role ) ) {
				continue;
			}
			$iam_role = strtolower( $iam_role );
			if ( 0 !== strpos( $iam_role, $prefix ) ) {
				continue;
			}
			$wp_role = substr( $iam_role, strlen( $prefix ) );
			if ( in_array( $wp_role, self::MAPPABLE_ROLES, true ) ) {
				$granted[ $wp_role ] = true;
			}
		}

		$ordered = array();
		foreach ( self::MAPPABLE_ROLES as $wp_role ) {
			if ( isset( $granted[ $wp_role ] ) ) {
				$ordered[] = $wp_role;
			}
		}

		return $ordered;
	}

	/**
	 * Sync the WordPress role from IAM on every SSO login (when enabled).
	 * Users without any mapped IAM role keep their existing WordPress role
	 * (they only reach this point when their account pre-existed). The
	 * break-glass 'jengo-admin' account is never modified.
	 */
	private function sync_roles( WP_User $user, array $mapped_roles ) {
		$options = $this->options();

		if ( empty( $options['sync_roles'] ) || empty( $mapped_roles ) ) {
			return;
		}

		if ( self::PROTECTED_LOGIN === $user->user_login ) {
			return;
		}

		$target = $mapped_roles[0];
		if ( ! in_array( $target, $user->roles, true ) || count( $user->roles ) > 1 ) {
			$user->set_role( $target );
		}
	}

	/**
	 * Abort the login flow with a user-facing error. wp_die (instead of a
	 * redirect back to wp-login.php) deliberately avoids redirect loops in
	 * 'iam' mode. No tokens, secrets or raw provider responses are included.
	 */
	private function fail( $message ) {
		wp_die(
			esc_html( $message ),
			esc_html__( 'Jengo IAM sign-in', 'jengo-iam-sso' ),
			array(
				'response'  => 403,
				'link_url'  => esc_url( wp_login_url() ),
				'link_text' => esc_html__( 'Back to login', 'jengo-iam-sso' ),
			)
		);
	}
}
