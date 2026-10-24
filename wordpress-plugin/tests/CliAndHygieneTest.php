<?php

/**
 * WP-CLI emergency commands, plus repository hygiene checks: nothing in the
 * plugin writes to a log, and the version is consistent everywhere.
 */
class CliAndHygieneTest extends JengoTestCase {

	protected function setUp(): void {
		parent::setUp();
		WP_CLI::$lines   = array();
		WP_CLI::$success = array();
	}

	/* ---- WP-CLI ------------------------------------------------------------- */

	public function test_the_commands_are_registered_with_wp_cli() {
		$this->assertSame( 'Jengo_IAM_SSO_CLI', WP_CLI::$commands['jengo-iam-sso'] );
	}

	public function test_set_mode_wordpress_is_the_emergency_switch() {
		$this->configure( array( 'login_mode' => 'iam' ) );

		( new Jengo_IAM_SSO_CLI() )->set_mode( array( 'wordpress' ) );

		$this->assertSame( 'wordpress', jengo_iam_sso_get_options()['login_mode'] );
		$this->assertSame( 'wordpress', $this->login()->effective_login_mode() );
	}

	public function test_set_mode_rejects_unknown_values_and_changes_nothing() {
		$this->configure( array( 'login_mode' => 'both' ) );

		try {
			( new Jengo_IAM_SSO_CLI() )->set_mode( array( 'nonsense' ) );
			$this->fail( 'Expected a CLI error' );
		} catch ( Jengo_Test_Cli_Error $error ) {
			$this->assertStringContainsString( 'wordpress', $error->getMessage() );
		}
		$this->assertSame( 'both', jengo_iam_sso_get_options()['login_mode'] );
	}

	public function test_set_mode_iam_makes_sure_a_bypass_secret_exists() {
		$this->configure( array( 'bypass_secret' => '' ) );

		( new Jengo_IAM_SSO_CLI() )->set_mode( array( 'iam' ) );

		$this->assertSame( 40, strlen( jengo_iam_sso_get_options()['bypass_secret'] ) );
	}

	public function test_bypass_url_prints_the_working_bypass_link() {
		( new Jengo_IAM_SSO_CLI() )->bypass_url();

		$this->assertSame(
			array( 'https://test.prospergenics.com/wp-login.php?jengo_sso_bypass=bypass-secret-1234567890' ),
			WP_CLI::$lines
		);
	}

	public function test_bypass_url_without_a_secret_is_an_error() {
		$this->configure( array( 'bypass_secret' => '' ) );

		$this->expectException( Jengo_Test_Cli_Error::class );
		( new Jengo_IAM_SSO_CLI() )->bypass_url();
	}

	public function test_regenerate_bypass_invalidates_the_old_secret() {
		( new Jengo_IAM_SSO_CLI() )->regenerate_bypass();

		$new = jengo_iam_sso_get_options()['bypass_secret'];
		$this->assertNotSame( 'bypass-secret-1234567890', $new );
		$this->assertSame( 40, strlen( $new ) );
	}

	public function test_status_never_prints_the_client_secret() {
		( new Jengo_IAM_SSO_CLI() )->status();

		$output = implode( "\n", WP_CLI::$lines );
		$this->assertStringContainsString( 'client secret:  set', $output );
		$this->assertStringContainsString( 'IAM discovery:  ok', $output );
		$this->assertStringNotContainsString( $this->iam->client_secret, $output );
	}

	public function test_status_reports_a_discovery_failure() {
		$this->iam->discovery_up = false;

		( new Jengo_IAM_SSO_CLI() )->status();

		$this->assertStringContainsString( 'FAILED', implode( "\n", WP_CLI::$lines ) );
	}

	/* ---- hygiene ---------------------------------------------------------------- */

	private function plugin_php_files() {
		$root  = dirname( __DIR__ ) . '/jengo-iam-sso';
		$files = array( $root . '/jengo-iam-sso.php' );
		foreach ( glob( $root . '/includes/*.php' ) as $file ) {
			$files[] = $file;
		}
		return $files;
	}

	public function test_the_plugin_never_writes_to_a_log_or_dumps_variables() {
		foreach ( $this->plugin_php_files() as $file ) {
			$code = file_get_contents( $file );
			$this->assertDoesNotMatchRegularExpression( '/\b(error_log|var_dump|var_export|print_r|trigger_error|syslog)\s*\(/', $code, basename( $file ) );
		}
	}

	public function test_the_plugin_stays_compatible_with_php_7_4_syntax() {
		foreach ( $this->plugin_php_files() as $file ) {
			$code = file_get_contents( $file );
			$this->assertDoesNotMatchRegularExpression( '/\b(str_starts_with|str_ends_with|str_contains)\s*\(/', $code, basename( $file ) . ' uses a PHP 8 string function' );
			$this->assertDoesNotMatchRegularExpression( '/\bmatch\s*\(\s*\$/', $code, basename( $file ) . ' uses match()' );
			$this->assertDoesNotMatchRegularExpression( '/\?->/', $code, basename( $file ) . ' uses the nullsafe operator' );
			$this->assertDoesNotMatchRegularExpression( '/function\s+\w+\s*\([^)]*\b(int|string|array)\s*\|\s*(int|string|array|null|false)/', $code, basename( $file ) . ' uses union types' );
		}
	}

	public function test_the_version_is_the_same_in_the_header_the_constant_and_the_readme() {
		$main   = file_get_contents( dirname( __DIR__ ) . '/jengo-iam-sso/jengo-iam-sso.php' );
		$readme = file_get_contents( dirname( __DIR__ ) . '/jengo-iam-sso/readme.txt' );

		preg_match( '/^\s*\*\s*Version:\s*(\S+)/m', $main, $header );
		preg_match( '/^Stable tag:\s*(\S+)/m', $readme, $stable );

		$this->assertSame( JENGO_IAM_SSO_VERSION, $header[1] );
		$this->assertSame( JENGO_IAM_SSO_VERSION, $stable[1] );
	}
}
