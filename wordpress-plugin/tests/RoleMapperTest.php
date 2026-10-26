<?php

/**
 * IAM role -> WordPress role map: naming convention, explicit map, ordering,
 * unknown roles, privileged-role detection.
 */
class RoleMapperTest extends JengoTestCase {

	public function test_convention_maps_client_prefixed_standard_roles() {
		$roles = Jengo_IAM_SSO_Role_Mapper::map_roles(
			array( 'role' => array( 'wp-test-client:editor', 'wp-test-client:subscriber' ) ),
			'wp-test-client'
		);

		$this->assertSame( array( 'editor', 'subscriber' ), $roles );
	}

	public function test_roles_of_other_clients_do_not_map() {
		$roles = Jengo_IAM_SSO_Role_Mapper::map_roles( array( 'role' => array( 'other-client:administrator', 'administrator' ) ), 'wp-test-client' );

		$this->assertSame( array(), $roles );
	}

	public function test_a_single_role_arrives_as_a_string_and_roles_is_accepted_as_an_alias() {
		$this->assertSame( array( 'author' ), Jengo_IAM_SSO_Role_Mapper::map_roles( array( 'role' => 'wp-test-client:author' ), 'wp-test-client' ) );
		$this->assertSame( array( 'author' ), Jengo_IAM_SSO_Role_Mapper::map_roles( array( 'roles' => array( 'wp-test-client:author' ) ), 'wp-test-client' ) );
	}

	public function test_matching_is_case_insensitive() {
		$roles = Jengo_IAM_SSO_Role_Mapper::map_roles( array( 'role' => array( 'WP-Test-Client:Editor' ) ), 'wp-test-client' );

		$this->assertSame( array( 'editor' ), $roles );
	}

	public function test_convention_ranks_administrator_over_editor_over_author_over_contributor_over_subscriber() {
		$roles = Jengo_IAM_SSO_Role_Mapper::map_roles(
			array( 'role' => array( 'c:subscriber', 'c:contributor', 'c:author', 'c:editor', 'c:administrator' ) ),
			'c'
		);

		$this->assertSame( array( 'administrator', 'editor', 'author', 'contributor', 'subscriber' ), $roles );
	}

	public function test_explicit_map_entries_apply_and_rank_ahead_of_the_convention() {
		$map   = "TeamLead = editor\nIntern = subscriber\n";
		$roles = Jengo_IAM_SSO_Role_Mapper::map_roles( array( 'role' => array( 'teamlead', 'c:author' ) ), 'c', $map );

		$this->assertSame( array( 'editor', 'author' ), $roles );
	}

	public function test_first_explicit_line_wins_over_later_lines() {
		$map   = "Staff = author\nManagers = editor\n";
		$roles = Jengo_IAM_SSO_Role_Mapper::map_roles( array( 'role' => array( 'managers', 'staff' ) ), 'c', $map );

		$this->assertSame( array( 'author', 'editor' ), $roles );
	}

	public function test_explicit_map_can_target_a_custom_role() {
		$roles = Jengo_IAM_SSO_Role_Mapper::map_roles( array( 'role' => array( 'Shop' ) ), 'c', 'shop = shop_manager' );

		$this->assertSame( array( 'shop_manager' ), $roles );
	}

	public function test_roles_that_do_not_exist_on_this_site_are_ignored() {
		$roles = Jengo_IAM_SSO_Role_Mapper::map_roles( array( 'role' => array( 'ghost', 'c:editor' ) ), 'c', 'ghost = no_such_role' );

		$this->assertSame( array( 'editor' ), $roles );
	}

	public function test_duplicates_collapse() {
		$roles = Jengo_IAM_SSO_Role_Mapper::map_roles( array( 'role' => array( 'a', 'b', 'c:editor' ) ), 'c', "a = editor\nb = editor" );

		$this->assertSame( array( 'editor' ), $roles );
	}

	public function test_parse_map_skips_blank_lines_comments_and_garbage() {
		$map = Jengo_IAM_SSO_Role_Mapper::parse_map( "# comment\n\nTeamLead = editor\nno-equals-sign\n= nothing\nnothing =\n  Spaced   =   Author  \r\nTeamLead = subscriber" );

		$this->assertSame( array( 'teamlead' => 'editor', 'spaced' => 'author' ), $map );
	}

	public function test_non_string_role_claims_are_ignored() {
		$roles = Jengo_IAM_SSO_Role_Mapper::map_roles( array( 'role' => array( 1, null, array( 'x' ), 'c:editor' ) ), 'c' );

		$this->assertSame( array( 'editor' ), $roles );
	}

	public function test_an_empty_client_id_never_matches_the_convention() {
		$this->assertSame( array(), Jengo_IAM_SSO_Role_Mapper::map_roles( array( 'role' => array( ':editor', 'editor' ) ), '' ) );
	}

	public function test_privileged_roles_are_detected_by_capability_not_just_by_name() {
		$this->assertTrue( Jengo_IAM_SSO_Role_Mapper::is_privileged_role( 'administrator' ) );
		$this->assertTrue( Jengo_IAM_SSO_Role_Mapper::is_privileged_role( 'user_admin' ) );
		$this->assertFalse( Jengo_IAM_SSO_Role_Mapper::is_privileged_role( 'editor' ) );
		$this->assertFalse( Jengo_IAM_SSO_Role_Mapper::is_privileged_role( 'shop_manager' ) );
		$this->assertFalse( Jengo_IAM_SSO_Role_Mapper::is_privileged_role( 'subscriber' ) );
		$this->assertFalse( Jengo_IAM_SSO_Role_Mapper::is_privileged_role( 'no_such_role' ) );
	}
}
