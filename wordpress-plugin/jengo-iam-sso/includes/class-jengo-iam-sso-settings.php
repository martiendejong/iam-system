<?php
/**
 * Settings -> Jengo IAM SSO.
 *
 * Capability: manage_options. The client secret is write-only: it is never
 * echoed back into the form (a blank field keeps the stored value) and can be
 * defined in wp-config.php instead (JENGO_IAM_SSO_CLIENT_SECRET) so it never
 * has to live in the database.
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
		$clean['logout_iam']     = empty( $input['logout_iam'] ) ? 0 : 1;

		// The default role for new accounts can never be administrator (or any
		// other role that can manage users, plugins or options).
		if ( isset( $input['default_role'] ) ) {
			$role = sanitize_key( (string) $input['default_role'] );
			if ( null !== get_role( $role ) && ! Jengo_IAM_SSO_Role_Mapper::is_privileged_role( $role ) ) {
				$clean['default_role'] = $role;
			} else {
				add_settings_error( self::SETTINGS_SLUG, 'default_role', __( 'That role cannot be the default for new accounts (administrators and other user-management roles are excluded).', 'jengo-iam-sso' ) );
			}
		}

		if ( isset( $input['role_map'] ) ) {
			$clean['role_map'] = sanitize_textarea_field( (string) $input['role_map'] );
		}

		if ( ! empty( $input['regenerate_bypass'] ) ) {
			$clean['bypass_secret'] = bin2hex( random_bytes( 20 ) );
		}

		// Issuer/credentials may have changed: drop cached discovery and JWKS.
		( new Jengo_IAM_SSO_OIDC_Client( $current ) )->flush_cache();
		( new Jengo_IAM_SSO_OIDC_Client( $clean ) )->flush_cache();

		return $clean;
	}

	public function render_settings_page() {
		if ( ! current_user_can( 'manage_options' ) ) {
			return;
		}

		$options       = jengo_iam_sso_get_options();
		$option_name   = JENGO_IAM_SSO_OPTION;
		$redirect_uri  = Jengo_IAM_SSO_OIDC_Client::redirect_uri();
		$logout_uri    = Jengo_IAM_SSO_OIDC_Client::post_logout_redirect_uri();
		$bypass_url    = add_query_arg( 'jengo_sso_bypass', $options['bypass_secret'], wp_login_url() );
		$secret_in_cfg = defined( 'JENGO_IAM_SSO_CLIENT_SECRET' ) && '' !== (string) JENGO_IAM_SSO_CLIENT_SECRET;
		$discovery     = ( new Jengo_IAM_SSO_OIDC_Client( $options ) )->get_discovery();

		$default_role = Jengo_IAM_SSO_Role_Mapper::is_privileged_role( $options['default_role'] ) ? 'subscriber' : $options['default_role'];
		?>
		<div class="wrap">
			<h1><?php esc_html_e( 'Jengo IAM SSO', 'jengo-iam-sso' ); ?></h1>
			<p><?php esc_html_e( 'Single sign-on via Jengo IAM (OpenID Connect, authorization code + PKCE). Register these exact URIs on the IAM client:', 'jengo-iam-sso' ); ?></p>
			<ul>
				<li><?php esc_html_e( 'Redirect URI:', 'jengo-iam-sso' ); ?> <code><?php echo esc_html( $redirect_uri ); ?></code></li>
				<li><?php esc_html_e( 'Post-logout redirect URI:', 'jengo-iam-sso' ); ?> <code><?php echo esc_html( $logout_uri ); ?></code></li>
			</ul>
			<p>
				<strong><?php esc_html_e( 'IAM connection:', 'jengo-iam-sso' ); ?></strong>
				<?php
				if ( is_wp_error( $discovery ) ) {
					/* translators: %s: short error code, e.g. issuer_mismatch. */
					echo esc_html( sprintf( __( 'not reachable or invalid (%s). Check the issuer URL; it must equal the "issuer" in the discovery document.', 'jengo-iam-sso' ), $discovery->get_error_message() ) );
				} else {
					esc_html_e( 'OK (discovery document found and issuer matches).', 'jengo-iam-sso' );
				}
				?>
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
							<?php if ( $secret_in_cfg ) : ?>
								<p><?php esc_html_e( 'Defined in wp-config.php (JENGO_IAM_SSO_CLIENT_SECRET); the field below is ignored.', 'jengo-iam-sso' ); ?></p>
							<?php endif; ?>
							<input name="<?php echo esc_attr( $option_name ); ?>[client_secret]" id="jengo-client-secret" type="password" class="regular-text" value="" autocomplete="new-password" placeholder="<?php echo esc_attr( '' !== $options['client_secret'] ? __( '(secret stored, leave blank to keep)', 'jengo-iam-sso' ) : '' ); ?>" />
							<p class="description"><?php esc_html_e( 'Stored on save and never shown again; leave blank to keep the current secret. It can also be set in wp-config.php.', 'jengo-iam-sso' ); ?></p>
						</td>
					</tr>
					<tr>
						<th scope="row"><?php esc_html_e( 'Login mode', 'jengo-iam-sso' ); ?></th>
						<td>
							<fieldset>
								<label><input type="radio" name="<?php echo esc_attr( $option_name ); ?>[login_mode]" value="both" <?php checked( $options['login_mode'], 'both' ); ?> /> <?php esc_html_e( 'Both: WordPress form plus a "Log in with Jengo IAM" button (default)', 'jengo-iam-sso' ); ?></label><br />
								<label><input type="radio" name="<?php echo esc_attr( $option_name ); ?>[login_mode]" value="iam" <?php checked( $options['login_mode'], 'iam' ); ?> /> <?php esc_html_e( 'IAM only: wp-login.php redirects straight to Jengo IAM and password logins are refused (emergency bypass below keeps working)', 'jengo-iam-sso' ); ?></label><br />
								<label><input type="radio" name="<?php echo esc_attr( $option_name ); ?>[login_mode]" value="wordpress" <?php checked( $options['login_mode'], 'wordpress' ); ?> /> <?php esc_html_e( 'WordPress only: SSO disabled on the login form', 'jengo-iam-sso' ); ?></label>
							</fieldset>
						</td>
					</tr>
					<tr>
						<th scope="row"><?php esc_html_e( 'Auto-provision users', 'jengo-iam-sso' ); ?></th>
						<td>
							<label><input type="checkbox" name="<?php echo esc_attr( $option_name ); ?>[auto_provision]" value="1" <?php checked( $options['auto_provision'], 1 ); ?> /> <?php esc_html_e( 'Create a WordPress account on first IAM login (off by default; requires an IAM-granted role for this site and a verified e-mail address)', 'jengo-iam-sso' ); ?></label>
						</td>
					</tr>
					<tr>
						<th scope="row"><label for="jengo-default-role"><?php esc_html_e( 'Default role for new users', 'jengo-iam-sso' ); ?></label></th>
						<td>
							<select name="<?php echo esc_attr( $option_name ); ?>[default_role]" id="jengo-default-role">
								<?php
								foreach ( wp_roles()->roles as $role_key => $role_details ) {
									if ( Jengo_IAM_SSO_Role_Mapper::is_privileged_role( $role_key ) ) {
										continue;
									}
									echo '<option value="' . esc_attr( $role_key ) . '"' . selected( $default_role, $role_key, false ) . '>' . esc_html( translate_user_role( $role_details['name'] ) ) . '</option>';
								}
								?>
							</select>
							<p class="description"><?php esc_html_e( 'Used for new accounts when role sync is off. Administrator and other user-management roles are not offered.', 'jengo-iam-sso' ); ?></p>
						</td>
					</tr>
					<tr>
						<th scope="row"><?php esc_html_e( 'Sync role from IAM', 'jengo-iam-sso' ); ?></th>
						<td>
							<label><input type="checkbox" name="<?php echo esc_attr( $option_name ); ?>[sync_roles]" value="1" <?php checked( $options['sync_roles'], 1 ); ?> /> <?php esc_html_e( 'On every SSO login, set the WordPress role from the IAM roles claim', 'jengo-iam-sso' ); ?></label>
						</td>
					</tr>
					<tr>
						<th scope="row"><label for="jengo-role-map"><?php esc_html_e( 'IAM role to WordPress role map', 'jengo-iam-sso' ); ?></label></th>
						<td>
							<textarea name="<?php echo esc_attr( $option_name ); ?>[role_map]" id="jengo-role-map" rows="5" cols="50" class="large-text code"><?php echo esc_textarea( $options['role_map'] ); ?></textarea>
							<p class="description"><?php esc_html_e( 'Optional. One "iam_role = wp_role" per line, first line wins, e.g. TeamLead = editor. IAM roles named "{client id}:administrator|editor|author|contributor|subscriber" map automatically and rank below these lines.', 'jengo-iam-sso' ); ?></p>
						</td>
					</tr>
					<tr>
						<th scope="row"><?php esc_html_e( 'Logout', 'jengo-iam-sso' ); ?></th>
						<td>
							<label><input type="checkbox" name="<?php echo esc_attr( $option_name ); ?>[logout_iam]" value="1" <?php checked( $options['logout_iam'], 1 ); ?> /> <?php esc_html_e( 'Also log out of Jengo IAM when a user who signed in through IAM logs out of WordPress', 'jengo-iam-sso' ); ?></label>
						</td>
					</tr>
					<tr>
						<th scope="row"><?php esc_html_e( 'Emergency bypass', 'jengo-iam-sso' ); ?></th>
						<td>
							<?php if ( ! empty( $options['bypass_secret'] ) ) : ?>
								<p><?php esc_html_e( 'With login mode "IAM only", this URL still shows the normal WordPress login form:', 'jengo-iam-sso' ); ?></p>
								<p><code><?php echo esc_html( $bypass_url ); ?></code></p>
							<?php else : ?>
								<p><?php esc_html_e( 'No bypass secret yet; it is generated automatically on plugin activation.', 'jengo-iam-sso' ); ?></p>
							<?php endif; ?>
							<label><input type="checkbox" name="<?php echo esc_attr( $option_name ); ?>[regenerate_bypass]" value="1" /> <?php esc_html_e( 'Regenerate the bypass secret on save (invalidates the URL above)', 'jengo-iam-sso' ); ?></label>
							<p class="description"><?php esc_html_e( 'Other emergency routes: add define( \'JENGO_IAM_SSO_BYPASS\', true ); to wp-config.php, or run "wp jengo-iam-sso set-mode wordpress". If IAM cannot be reached, the normal form is shown automatically.', 'jengo-iam-sso' ); ?></p>
						</td>
					</tr>
				</table>
				<?php submit_button(); ?>
			</form>
		</div>
		<?php
	}
}
