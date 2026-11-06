<?php

/**
 * Settings validation and the settings page: the secret is write-only,
 * auto-provision can never default to administrator, lockout guards.
 */
class SettingsTest extends JengoTestCase {

	private function settings() {
		return new Jengo_IAM_SSO_Settings();
	}

	private function sanitize( array $input ) {
		return $this->settings()->sanitize_options( $input );
	}

	public function test_an_unchecked_auto_provision_box_saves_as_off() {
		$this->configure( array( 'auto_provision' => 1 ) );

		$this->assertSame( 0, $this->sanitize( array( 'issuer' => 'https://iam.test/auth' ) )['auto_provision'] );
	}

	public function test_a_checked_auto_provision_box_saves_as_on() {
		$this->assertSame( 1, $this->sanitize( array( 'auto_provision' => '1' ) )['auto_provision'] );
	}

	public function test_default_role_can_never_be_administrator_or_a_user_management_role() {
		foreach ( array( 'administrator', 'user_admin' ) as $role ) {
			Jengo_Test_World::$settings_errors = array();

			$clean = $this->sanitize( array( 'default_role' => $role ) );

			$this->assertSame( 'subscriber', $clean['default_role'], $role );
			$this->assertSame( array( array( 'jengo_iam_sso', 'default_role' ) ), Jengo_Test_World::$settings_errors );
		}
	}

	public function test_a_harmless_default_role_is_saved_and_an_unknown_one_is_refused() {
		$this->assertSame( 'contributor', $this->sanitize( array( 'default_role' => 'contributor' ) )['default_role'] );
		$this->assertSame( 'subscriber', $this->sanitize( array( 'default_role' => 'no_such_role' ) )['default_role'] );
	}

	public function test_the_role_dropdown_never_offers_privileged_roles() {
		ob_start();
		$this->settings()->render_settings_page();
		$html = (string) ob_get_clean();

		$this->assertStringContainsString( 'value="subscriber"', $html );
		$this->assertStringContainsString( 'value="editor"', $html );
		$this->assertStringNotContainsString( 'value="administrator"', $html );
		$this->assertStringNotContainsString( 'value="user_admin"', $html );
	}

	public function test_the_client_secret_is_never_rendered_back_into_the_page() {
		$this->configure( array( 'client_secret' => 'super-secret-do-not-leak' ) );

		ob_start();
		$this->settings()->render_settings_page();
		$html = (string) ob_get_clean();

		$this->assertStringNotContainsString( 'super-secret-do-not-leak', $html );
		$this->assertMatchesRegularExpression( '/name="jengo_iam_sso_options\[client_secret\]"[^>]*value=""/', $html );
		$this->assertStringContainsString( 'secret stored', $html );
	}

	public function test_a_blank_secret_field_keeps_the_stored_secret_and_a_new_one_replaces_it() {
		$this->configure( array( 'client_secret' => 'kept-secret' ) );

		$this->assertSame( 'kept-secret', $this->sanitize( array( 'client_secret' => '' ) )['client_secret'] );
		$this->assertSame( 'kept-secret', $this->sanitize( array( 'client_secret' => '   ' ) )['client_secret'] );
		$this->assertSame( 'new-secret', $this->sanitize( array( 'client_secret' => ' new-secret ' ) )['client_secret'] );
	}

	public function test_the_issuer_must_be_https() {
		$this->configure( array( 'issuer' => 'https://iam.test/auth' ) );

		$clean = $this->sanitize( array( 'issuer' => 'http://iam.test/auth' ) );

		$this->assertSame( 'https://iam.test/auth', $clean['issuer'] );
		$this->assertSame( array( array( 'jengo_iam_sso', 'issuer' ) ), Jengo_Test_World::$settings_errors );
	}

	public function test_the_issuer_is_stored_without_a_trailing_slash() {
		$this->assertSame( 'https://iam.test/auth', $this->sanitize( array( 'issuer' => 'https://iam.test/auth/' ) )['issuer'] );
	}

	public function test_iam_only_cannot_be_saved_without_a_bypass_secret() {
		$this->configure( array( 'bypass_secret' => '', 'login_mode' => 'both' ) );

		$clean = $this->sanitize( array( 'login_mode' => 'iam' ) );

		$this->assertSame( 'both', $clean['login_mode'] );
	}

	public function test_login_mode_accepts_only_the_three_known_values() {
		$this->assertSame( 'wordpress', $this->sanitize( array( 'login_mode' => 'wordpress' ) )['login_mode'] );
		$this->assertSame( 'iam', $this->sanitize( array( 'login_mode' => 'iam' ) )['login_mode'] );
		$this->assertSame( 'both', $this->sanitize( array( 'login_mode' => 'nonsense' ) )['login_mode'] );
	}

	public function test_the_role_map_is_saved_and_the_logout_option_round_trips() {
		$clean = $this->sanitize( array( 'role_map' => "TeamLead = editor\n", 'logout_iam' => '1' ) );

		$this->assertSame( 'TeamLead = editor', $clean['role_map'] );
		$this->assertSame( 1, $clean['logout_iam'] );
		$this->assertSame( 0, $this->sanitize( array() )['logout_iam'] );
	}

	public function test_regenerating_the_bypass_secret_changes_it() {
		$before = jengo_iam_sso_get_options()['bypass_secret'];

		$clean = $this->sanitize( array( 'regenerate_bypass' => '1' ) );

		$this->assertNotSame( $before, $clean['bypass_secret'] );
		$this->assertSame( 40, strlen( $clean['bypass_secret'] ) );
	}

	public function test_saving_drops_the_cached_discovery_document() {
		$login = $this->login();
		$login->render_login_button( '' ); // primes the discovery cache
		$this->assertNotEmpty( array_filter( array_keys( Jengo_Test_World::$transients ), static function ( $k ) {
			return 0 === strpos( $k, 'jengo_iam_sso_disco_' );
		} ) );

		$this->sanitize( array( 'issuer' => 'https://iam.test/auth' ) );

		$this->assertEmpty( array_filter( array_keys( Jengo_Test_World::$transients ), static function ( $k ) {
			return 0 === strpos( $k, 'jengo_iam_sso_disco_' );
		} ) );
	}

	public function test_the_page_names_the_exact_redirect_and_post_logout_uris_to_register() {
		ob_start();
		$this->settings()->render_settings_page();
		$html = (string) ob_get_clean();

		$this->assertStringContainsString( 'https://test.prospergenics.com/wp-login.php?action=jengo-iam-callback', $html );
		$this->assertStringContainsString( 'https://test.prospergenics.com/', $html );
		$this->assertStringContainsString( 'JENGO_IAM_SSO_BYPASS', $html );
	}

	public function test_the_page_reports_a_failing_iam_connection() {
		$this->iam->discovery_up = false;

		ob_start();
		$this->settings()->render_settings_page();
		$html = (string) ob_get_clean();

		$this->assertStringContainsString( 'not reachable or invalid', $html );
	}

	public function test_the_page_reports_a_working_iam_connection() {
		ob_start();
		$this->settings()->render_settings_page();
		$html = (string) ob_get_clean();

		$this->assertStringContainsString( 'OK (discovery document found', $html );
	}
}
