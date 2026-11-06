<?php
/**
 * PHPUnit bootstrap: WordPress stubs first, then the plugin itself.
 */

define( 'ABSPATH', __DIR__ . '/' );

require_once __DIR__ . '/stubs/wordpress.php';
require_once __DIR__ . '/support/FakeIam.php';

Jengo_Test_World::reset();

require_once dirname( __DIR__ ) . '/jengo-iam-sso/jengo-iam-sso.php';

require_once __DIR__ . '/support/JengoTestCase.php';
