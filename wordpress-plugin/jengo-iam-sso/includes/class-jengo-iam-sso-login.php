<?php
/**
 * Login-flow glue: the "Log in with Jengo IAM" button, the three login modes
 * (with emergency bypass), the OIDC callback, user matching/provisioning and
 * IAM -> WordPress role mapping, and the optional IAM logout.
 *
 * Security invariants (see the unit tests):
 *  - no WordPress session is ever issued without a fully validated id_token
 *    (RS256 signature against the IAM JWKS, iss, aud, exp, nonce, sub);
 *  - the OAuth state is single-use AND bound to the browser that started the
 *    flow (HttpOnly cookie), so a login-CSRF link cannot be replayed on a victim;
 *  - an account is matched/created by e-mail only when IAM says
 *    email_verified === true;
 *  - IAM-only mode never locks the site out: bypass (secret URL, wp-config
 *    constant, WP-CLI) and automatic fallback to the form when IAM is down.
 */

if ( ! defined( 'ABSPATH' ) ) {
	exit;
}

class Jengo_IAM_SSO_Login {

	const STATE_TRANSIENT_PREFIX = 'jengo_iam_sso_state_';
	const STATE_TTL              = 10 * MINUTE_IN_SECONDS;
	const STATE_COOKIE           = 'jengo_iam_sso_bind';
	const SSO_SESSION_COOKIE     = 'jengo_iam_sso_session';
	const BYPASS_COOKIE          = 'jengo_iam_sso_bypass';
	const BYPASS_COOKIE_TTL      = 15 * MINUTE_IN_SECONDS;
	const SUB_META_KEY           = 'jengo_iam_sub';

	/** The one account role sync must never touch (break-glass admin). */
	const PROTECTED_LOGIN = 'jengo-admin';

	/** @var bool IAM-only mode is active and password logins on wp-login.php are refused. */
	private $password_login_blocked = false;

	/** @var bool IAM-only mode wanted to redirect but IAM could not be reached. */
	private $iam_unreachable = false;

	public function __construct() {
		add_action( 'login_init', array( $this, 'maybe_force_iam_login' ), 1 );
		add_action( 'login_form_jengo-iam-auth', array( $this, 'start_authorization' ) );
		add_action( 'login_form_jengo-iam-callback', array( $this, 'handle_callback' ) );
		add_filter( 'login_message', array( $this, 'render_login_button' ) );
		add_action( 'login_head', array( $this, 'maybe_hide_password_form' ) );
		add_filter( 'authenticate', array( $this, 'block_password_login' ), 100, 3 );
		add_action( 'wp_logout', array( $this, 'maybe_end_iam_session' ), 10, 1 );
	}

	protected function options() {
		return jengo_iam_sso_get_options();
	}

	protected function oidc() {
		return new Jengo_IAM_SSO_OIDC_Client( $this->options() );
	}

	/**
	 * wp-config.php emergency switch: define( 'JENGO_IAM_SSO_BYPASS', true );
	 * turns the plugin off for logins (plain WordPress login form, no button)
	 * without touching the database, so it works even when settings are wrong.
	 */
	protected function is_bypass_constant_set() {
		return defined( 'JENGO_IAM_SSO_BYPASS' ) && JENGO_IAM_SSO_BYPASS;
	}

	/**
	 * Whether the plugin is configured well enough to attempt a login.
	 */
	public function is_configured() {
		$options = $this->options();
		return ! empty( $options['issuer'] ) && ! empty( $options['client_id'] ) && ! empty( $options['client_secret'] );
	}

	/**
	 * The login mode that applies right now: 'wordpress' | 'iam' | 'both'.
	 * An unconfigured plugin or the wp-config bypass constant means 'wordpress'.
	 */
	public function effective_login_mode() {
		if ( $this->is_bypass_constant_set() || ! $this->is_configured() ) {
			return 'wordpress';
		}
		$options = $this->options();
		return in_array( $options['login_mode'], array( 'wordpress', 'iam', 'both' ), true ) ? $options['login_mode'] : 'both';
	}

	/* ---------------------------------------------------------------------
	 * Login mode behaviour
	 * ------------------------------------------------------------------ */

	/**
	 * 'iam' mode: wp-login.php redirects straight to the IAM authorize
	 * endpoint, except for the plugin's own actions, logout/postpass, the
	 * signed-out page, and an active emergency bypass. If IAM cannot be
	 * reached the normal form is shown instead (with a notice). While IAM-only
	 * mode is in force, password logins on wp-login.php are refused too, so
	 * hiding the form is not the only line of defence.
	 */
	public function maybe_force_iam_login() {
		$this->password_login_blocked = false;
		$this->iam_unreachable        = false;

		if ( 'iam' !== $this->effective_login_mode() ) {
			return;
		}

		$options = $this->options();

		$action = isset( $_REQUEST['action'] ) ? sanitize_key( wp_unslash( $_REQUEST['action'] ) ) : 'login';
		if ( in_array( $action, array( 'jengo-iam-callback', 'jengo-iam-auth', 'logout', 'postpass' ), true ) ) {
			return;
		}

		if ( $this->is_bypass_active( $options ) ) {
			return;
		}

		if ( ! $this->oidc()->is_available() ) {
			$this->iam_unreachable = true;
			return;
		}

		$this->password_login_blocked = true;

		// POSTs are never redirected, but block_password_login() still refuses
		// them. The signed-out page is shown (IAM button only): redirecting it
		// would sign the user straight back in via the still-live IAM session.
		$method = isset( $_SERVER['REQUEST_METHOD'] ) ? strtoupper( sanitize_text_field( wp_unslash( $_SERVER['REQUEST_METHOD'] ) ) ) : 'GET';
		if ( 'GET' !== $method || isset( $_GET['loggedout'] ) ) {
			return;
		}

		$this->start_authorization();
	}

	/**
	 * Emergency bypass check: a matching ?jengo_sso_bypass=<secret> query
	 * parameter, or the short-lived cookie it sets (so the subsequently
	 * POSTed login form and follow-up GETs keep working without carrying the
	 * secret around in every URL).
	 */
	protected function is_bypass_active( array $options ) {
		if ( empty( $options['bypass_secret'] ) ) {
			return false;
		}

		$cookie_value = wp_hash( $options['bypass_secret'] . '|jengo-iam-sso-bypass' );

		if ( isset( $_GET['jengo_sso_bypass'] ) ) {
			$provided = sanitize_text_field( wp_unslash( $_GET['jengo_sso_bypass'] ) );
			if ( hash_equals( (string) $options['bypass_secret'], $provided ) ) {
				jengo_iam_sso_set_cookie( self::BYPASS_COOKIE, $cookie_value, time() + self::BYPASS_COOKIE_TTL );
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
	 * IAM-only mode: refuse username/password logins that arrive through
	 * wp-login.php (flag set in maybe_force_iam_login()). REST/XML-RPC
	 * application passwords are not affected.
	 */
	public function block_password_login( $user, $username = '', $password = '' ) {
		if ( $this->password_login_blocked && $user instanceof WP_User ) {
			return new WP_Error(
				'jengo_iam_sso_iam_only',
				__( 'Password login is disabled on this site. Use "Log in with Jengo IAM".', 'jengo-iam-sso' )
			);
		}
		return $user;
	}

	/**
	 * Hide the WordPress credentials form while IAM-only mode blocks it, so the
	 * signed-out page only offers the IAM button.
	 */
	public function maybe_hide_password_form() {
		if ( $this->password_login_blocked ) {
			echo '<style>#loginform,#nav{display:none}</style>';
		}
	}

	/**
	 * Mode 'both' (default): the normal wp-login form plus a prominent
	 * "Log in with Jengo IAM" button. Mode 'iam': the button on the signed-out
	 * page and on the bypassed form. Mode 'wordpress': nothing at all.
	 */
	public function render_login_button( $message ) {
		if ( 'wordpress' === $this->effective_login_mode() ) {
			return $message;
		}

		$action = isset( $_REQUEST['action'] ) ? sanitize_key( wp_unslash( $_REQUEST['action'] ) ) : 'login';
		if ( 'login' !== $action ) {
			return $message;
		}

		if ( $this->iam_unreachable || ! $this->oidc()->is_available() ) {
			$notice = '<div class="message" style="border-left-color:#dba617;">'
				. esc_html__( 'Jengo IAM sign-in is not reachable right now. Use your WordPress credentials instead.', 'jengo-iam-sso' )
				. '</div>';
			return $message . $notice;
		}

		$url = add_query_arg( 'action', 'jengo-iam-auth', wp_login_url() );
		if ( isset( $_REQUEST['redirect_to'] ) ) {
			$url = add_query_arg( 'redirect_to', rawurlencode( esc_url_raw( wp_unslash( $_REQUEST['redirect_to'] ) ) ), $url );
		}

		$button  = '<div style="margin:0 0 20px;padding:20px;background:#fff;border:1px solid #c3c4c7;box-shadow:0 1px 3px rgba(0,0,0,.04);text-align:center;">';
		$button .= '<a href="' . esc_url( $url ) . '" class="button button-primary button-large" style="width:100%;">'
			. esc_html__( 'Log in with Jengo IAM', 'jengo-iam-sso' ) . '</a>';
		if ( ! $this->password_login_blocked ) {
			$button .= '<p style="margin:12px 0 0;color:#646970;">' . esc_html__( 'or use your WordPress credentials below', 'jengo-iam-sso' ) . '</p>';
		}
		$button .= '</div>';

		return $message . $button;
	}

	/* ---------------------------------------------------------------------
	 * Authorization request
	 * ------------------------------------------------------------------ */

	/**
	 * Start the OIDC flow: mint state + PKCE pair + nonce + a browser-binding
	 * secret, persist the server-side half in a short-lived transient keyed by
	 * the state value, set the browser-side half as an HttpOnly cookie, and
	 * redirect to IAM.
	 */
	public function start_authorization() {
		if ( ! $this->is_configured() ) {
			$this->fail( __( 'Jengo IAM SSO is not configured yet (issuer, client ID and client secret are required).', 'jengo-iam-sso' ) );
		}

		$state   = bin2hex( random_bytes( 16 ) );
		$nonce   = bin2hex( random_bytes( 16 ) );
		$binding = bin2hex( random_bytes( 16 ) );
		$pkce    = Jengo_IAM_SSO_OIDC_Client::generate_pkce_pair();

		$url = $this->oidc()->build_authorize_url( $state, $pkce['challenge'], $nonce );
		if ( is_wp_error( $url ) ) {
			$this->fail( __( 'Jengo IAM is not reachable right now. Please try again later or use your WordPress credentials.', 'jengo-iam-sso' ) );
		}

		$redirect_to = '';
		if ( isset( $_REQUEST['redirect_to'] ) ) {
			$redirect_to = wp_validate_redirect( esc_url_raw( wp_unslash( $_REQUEST['redirect_to'] ) ), '' );
		}

		set_transient(
			self::STATE_TRANSIENT_PREFIX . $state,
			array(
				'verifier'    => $pkce['verifier'],
				'nonce'       => $nonce,
				'binding'     => hash( 'sha256', $binding ),
				'redirect_to' => $redirect_to,
				'created'     => time(),
			),
			self::STATE_TTL
		);
		jengo_iam_sso_set_cookie( self::STATE_COOKIE, $binding, time() + self::STATE_TTL );

		// wp_safe_redirect() would strip the external IAM host; the authorize
		// URL is built from the validated discovery document, not user input.
		wp_redirect( $url ); // phpcs:ignore WordPress.Security.SafeRedirect
		exit;
	}

	/* ---------------------------------------------------------------------
	 * Callback
	 * ------------------------------------------------------------------ */

	/**
	 * wp-login.php?action=jengo-iam-callback, the registered redirect URI.
	 * Order of checks: error param -> state (single-use) -> browser binding ->
	 * code exchange -> id_token validation -> e-mail verification -> matching /
	 * provisioning -> role sync -> session.
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

		if ( ! is_array( $stored ) || empty( $stored['verifier'] ) || empty( $stored['nonce'] ) || empty( $stored['binding'] ) ) {
			$this->fail( __( 'Your sign-in attempt expired or was already used. Please try again.', 'jengo-iam-sso' ) );
		}

		// Browser binding: the state must come back in the browser that started
		// the flow. Without this an attacker could start a flow, sign in as
		// themselves and hand the callback URL to a victim (login CSRF).
		$cookie = isset( $_COOKIE[ self::STATE_COOKIE ] ) ? sanitize_text_field( wp_unslash( $_COOKIE[ self::STATE_COOKIE ] ) ) : '';
		jengo_iam_sso_set_cookie( self::STATE_COOKIE, '', time() - HOUR_IN_SECONDS );
		if ( '' === $cookie || ! hash_equals( (string) $stored['binding'], hash( 'sha256', $cookie ) ) ) {
			$this->fail( __( 'This sign-in was not started in this browser. Please start again from the login page.', 'jengo-iam-sso' ) );
		}

		$options = $this->options();
		$oidc    = $this->oidc();

		$tokens = $oidc->exchange_code( $code, $stored['verifier'] );
		if ( is_wp_error( $tokens ) ) {
			$this->fail( __( 'Could not exchange the authorization code with Jengo IAM.', 'jengo-iam-sso' ) );
		}

		// No login without a validated id_token.
		$discovery = $oidc->get_discovery();
		$claims    = Jengo_IAM_SSO_Token_Validator::validate_id_token(
			$tokens['id_token'],
			array( $oidc, 'get_jwks' ),
			array(
				'issuer'    => is_wp_error( $discovery ) ? '' : $discovery['issuer'],
				'client_id' => $options['client_id'],
				'nonce'     => $stored['nonce'],
			)
		);
		if ( is_wp_error( $claims ) ) {
			$this->fail( __( 'The identity token from Jengo IAM could not be verified. Please try again.', 'jengo-iam-sso' ) );
		}

		$sub            = sanitize_text_field( $claims['sub'] );
		$email          = isset( $claims['email'] ) && is_string( $claims['email'] ) ? sanitize_email( $claims['email'] ) : '';
		$email_verified = isset( $claims['email_verified'] ) && true === $claims['email_verified'];
		$name           = isset( $claims['name'] ) && is_string( $claims['name'] ) ? sanitize_text_field( $claims['name'] ) : '';

		$mapped_roles = Jengo_IAM_SSO_Role_Mapper::map_roles( $claims, $options['client_id'], $options['role_map'] );

		$user = $this->match_user( $sub, $email, $email_verified );

		if ( ! $user ) {
			if ( empty( $options['auto_provision'] ) ) {
				$this->fail( __( 'No WordPress account is linked to your Jengo IAM identity, and automatic account creation is disabled.', 'jengo-iam-sso' ) );
			}

			// Auto-provision applies but IAM granted none of the mapped
			// roles for this site: refuse, do not create an account.
			if ( empty( $mapped_roles ) ) {
				$this->fail( __( 'You have no access granted in Jengo IAM for this site. Ask an administrator to grant you a role in the IAM access matrix.', 'jengo-iam-sso' ) );
			}

			$user = $this->provision_user( $email, $name, $mapped_roles, $options );
		}

		// Link the IAM subject for next time (idempotent). Only reached for
		// accounts matched by subject or by a verified e-mail address.
		update_user_meta( $user->ID, self::SUB_META_KEY, $sub );

		$this->sync_roles( $user, $mapped_roles );

		// Sign in.
		wp_set_current_user( $user->ID, $user->user_login );
		wp_set_auth_cookie( $user->ID, false, is_ssl() );
		jengo_iam_sso_set_cookie( self::SSO_SESSION_COOKIE, '1', 0 );
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
	 * (2) user with the same e-mail address, but only when IAM reports the
	 * address as verified and the account is not linked to another IAM identity.
	 *
	 * @return WP_User|null
	 */
	protected function match_user( $sub, $email, $email_verified ) {
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

		// From here on the e-mail address is the only link: it must be verified.
		if ( '' === $email || ! $email_verified ) {
			$this->fail( __( 'Your e-mail address is not verified in Jengo IAM, so it cannot be used to sign in here. Verify it in Jengo IAM and try again.', 'jengo-iam-sso' ) );
		}

		$by_email = get_user_by( 'email', $email );
		if ( $by_email instanceof WP_User ) {
			$linked = get_user_meta( $by_email->ID, self::SUB_META_KEY, true );
			if ( '' !== $linked && false !== $linked && $linked !== $sub ) {
				$this->fail( __( 'This WordPress account is already linked to a different Jengo IAM identity.', 'jengo-iam-sso' ) );
			}
			return $by_email;
		}

		return null;
	}

	/**
	 * Create a WordPress user for a first-time IAM login. Login name is the
	 * sanitized local part of the verified e-mail address, deduplicated with a
	 * numeric suffix; the password is random and never communicated.
	 *
	 * @return WP_User
	 */
	protected function provision_user( $email, $name, array $mapped_roles, array $options ) {
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

		// With sync enabled the IAM-granted role wins; otherwise new users get
		// the configured default role, which is never a privileged role.
		if ( ! empty( $options['sync_roles'] ) && ! empty( $mapped_roles ) ) {
			$role = $mapped_roles[0];
		} else {
			$role = $options['default_role'];
			if ( '' === $role || null === get_role( $role ) || Jengo_IAM_SSO_Role_Mapper::is_privileged_role( $role ) ) {
				$role = 'subscriber';
			}
		}

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
	 * Sync the WordPress role from IAM on every SSO login (when enabled).
	 * Users without any mapped IAM role keep their existing WordPress role
	 * (they only reach this point when their account pre-existed). The
	 * break-glass 'jengo-admin' account is never modified.
	 */
	protected function sync_roles( WP_User $user, array $mapped_roles ) {
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

	/* ---------------------------------------------------------------------
	 * Logout
	 * ------------------------------------------------------------------ */

	/**
	 * "Also log out of IAM": when enabled and this WordPress session was
	 * created by an SSO login, an interactive logout (wp-login.php?action=logout)
	 * continues to the IAM end_session endpoint, which ends the IAM session and
	 * returns to the site home page. Skipped quietly when IAM does not
	 * advertise an end_session endpoint.
	 */
	public function maybe_end_iam_session( $user_id = 0 ) {
		$options = $this->options();
		if ( empty( $options['logout_iam'] ) || ! $this->is_configured() ) {
			return;
		}
		if ( empty( $_COOKIE[ self::SSO_SESSION_COOKIE ] ) ) {
			return;
		}

		jengo_iam_sso_set_cookie( self::SSO_SESSION_COOKIE, '', time() - HOUR_IN_SECONDS );

		$action = isset( $_REQUEST['action'] ) ? sanitize_key( wp_unslash( $_REQUEST['action'] ) ) : '';
		if ( 'logout' !== $action || ( function_exists( 'wp_doing_ajax' ) && wp_doing_ajax() ) ) {
			return;
		}

		$url = $this->oidc()->build_end_session_url( Jengo_IAM_SSO_OIDC_Client::post_logout_redirect_uri() );
		if ( '' === $url ) {
			return;
		}

		wp_redirect( $url ); // phpcs:ignore WordPress.Security.SafeRedirect
		exit;
	}

	/**
	 * Abort the login flow with a user-facing error. wp_die (instead of a
	 * redirect back to wp-login.php) deliberately avoids redirect loops in
	 * 'iam' mode. No tokens, secrets or raw provider responses are included.
	 */
	protected function fail( $message ) {
		$back = 'iam' === $this->effective_login_mode() ? home_url( '/' ) : wp_login_url();
		wp_die(
			esc_html( $message ),
			esc_html__( 'Jengo IAM sign-in', 'jengo-iam-sso' ),
			array(
				'response'  => 403,
				'link_url'  => esc_url( $back ),
				'link_text' => esc_html__( 'Back', 'jengo-iam-sso' ),
			)
		);
	}
}
