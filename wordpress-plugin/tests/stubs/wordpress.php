<?php
/**
 * Minimal WordPress stand-ins for the unit tests: just enough of the WP API
 * for the plugin code to run for real (options, transients, users, HTTP,
 * redirects, cookies). State lives in Jengo_Test_World and is reset per test.
 *
 * Behaviour that matters for the plugin's guarantees is modelled faithfully:
 * wp_die() and wp_redirect() stop execution (they throw, as `exit` would),
 * transients expire, add_query_arg() does NOT encode values (like core).
 */

define( 'MINUTE_IN_SECONDS', 60 );
define( 'HOUR_IN_SECONDS', 3600 );
define( 'DAY_IN_SECONDS', 86400 );
define( 'COOKIEPATH', '/' );

class Jengo_Test_Redirect extends RuntimeException {
	public $location;
	public function __construct( $location ) {
		parent::__construct( 'redirect: ' . $location );
		$this->location = $location;
	}
}

class Jengo_Test_Die extends RuntimeException {
}

class Jengo_Test_Cli_Error extends RuntimeException {
}

class WP_Error {
	private $code;
	private $message;
	public function __construct( $code = '', $message = '', $data = '' ) {
		$this->code    = $code;
		$this->message = $message;
	}
	public function get_error_code() {
		return $this->code;
	}
	public function get_error_message() {
		return $this->message;
	}
}

class WP_Role {
	public $name;
	public $capabilities;
	public function __construct( $name, array $caps ) {
		$this->name         = $name;
		$this->capabilities = array_fill_keys( $caps, true );
	}
	public function has_cap( $cap ) {
		return ! empty( $this->capabilities[ $cap ] );
	}
}

class WP_User {
	public $ID;
	public $user_login;
	public $user_email;
	public $display_name;
	public $roles = array();
	public function __construct( $id, $login, $email, array $roles = array() ) {
		$this->ID           = $id;
		$this->user_login   = $login;
		$this->user_email   = $email;
		$this->display_name = $login;
		$this->roles        = $roles;
	}
	public function set_role( $role ) {
		$this->roles = array( $role );
		Jengo_Test_World::$role_changes[] = array( $this->ID, $role );
	}
}

class WP_CLI {
	public static $lines    = array();
	public static $success  = array();
	public static $commands = array();
	public static function line( $text = '' ) {
		self::$lines[] = $text;
	}
	public static function success( $text ) {
		self::$success[] = $text;
	}
	public static function error( $text ) {
		throw new Jengo_Test_Cli_Error( $text );
	}
	public static function add_command( $name, $callable ) {
		self::$commands[ $name ] = $callable;
	}
}
define( 'WP_CLI', true );

class Jengo_Test_World {
	public static $site = 'https://test.prospergenics.com';

	public static $options      = array();
	public static $transients   = array();
	public static $users        = array();
	public static $usermeta     = array();
	public static $next_user_id = 100;
	public static $role_changes = array();
	public static $inserted     = array();
	public static $cookies      = array();
	public static $auth_cookie  = null;
	public static $current_user = 0;
	public static $actions_done = array();
	public static $filters      = array();
	public static $hooks        = array();
	public static $http_log     = array();
	public static $http_routes  = array();
	public static $now_offset   = 0;
	public static $settings_errors = array();

	public static $roles = array();

	public static function reset() {
		self::$options      = array();
		self::$transients   = array();
		self::$users        = array();
		self::$usermeta     = array();
		self::$next_user_id = 100;
		self::$role_changes = array();
		self::$inserted     = array();
		self::$cookies      = array();
		self::$auth_cookie  = null;
		self::$current_user = 0;
		self::$actions_done = array();
		self::$http_log     = array();
		self::$http_routes  = array();
		self::$now_offset   = 0;
		self::$settings_errors = array();

		self::$roles = array(
			'administrator' => new WP_Role( 'administrator', array( 'manage_options', 'promote_users', 'edit_users', 'delete_users', 'install_plugins', 'edit_posts', 'read' ) ),
			'editor'        => new WP_Role( 'editor', array( 'edit_posts', 'edit_others_posts', 'publish_posts', 'read' ) ),
			'author'        => new WP_Role( 'author', array( 'edit_posts', 'publish_posts', 'read' ) ),
			'contributor'   => new WP_Role( 'contributor', array( 'edit_posts', 'read' ) ),
			'subscriber'    => new WP_Role( 'subscriber', array( 'read' ) ),
			'shop_manager'  => new WP_Role( 'shop_manager', array( 'edit_posts', 'read' ) ),
			'user_admin'    => new WP_Role( 'user_admin', array( 'edit_users', 'read' ) ),
		);

		$_GET     = array();
		$_POST    = array();
		$_REQUEST = array();
		$_COOKIE  = array();

		$_SERVER['REQUEST_METHOD'] = 'GET';
	}

	public static function add_user( $login, $email, array $roles = array( 'subscriber' ) ) {
		$id = self::$next_user_id++;
		self::$users[ $id ] = new WP_User( $id, $login, $email, $roles );
		return self::$users[ $id ];
	}

	/** Register a fake HTTP endpoint: callable( array $args ): array{0:int,1:string}|WP_Error|null. */
	public static function route( $method, $url, $handler ) {
		self::$http_routes[ strtoupper( $method ) . ' ' . $url ] = $handler;
	}

	public static function cookie_value( $name ) {
		return isset( self::$cookies[ $name ] ) ? self::$cookies[ $name ]['value'] : null;
	}
}

/* ---- hooks ---------------------------------------------------------- */

function add_action( $hook, $callback, $priority = 10, $args = 1 ) {
	Jengo_Test_World::$hooks[ $hook ][] = array( $callback, $priority, $args );
	return true;
}
function add_filter( $hook, $callback, $priority = 10, $args = 1 ) {
	Jengo_Test_World::$hooks[ $hook ][] = array( $callback, $priority, $args );
	return true;
}
function do_action( $hook, ...$args ) {
	Jengo_Test_World::$actions_done[] = array( $hook, $args );
}
function register_activation_hook( $file, $callback ) {
}
function register_setting( $group, $name, $args = array() ) {
}
function add_options_page() {
}
function plugin_dir_path( $file ) {
	return rtrim( str_replace( '\\', '/', dirname( $file ) ), '/' ) . '/';
}

/* ---- options / transients -------------------------------------------- */

function get_option( $name, $default = false ) {
	return array_key_exists( $name, Jengo_Test_World::$options ) ? Jengo_Test_World::$options[ $name ] : $default;
}
function update_option( $name, $value, $autoload = null ) {
	Jengo_Test_World::$options[ $name ] = $value;
	return true;
}
function get_transient( $key ) {
	if ( ! isset( Jengo_Test_World::$transients[ $key ] ) ) {
		return false;
	}
	list( $value, $expires ) = Jengo_Test_World::$transients[ $key ];
	if ( $expires && $expires < time() + Jengo_Test_World::$now_offset ) {
		unset( Jengo_Test_World::$transients[ $key ] );
		return false;
	}
	return $value;
}
function set_transient( $key, $value, $ttl = 0 ) {
	Jengo_Test_World::$transients[ $key ] = array( $value, $ttl ? time() + Jengo_Test_World::$now_offset + $ttl : 0 );
	return true;
}
function delete_transient( $key ) {
	unset( Jengo_Test_World::$transients[ $key ] );
	return true;
}

/* ---- urls ------------------------------------------------------------- */

function home_url( $path = '' ) {
	return Jengo_Test_World::$site . $path;
}
function wp_login_url() {
	return Jengo_Test_World::$site . '/wp-login.php';
}
function admin_url( $path = '' ) {
	return Jengo_Test_World::$site . '/wp-admin/' . $path;
}
function is_ssl() {
	return true;
}
function untrailingslashit( $value ) {
	return rtrim( $value, '/\\' );
}
function add_query_arg( ...$args ) {
	if ( is_array( $args[0] ) ) {
		$params = $args[0];
		$url    = $args[1];
	} else {
		$params = array( $args[0] => $args[1] );
		$url    = $args[2];
	}
	$query = '';
	if ( false !== strpos( $url, '?' ) ) {
		list( $url, $query ) = explode( '?', $url, 2 );
	}
	parse_str( $query, $existing );
	$merged = array_merge( $existing, $params );
	$pairs  = array();
	foreach ( $merged as $k => $v ) {
		$pairs[] = $k . '=' . $v; // like core: values are NOT encoded here.
	}
	return $url . ( $pairs ? '?' . implode( '&', $pairs ) : '' );
}
function wp_validate_redirect( $location, $default = '' ) {
	$host = parse_url( $location, PHP_URL_HOST );
	if ( null === $host || false === $host ) {
		return $location;
	}
	return parse_url( Jengo_Test_World::$site, PHP_URL_HOST ) === $host ? $location : $default;
}

/* ---- sanitizing / escaping -------------------------------------------- */

function wp_unslash( $value ) {
	return $value;
}
function sanitize_text_field( $value ) {
	return trim( strip_tags( (string) $value ) );
}
function sanitize_textarea_field( $value ) {
	return trim( strip_tags( (string) $value ) );
}
function sanitize_key( $value ) {
	return preg_replace( '/[^a-z0-9_\-]/', '', strtolower( (string) $value ) );
}
function sanitize_email( $value ) {
	$value = trim( (string) $value );
	return false !== filter_var( $value, FILTER_VALIDATE_EMAIL ) ? $value : '';
}
function sanitize_user( $value, $strict = false ) {
	return preg_replace( '/[^a-z0-9_.\-@]/i', '', (string) $value );
}
function is_email( $value ) {
	return false !== filter_var( $value, FILTER_VALIDATE_EMAIL );
}
function esc_url_raw( $url ) {
	return trim( (string) $url );
}
function esc_url( $url ) {
	return htmlspecialchars( (string) $url, ENT_QUOTES );
}
function esc_html( $text ) {
	return htmlspecialchars( (string) $text, ENT_QUOTES );
}
function esc_attr( $text ) {
	return htmlspecialchars( (string) $text, ENT_QUOTES );
}
function esc_textarea( $text ) {
	return htmlspecialchars( (string) $text, ENT_QUOTES );
}
function __( $text, $domain = 'default' ) {
	return $text;
}
function esc_html__( $text, $domain = 'default' ) {
	return htmlspecialchars( $text, ENT_QUOTES );
}
function esc_html_e( $text, $domain = 'default' ) {
	echo htmlspecialchars( $text, ENT_QUOTES );
}
function wp_hash( $data ) {
	return hash_hmac( 'sha256', $data, 'jengo-test-salt' );
}
function wp_generate_password( $length = 12, $special = true, $extra = false ) {
	return substr( bin2hex( random_bytes( $length ) ), 0, $length );
}

/* ---- errors / http ------------------------------------------------------ */

function is_wp_error( $thing ) {
	return $thing instanceof WP_Error;
}

function wp_die( $message = '', $title = '', $args = array() ) {
	throw new Jengo_Test_Die( (string) $message );
}

function wp_redirect( $location, $status = 302 ) {
	throw new Jengo_Test_Redirect( $location );
}
function wp_safe_redirect( $location, $status = 302 ) {
	throw new Jengo_Test_Redirect( $location );
}

function wp_remote_get( $url, $args = array() ) {
	return jengo_test_http( 'GET', $url, $args );
}
function wp_remote_post( $url, $args = array() ) {
	return jengo_test_http( 'POST', $url, $args );
}
function jengo_test_http( $method, $url, $args ) {
	Jengo_Test_World::$http_log[] = array( $method, $url, $args );
	$key = $method . ' ' . $url;
	if ( ! isset( Jengo_Test_World::$http_routes[ $key ] ) ) {
		return new WP_Error( 'http_request_failed', 'no route: ' . $key );
	}
	$result = call_user_func( Jengo_Test_World::$http_routes[ $key ], $args );
	if ( $result instanceof WP_Error ) {
		return $result;
	}
	return array(
		'response' => array( 'code' => $result[0] ),
		'body'     => $result[1],
	);
}
function wp_remote_retrieve_body( $response ) {
	return is_array( $response ) ? $response['body'] : '';
}
function wp_remote_retrieve_response_code( $response ) {
	return is_array( $response ) ? $response['response']['code'] : '';
}

/* ---- users --------------------------------------------------------------- */

function get_role( $name ) {
	return isset( Jengo_Test_World::$roles[ $name ] ) ? Jengo_Test_World::$roles[ $name ] : null;
}
function get_users( $args = array() ) {
	$found = array();
	foreach ( Jengo_Test_World::$users as $user ) {
		if ( isset( $args['meta_key'] ) ) {
			$value = Jengo_Test_World::$usermeta[ $user->ID ][ $args['meta_key'] ] ?? null;
			if ( $value !== $args['meta_value'] ) {
				continue;
			}
		}
		$found[] = $user;
	}
	return isset( $args['number'] ) ? array_slice( $found, 0, $args['number'] ) : $found;
}
function get_user_by( $field, $value ) {
	foreach ( Jengo_Test_World::$users as $user ) {
		if ( 'email' === $field && strtolower( $user->user_email ) === strtolower( $value ) ) {
			return $user;
		}
		if ( 'id' === $field && (int) $user->ID === (int) $value ) {
			return $user;
		}
		if ( 'login' === $field && $user->user_login === $value ) {
			return $user;
		}
	}
	return false;
}
function username_exists( $login ) {
	foreach ( Jengo_Test_World::$users as $user ) {
		if ( $user->user_login === $login ) {
			return $user->ID;
		}
	}
	return false;
}
function wp_insert_user( $data ) {
	$user               = Jengo_Test_World::add_user( $data['user_login'], $data['user_email'], array( $data['role'] ) );
	$user->display_name = $data['display_name'];
	Jengo_Test_World::$inserted[] = $data;
	return $user->ID;
}
function update_user_meta( $user_id, $key, $value ) {
	Jengo_Test_World::$usermeta[ $user_id ][ $key ] = $value;
	return true;
}
function get_user_meta( $user_id, $key, $single = false ) {
	return Jengo_Test_World::$usermeta[ $user_id ][ $key ] ?? '';
}
function wp_set_current_user( $id, $login = '' ) {
	Jengo_Test_World::$current_user = $id;
}
function wp_set_auth_cookie( $user_id, $remember = false, $secure = '' ) {
	Jengo_Test_World::$auth_cookie = $user_id;
}
function wp_doing_ajax() {
	return false;
}

/* ---- cookies (recorder replaces the plugin's setcookie wrapper) ---------- */

function jengo_iam_sso_set_cookie( $name, $value, $expires ) {
	Jengo_Test_World::$cookies[ $name ] = array(
		'value'   => $value,
		'expires' => $expires,
	);
}

/* ---- admin / settings page helpers --------------------------------------- */

function current_user_can( $capability ) {
	return true;
}
function settings_fields( $group ) {
}
function submit_button() {
}
function checked( $value, $current = true, $echo = true ) {
	$result = ( (string) $value === (string) $current ) ? ' checked="checked"' : '';
	if ( $echo ) {
		echo $result;
	}
	return $result;
}
function selected( $value, $current = true, $echo = true ) {
	$result = ( (string) $value === (string) $current ) ? ' selected="selected"' : '';
	if ( $echo ) {
		echo $result;
	}
	return $result;
}
function wp_roles() {
	$roles = array();
	foreach ( Jengo_Test_World::$roles as $key => $role ) {
		$roles[ $key ] = array( 'name' => ucfirst( $key ) );
	}
	return (object) array( 'roles' => $roles );
}
function translate_user_role( $name ) {
	return $name;
}
function add_settings_error( $setting, $code, $message, $type = 'error' ) {
	Jengo_Test_World::$settings_errors[] = array( $setting, $code );
}
