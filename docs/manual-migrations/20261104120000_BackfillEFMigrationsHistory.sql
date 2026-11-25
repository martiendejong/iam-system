-- Task 7118: backfill "__EFMigrationsHistory" so it matches the schema that is actually applied to live iam_db.
-- IAM boots with EnsureCreatedAsync and never calls Migrate(), so the history table only held 3 rows.
-- Only the history table is touched. ProductVersion is 10.0.10 for every row (EF never reads it when deciding what is applied).
-- Idempotent: ON CONFLICT DO NOTHING, a second run reports INSERT 0 0.
BEGIN;

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion") VALUES
    ('20260322140003_InitialIAMSchema', '10.0.10'),
    ('20260322155105_UpdatedIAMSchema', '10.0.10'),
    ('20260322160321_AddOpenIddict', '10.0.10'),
    ('20260322180125_AddPolicyEntity', '10.0.10'),
    ('20260322183752_AddTemporalPolicyEntities', '10.0.10'),
    ('20260322184111_AddAuditAndCompliance', '10.0.10'),
    ('20260322184246_AddPolicyTesting', '10.0.10'),
    ('20260322184628_AddEmergencyOverride', '10.0.10'),
    ('20260325202129_AddIoTDeviceSupport', '10.0.10'),
    ('20260325230808_AddAllIAMFeatures', '10.0.10'),
    ('20260723214426_AddTenantBranding', '10.0.10'),
    ('20260723214508_AddInvitationsAndOrganizationSettings', '10.0.10'),
    ('20260723220000_AddDirectorySync', '10.0.10'),
    ('20260724010000_AddOtpAndRiskTables', '10.0.10'),
    ('20260924104218_AddApiKeyScopeAndVaultReference', '10.0.10'),
    ('20260926120000_AddAllowedRedirectUrisToIdentityProvider', '10.0.10'),
    ('20261002120000_AddApprovalVotes', '10.0.10'),
    ('20261005120000_AddPrincipalKindAndManager', '10.0.10')
ON CONFLICT ("MigrationId") DO NOTHING;

COMMIT;
