<?php

use PHPUnit\Framework\TestCase;

/**
 * Login subclass with a seam for the wp-config constant (constants cannot be
 * un-defined inside one PHP process).
 */
class Jengo_Testable_Login extends Jengo_IAM_SSO_Login {
	public $bypass_constant = false;

	protected function is_bypass_constant_set() {
		return $this->bypass_constant;
	}
}

/**
 * Shared fixture: a configured plugin talking to a fake IAM, plus helpers that
 * drive the REAL start_authorization() / handle_callback() code the way a
 * browser would (authorize redirect -> IAM -> callback with code + state +
 * the browser-binding cookie).
 */
abstract class JengoTestCase extends TestCase {

	/** @var Jengo_Test_Fake_Iam */
	protected $iam;

	protected function setUp(): void {
		parent::setUp();
		Jengo_Test_World::reset();
		$this->iam = new Jengo_Test_Fake_Iam();
		$this->iam->register();
		$this->configure();
	}

	protected function configure( array $overrides = array() ) {
		update_option(
			JENGO_IAM_SSO_OPTION,
			array_merge(
				jengo_iam_sso_default_options(),
				array(
					'issuer'        => 'https://iam.test/auth',
					'client_id'     => $this->iam->client_id,
					'client_secret' => $this->iam->client_secret,
					'login_mode'    => 'both',
					'bypass_secret' => 'bypass-secret-1234567890',
				),
				$overrides
			)
		);
	}

	protected function login() {
		return new Jengo_Testable_Login();
	}

	/** Run $fn and return the redirect location it ended in. */
	protected function redirect_of( callable $fn ) {
		try {
			$fn();
		} catch ( Jengo_Test_Redirect $redirect ) {
			return $redirect->location;
		}
		$this->fail( 'Expected a redirect, none happened' );
	}

	/** Run $fn and return the wp_die() message it ended in. */
	protected function die_message_of( callable $fn ) {
		try {
			$fn();
		} catch ( Jengo_Test_Die $die ) {
			return $die->getMessage();
		}
		$this->fail( 'Expected wp_die(), none happened' );
	}

	/**
	 * "Click the IAM button": start the flow and return what IAM would see.
	 *
	 * @return array{location:string,params:array,binding:string}
	 */
	protected function start_flow( Jengo_IAM_SSO_Login $login, array $request = array() ) {
		$_REQUEST = $request;
		$_GET     = $request;
		$location = $this->redirect_of( array( $login, 'start_authorization' ) );
		parse_str( (string) parse_url( $location, PHP_URL_QUERY ), $params );
		return array(
			'location' => $location,
			'params'   => $params,
			'binding'  => (string) Jengo_Test_World::cookie_value( Jengo_IAM_SSO_Login::STATE_COOKIE ),
		);
	}

	/**
	 * The browser comes back to the callback URL.
	 *
	 * @param string|null $binding Value of the binding cookie the browser sends (null = none).
	 * @return array{redirect?:string,die?:string}
	 */
	protected function run_callback( Jengo_IAM_SSO_Login $login, $code, $state, $binding, array $extra_get = array() ) {
		$_GET     = array_merge( array( 'code' => $code, 'state' => $state ), $extra_get );
		$_REQUEST = $_GET;
		$_COOKIE  = null === $binding ? array() : array( Jengo_IAM_SSO_Login::STATE_COOKIE => $binding );
		try {
			$login->handle_callback();
		} catch ( Jengo_Test_Redirect $redirect ) {
			return array( 'redirect' => $redirect->location );
		} catch ( Jengo_Test_Die $die ) {
			return array( 'die' => $die->getMessage() );
		}
		$this->fail( 'handle_callback() neither redirected nor died' );
	}

	/**
	 * Full round trip through IAM for one user.
	 *
	 * @param array $claim_overrides id_token claims to override (null removes a claim).
	 */
	protected function sso_login( array $claim_overrides = array(), ?Jengo_IAM_SSO_Login $login = null, array $request = array() ) {
		$login = $login ?: $this->login();
		$flow  = $this->start_flow( $login, $request );
		$code  = $this->iam->issue_code( $flow['location'], $claim_overrides );
		return $this->run_callback( $login, $code, $flow['params']['state'], $flow['binding'] );
	}

	protected function assertSessionIssuedFor( $user_id ) {
		$this->assertSame( $user_id, Jengo_Test_World::$auth_cookie, 'Expected a WordPress session for this user' );
	}

	protected function assertNoSession() {
		$this->assertNull( Jengo_Test_World::$auth_cookie, 'No WordPress session may be issued' );
	}

	protected function token_posts() {
		return array_values(
			array_filter(
				Jengo_Test_World::$http_log,
				static function ( $entry ) {
					return 'POST' === $entry[0] && false !== strpos( $entry[1], '/connect/token' );
				}
			)
		);
	}
}
