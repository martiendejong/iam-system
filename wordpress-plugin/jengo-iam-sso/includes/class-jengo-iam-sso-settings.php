<?php
/**
 * Settings → Jengo IAM SSO.
 *
 * Capability: manage_options. The client secret is stored in the options
 * table (standard for WP OIDC plugins) and never echoed back into the form;
 * leaving the field blank keeps the stored value.
 */

if ( ! defined( 'ABSPATH' ) ) {
	exit;
}

class Jengo_IAM_SSO_Settings {

	const PAGE_SLUG     = 'jengo-iam-sso';
	const SETTINGS_SLUG = 'jengo_iam_sso';

	public function __construct() {
		add_action( 'admin_menu', array( $this, 'add_settings_page' ) );
		add_action( 'admin_init', array( $this, 'register_settings' ) );
	}

	public function add_settings_page() {
		add_options_page(
			__( 'Jengo IAM SSO', 'jengo-iam-sso' ),
			__( 'Jengo IAM SSO', 'jengo-iam-sso' ),
			'manage_options',
			self::PAGE_SLUG,
			array( $this, 'render_settings_page' )
		);
	}

	public function register_settings() {
		register_setting(
			self::SETTINGS_SLUG,
			JENGO_IAM_SSO_OPTION,
			array(
				'type'              => 'array',
				'sanitize_callback' => array( $this, 'sanitize_options' ),
			)
		);
	}

	/**
	 * Sanitize and validate submitted options. Unknown keys are dropped,
	 * invalid values fall back to the previously stored (or default) value.
	 */
	public function sanitize_options( $input ) {
		$current = jengo_iam_sso_get_options();
		$clean   = $current;

		if ( ! is_array( $input ) ) {
			return $clean;
		}

		if ( isset( $input['issuer'] ) ) {
			$issuer = esc_url_raw( trim( (string) $input['issuer'] ) );
			if ( '' !== $issuer && 0 === strpos( $issuer, 'https://' ) ) {
				$clean['issuer'] = untrailingslashit( $issuer );
			} else {
				add_settings_error( self::SETTINGS_SLUG, 'issuer', __( 'The issuer URL must be a valid https:// URL.', 'jengo-iam-sso' ) );
			}
		}

		if ( isset( $input['client_id'] ) ) {
			$clean['client_id'] = sanitize_text_field( (string) $input['client_id'] );
		}

		// Blank secret field = keep the stored secret.
		if ( isset( $input['client_secret'] ) && '' !== trim( (string) $input['client_secret'] ) ) {
			$clean['client_secret'] = trim( (string) $input['client_secret'] );
		}

		if ( isset( $input['login_mode'] ) && in_array( $input['login_mode'], array( 'wordpress', 'iam', 'both' ), true ) ) {
			// Guard against lockout: 'iam' mode requires a bypass secret.
			if ( 'iam' === $input['login_mode'] && empty( $current['bypass_secret'] ) ) {
				add_settings_error( self::SETTINGS_SLUG, 'login_mode', __( 'Login mode "IAM only" requires an emergency bypass secret; re-activate the plugin first.', 'jengo-iam-sso' ) );
			} else {
				$clean['login_mode'] = $input['login_mode'];
			}
		}

		$clean['auto_provision'] = empty( $input['auto_provision'] ) ? 0 : 1;
		$clean['sync_roles']     = empty( $input['sync_roles'] ) ? 0 : 1;

		if ( isset( $input['default_role'] ) ) {
			$role = sanitize_key( (string) $input['default_role'] );
			if ( null !== get_role( $role ) ) {
				$clean['default_role'] = $role;
			}
		}

		foreach ( array( 'authorization_endpoint', 'token_endpoint', 'userinfo_endpoint' ) as $endpoint ) {
			if ( isset( $input[ $endpoint ] ) ) {
				$url = esc_url_raw( trim( (string) $input[ $endpoint ] ) );
				$clean[ $endpoint ] = $url; // Empty string = use discovery/defaults.
			}
		}

		if ( ! empty( $input['regenerate_bypass'] ) ) {
			$clean['bypass_secret'] = bin2hex( random_bytes( 20 ) );
		}

		// Endpoint config changed → drop the cached discovery document.
		delete_transient( 'jengo_iam_sso_disco_' . md5( untrailingslashit( $clean['issuer'] ) ) );

		return $clean;
	}

	public function render_settings_page() {
		if ( ! current_user_can( 'manage_options' ) ) {
			return;
		}

		$options      = jengo_iam_sso_get_options();
		$option_name  = JENGO_IAM_SSO_OPTION;
		$redirect_uri = add_query_arg( 'action', 'jengo-iam-callback', wp_login_url() );
		$bypass_url   = add_query_arg( 'jengo_sso_bypass', $options['bypass_secret'], wp_login_url() );
		?>
		<div class="wrap">
			<h1><?php esc_html_e( 'Jengo IAM SSO', 'jengo-iam-sso' ); ?></h1>
			<p>
				<?php esc_html_e( 'Single sign-on via Jengo IAM (OpenID Connect, authorization code + PKCE).', 'jengo-iam-sso' ); ?>
				<?php esc_html_e( 'Register this exact redirect URI on the IAM client:', 'jengo-iam-sso' ); ?>
				<code><?php echo esc_html( $redirect_uri ); ?></code>
			</p>

			<form method="post" action="options.php">
				<?php settings_fields( self::SETTINGS_SLUG ); ?>
				<table class="form-table" role="presentation">
					<tr>
						<th scope="row"><label for="jengo-issuer"><?php esc_html_e( 'Issuer URL', 'jengo-iam-sso' ); ?></label></th>
						<td>
							<input name="<?php echo esc_attr( $option_name ); ?>[issuer]" id="jengo-issuer" type="url" class="regular-text" value="<?php echo esc_attr( $options['issuer'] ); ?>" />
							<p class="description"><?php esc_html_e( 'The Jengo IAM base URL; endpoints are read from {issuer}/.well-known/openid-configuration.', 'jengo-iam-sso' ); ?></p>
						</td>
					</tr>
					<tr>
						<th scope="row"><label for="jengo-client-id"><?php esc_html_e( 'Client ID', 'jengo-iam-sso' ); ?></label></th>
						<td><input name="<?php echo esc_attr( $option_name ); ?>[client_id]" id="jengo-client-id" type="text" class="regular-text" value="<?php echo esc_attr( $options['client_id'] ); ?>" /></td>
					</tr>
					<tr>
						<th scope="row"><label for="jengo-client-secret"><?php esc_html_e( 'Client secret', 'jengo-iam-sso' ); ?></label></th>
						<td>
							<input name="<?php echo esc_attr( $option_name ); ?>[client_secret]" id="jengo-client-secret" type="password" class="regular-text" value="" autocomplete="new-password" placeholder="<?php echo esc_attr( '' !== $options['client_secret'] ? __( '(secret stored — leave blank to keep)', 'jengo-iam-sso' ) : '' ); ?>" />
							<p class="description"><?php esc_html_e( 'Stored on save; leave blank to keep the current secret.', 'jengo-iam-sso' ); ?></p>
						</td>
					</tr>
					<tr>
						<th scope="row"><?php esc_html_e( 'Login mode', 'jengo-iam-sso' ); ?></th>
						<td>
							<fieldset>
								<label><input type="radio" name="<?php echo esc_attr( $option_name ); ?>[login_mode]" value="both" <?php checked( $options['login_mode'], 'both' ); ?> /> <?php esc_html_e( 'Both — WordPress form plus a "Log in with Jengo IAM" button (default)', 'jengo-iam-sso' ); ?></label><br />
								<label><input type="radio" name="<?php echo esc_attr( $option_name ); ?>[login_mode]" value="iam" <?php checked( $options['login_mode'], 'iam' ); ?> /> <?php esc_html_e( 'IAM only — wp-login.php redirects straight to Jengo IAM (emergency bypass below keeps working)', 'jengo-iam-sso' ); ?></label><br />
								<label><input type="radio" name="<?php echo esc_attr( $option_name ); ?>[login_mode]" value="wordpress" <?php checked( $options['login_mode'], 'wordpress' ); ?> /> <?php esc_html_e( 'WordPress only — SSO disabled on the login form', 'jengo-iam-sso' ); ?></label>
							</fieldset>
						</td>
					</tr>
					<tr>
						<th scope="row"><?php esc_html_e( 'Auto-provision users', 'jengo-iam-sso' ); ?></th>
						<td>
							<label><input type="checkbox" name="<?php echo esc_attr( $option_name ); ?>[auto_provision]" value="1" <?php checked( $options['auto_provision'], 1 ); ?> /> <?php esc_html_e( 'Create a WordPress account on first IAM login (requires an IAM-granted role for this site)', 'jengo-iam-sso' ); ?></label>
						</td>
					</tr>
					<tr>
						<th scope="row"><label for="jengo-default-role"><?php esc_html_e( 'Default role for new users', 'jengo-iam-sso' ); ?></label></th>
						<td>
							<select name="<?php echo esc_attr( $option_name ); ?>[default_role]" id="jengo-default-role">
								<?php wp_dropdown_roles( $options['default_role'] ); ?>
							</select>
							<p class="description"><?php esc_html_e( 'Used for new accounts when role sync is off; with role sync on, the IAM role wins.', 'jengo-iam-sso' ); ?></p>
						</td>
					</tr>
					<tr>
						<th scope="row"><?php esc_html_e( 'Sync role from IAM', 'jengo-iam-sso' ); ?></th>
						<td>
							<label><input type="checkbox" name="<?php echo esc_attr( $option_name ); ?>[sync_roles]" value="1" <?php checked( $options['sync_roles'], 1 ); ?> /> <?php esc_html_e( 'On every SSO login, set the WordPress role from the IAM roles claim ({client id}:administrator/editor/author/contributor/subscriber)', 'jengo-iam-sso' ); ?></label>
						</td>
					</tr>
					<tr>
						<th scope="row"><?php esc_html_e( 'Emergency bypass', 'jengo-iam-sso' ); ?></th>
						<td>
							<?php if ( ! empty( $options['bypass_secret'] ) ) : ?>
								<p><?php esc_html_e( 'With login mode "IAM only", this URL still shows the normal WordPress login form:', 'jengo-iam-sso' ); ?></p>
								<p><code><?php echo esc_html( $bypass_url ); ?></code></p>
							<?php else : ?>
								<p><?php esc_html_e( 'No bypass secret yet — it is generated automatically on plugin activation.', 'jengo-iam-sso' ); ?></p>
							<?php endif; ?>
							<label><input type="checkbox" name="<?php echo esc_attr( $option_name ); ?>[regenerate_bypass]" value="1" /> <?php esc_html_e( 'Regenerate the bypass secret on save (invalidates the URL above)', 'jengo-iam-sso' ); ?></label>
						</td>
					</tr>
					<tr>
						<th scope="row"><?php esc_html_e( 'Endpoint overrides (optional)', 'jengo-iam-sso' ); ?></th>
						<td>
							<p class="description"><?php esc_html_e( 'Fallbacks when OIDC discovery is unreachable. Leave blank to use discovery / issuer defaults.', 'jengo-iam-sso' ); ?></p>
							<p><label for="jengo-authz-endpoint"><?php esc_html_e( 'Authorization endpoint', 'jengo-iam-sso' ); ?></label><br />
								<input name="<?php echo esc_attr( $option_name ); ?>[authorization_endpoint]" id="jengo-authz-endpoint" type="url" class="regular-text" value="<?php echo esc_attr( $options['authorization_endpoint'] ); ?>" /></p>
							<p><label for="jengo-token-endpoint"><?php esc_html_e( 'Token endpoint', 'jengo-iam-sso' ); ?></label><br />
								<input name="<?php echo esc_attr( $option_name ); ?>[token_endpoint]" id="jengo-token-endpoint" type="url" class="regular-text" value="<?php echo esc_attr( $options['token_endpoint'] ); ?>" /></p>
							<p><label for="jengo-userinfo-endpoint"><?php esc_html_e( 'Userinfo endpoint', 'jengo-iam-sso' ); ?></label><br />
								<input name="<?php echo esc_attr( $option_name ); ?>[userinfo_endpoint]" id="jengo-userinfo-endpoint" type="url" class="regular-text" value="<?php echo esc_attr( $options['userinfo_endpoint'] ); ?>" /></p>
						</td>
					</tr>
				</table>
				<?php submit_button(); ?>
			</form>
		</div>
		<?php
	}
}
