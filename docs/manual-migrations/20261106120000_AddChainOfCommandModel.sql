-- Task 4843 (Delegated Authority epic): Chain of Command data model. Purely additive (new tables only)
-- and idempotent. IAM creates its schema with EnsureCreated, which is a no-op on an existing database,
-- so apply this to iam_db BEFORE starting a build that contains the new entities.
-- Identities are referenced (Users, Groups, Roles, ServiceAccounts), never copied.
BEGIN;

CREATE TABLE IF NOT EXISTS "AuthorityDomains" (
    "Id" uuid NOT NULL,
    "TenantId" uuid NOT NULL,
    "Key" character varying(100) NOT NULL,
    "Name" character varying(255) NOT NULL,
    "Description" character varying(2000) NULL,
    "IsEnabled" boolean NOT NULL,
    "Version" integer NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    "CreatedByUserId" uuid NULL,
    "UpdatedAt" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_AuthorityDomains" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_AuthorityDomains_Tenants_TenantId" FOREIGN KEY ("TenantId") REFERENCES "Tenants" ("Id") ON DELETE CASCADE
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_AuthorityDomains_TenantId_Key" ON "AuthorityDomains" ("TenantId", "Key");

CREATE TABLE IF NOT EXISTS "CommandChains" (
    "Id" uuid NOT NULL,
    "TenantId" uuid NOT NULL,
    "Name" character varying(255) NOT NULL,
    "Description" character varying(2000) NULL,
    "EffectiveFrom" timestamp with time zone NOT NULL,
    "EffectiveUntil" timestamp with time zone NULL,
    "IsEnabled" boolean NOT NULL,
    "Version" integer NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    "CreatedByUserId" uuid NULL,
    "UpdatedAt" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_CommandChains" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_CommandChains_Tenants_TenantId" FOREIGN KEY ("TenantId") REFERENCES "Tenants" ("Id") ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS "IX_CommandChains_TenantId_IsEnabled" ON "CommandChains" ("TenantId", "IsEnabled");

CREATE TABLE IF NOT EXISTS "AuthorityPolicies" (
    "Id" uuid NOT NULL,
    "TenantId" uuid NOT NULL,
    "AuthorityDomainId" uuid NOT NULL,
    "ScopeLevel" integer NOT NULL,
    "ProjectKey" character varying(200) NULL,
    "ResourceType" character varying(100) NULL,
    "ResourceId" character varying(200) NULL,
    "Action" character varying(200) NULL,
    "IsExplicitOverride" boolean NOT NULL,
    "CommandChainId" uuid NOT NULL,
    "ContinuityPolicyRef" character varying(500) NULL,
    "EffectiveFrom" timestamp with time zone NOT NULL,
    "EffectiveUntil" timestamp with time zone NULL,
    "IsEnabled" boolean NOT NULL,
    "Version" integer NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    "CreatedByUserId" uuid NULL,
    "UpdatedAt" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_AuthorityPolicies" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_AuthorityPolicies_Tenants_TenantId" FOREIGN KEY ("TenantId") REFERENCES "Tenants" ("Id") ON DELETE CASCADE,
    CONSTRAINT "FK_AuthorityPolicies_AuthorityDomains_AuthorityDomainId" FOREIGN KEY ("AuthorityDomainId") REFERENCES "AuthorityDomains" ("Id") ON DELETE CASCADE,
    CONSTRAINT "FK_AuthorityPolicies_CommandChains_CommandChainId" FOREIGN KEY ("CommandChainId") REFERENCES "CommandChains" ("Id") ON DELETE RESTRICT
);
CREATE INDEX IF NOT EXISTS "IX_AuthorityPolicies_Tenant_Domain_Level_Enabled" ON "AuthorityPolicies" ("TenantId", "AuthorityDomainId", "ScopeLevel", "IsEnabled");
CREATE INDEX IF NOT EXISTS "IX_AuthorityPolicies_Domain_Scope" ON "AuthorityPolicies" ("AuthorityDomainId", "ProjectKey", "ResourceType", "ResourceId", "Action");
CREATE INDEX IF NOT EXISTS "IX_AuthorityPolicies_CommandChainId" ON "AuthorityPolicies" ("CommandChainId");

CREATE TABLE IF NOT EXISTS "CommandStages" (
    "Id" uuid NOT NULL,
    "CommandChainId" uuid NOT NULL,
    "Order" integer NOT NULL,
    "Name" character varying(255) NOT NULL,
    "RequiredApprovals" integer NOT NULL,
    "EscalationTimeoutSeconds" integer NULL,
    "EscalationConditions" jsonb NOT NULL,
    CONSTRAINT "PK_CommandStages" PRIMARY KEY ("Id"),
    CONSTRAINT "CK_CommandStages_Positive" CHECK ("Order" >= 1 AND "RequiredApprovals" >= 1 AND ("EscalationTimeoutSeconds" IS NULL OR "EscalationTimeoutSeconds" > 0)),
    CONSTRAINT "FK_CommandStages_CommandChains_CommandChainId" FOREIGN KEY ("CommandChainId") REFERENCES "CommandChains" ("Id") ON DELETE CASCADE
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_CommandStages_CommandChainId_Order" ON "CommandStages" ("CommandChainId", "Order");

CREATE TABLE IF NOT EXISTS "CommandStageMembers" (
    "Id" uuid NOT NULL,
    "CommandStageId" uuid NOT NULL,
    "Kind" integer NOT NULL,
    "UserId" uuid NULL,
    "GroupId" uuid NULL,
    "RoleId" uuid NULL,
    "ServiceAccountId" uuid NULL,
    "ExternalAuthorityRef" character varying(500) NULL,
    CONSTRAINT "PK_CommandStageMembers" PRIMARY KEY ("Id"),
    CONSTRAINT "CK_CommandStageMembers_OneReference" CHECK ((CASE WHEN "UserId" IS NOT NULL THEN 1 ELSE 0 END + CASE WHEN "GroupId" IS NOT NULL THEN 1 ELSE 0 END + CASE WHEN "RoleId" IS NOT NULL THEN 1 ELSE 0 END + CASE WHEN "ServiceAccountId" IS NOT NULL THEN 1 ELSE 0 END + CASE WHEN "ExternalAuthorityRef" IS NOT NULL THEN 1 ELSE 0 END) = 1),
    CONSTRAINT "FK_CommandStageMembers_CommandStages_CommandStageId" FOREIGN KEY ("CommandStageId") REFERENCES "CommandStages" ("Id") ON DELETE CASCADE,
    CONSTRAINT "FK_CommandStageMembers_Users_UserId" FOREIGN KEY ("UserId") REFERENCES "Users" ("Id") ON DELETE CASCADE,
    CONSTRAINT "FK_CommandStageMembers_Groups_GroupId" FOREIGN KEY ("GroupId") REFERENCES "Groups" ("Id") ON DELETE CASCADE,
    CONSTRAINT "FK_CommandStageMembers_Roles_RoleId" FOREIGN KEY ("RoleId") REFERENCES "Roles" ("Id") ON DELETE CASCADE,
    CONSTRAINT "FK_CommandStageMembers_ServiceAccounts_ServiceAccountId" FOREIGN KEY ("ServiceAccountId") REFERENCES "ServiceAccounts" ("Id") ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS "IX_CommandStageMembers_CommandStageId" ON "CommandStageMembers" ("CommandStageId");
CREATE INDEX IF NOT EXISTS "IX_CommandStageMembers_UserId" ON "CommandStageMembers" ("UserId");
CREATE INDEX IF NOT EXISTS "IX_CommandStageMembers_GroupId" ON "CommandStageMembers" ("GroupId");
CREATE INDEX IF NOT EXISTS "IX_CommandStageMembers_RoleId" ON "CommandStageMembers" ("RoleId");
CREATE INDEX IF NOT EXISTS "IX_CommandStageMembers_ServiceAccountId" ON "CommandStageMembers" ("ServiceAccountId");

CREATE TABLE IF NOT EXISTS "AuthorityDelegations" (
    "Id" uuid NOT NULL,
    "TenantId" uuid NOT NULL,
    "AuthorityDomainId" uuid NOT NULL,
    "CommandStageId" uuid NULL,
    "DelegatorUserId" uuid NOT NULL,
    "DelegateUserId" uuid NOT NULL,
    "DelegationId" uuid NULL,
    "Reason" character varying(2000) NOT NULL,
    "EffectiveFrom" timestamp with time zone NOT NULL,
    "EffectiveUntil" timestamp with time zone NULL,
    "Status" integer NOT NULL,
    "RevokedAt" timestamp with time zone NULL,
    "RevokedByUserId" uuid NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_AuthorityDelegations" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_AuthorityDelegations_Tenants_TenantId" FOREIGN KEY ("TenantId") REFERENCES "Tenants" ("Id") ON DELETE CASCADE,
    CONSTRAINT "FK_AuthorityDelegations_AuthorityDomains_AuthorityDomainId" FOREIGN KEY ("AuthorityDomainId") REFERENCES "AuthorityDomains" ("Id") ON DELETE CASCADE,
    CONSTRAINT "FK_AuthorityDelegations_CommandStages_CommandStageId" FOREIGN KEY ("CommandStageId") REFERENCES "CommandStages" ("Id") ON DELETE SET NULL,
    CONSTRAINT "FK_AuthorityDelegations_Users_DelegatorUserId" FOREIGN KEY ("DelegatorUserId") REFERENCES "Users" ("Id") ON DELETE CASCADE,
    CONSTRAINT "FK_AuthorityDelegations_Users_DelegateUserId" FOREIGN KEY ("DelegateUserId") REFERENCES "Users" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_AuthorityDelegations_Delegations_DelegationId" FOREIGN KEY ("DelegationId") REFERENCES "Delegations" ("Id") ON DELETE SET NULL
);
CREATE INDEX IF NOT EXISTS "IX_AuthorityDelegations_TenantId_AuthorityDomainId_Status" ON "AuthorityDelegations" ("TenantId", "AuthorityDomainId", "Status");
CREATE INDEX IF NOT EXISTS "IX_AuthorityDelegations_Delegate_Effective" ON "AuthorityDelegations" ("DelegateUserId", "Status", "EffectiveFrom", "EffectiveUntil");
CREATE INDEX IF NOT EXISTS "IX_AuthorityDelegations_AuthorityDomainId" ON "AuthorityDelegations" ("AuthorityDomainId");
CREATE INDEX IF NOT EXISTS "IX_AuthorityDelegations_CommandStageId" ON "AuthorityDelegations" ("CommandStageId");
CREATE INDEX IF NOT EXISTS "IX_AuthorityDelegations_DelegationId" ON "AuthorityDelegations" ("DelegationId");
CREATE INDEX IF NOT EXISTS "IX_AuthorityDelegations_DelegatorUserId" ON "AuthorityDelegations" ("DelegatorUserId");

CREATE TABLE IF NOT EXISTS "AuthorityAvailabilityEvidence" (
    "Id" uuid NOT NULL,
    "TenantId" uuid NOT NULL,
    "CommandStageMemberId" uuid NULL,
    "UserId" uuid NULL,
    "Source" character varying(100) NOT NULL,
    "EvidenceRef" character varying(1000) NOT NULL,
    "IsAvailable" boolean NOT NULL,
    "ObservedAt" timestamp with time zone NOT NULL,
    "ValidUntil" timestamp with time zone NULL,
    CONSTRAINT "PK_AuthorityAvailabilityEvidence" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_AuthorityAvailabilityEvidence_Tenants_TenantId" FOREIGN KEY ("TenantId") REFERENCES "Tenants" ("Id") ON DELETE CASCADE,
    CONSTRAINT "FK_AuthorityAvailabilityEvidence_StageMembers_MemberId" FOREIGN KEY ("CommandStageMemberId") REFERENCES "CommandStageMembers" ("Id") ON DELETE SET NULL,
    CONSTRAINT "FK_AuthorityAvailabilityEvidence_Users_UserId" FOREIGN KEY ("UserId") REFERENCES "Users" ("Id") ON DELETE SET NULL
);
CREATE INDEX IF NOT EXISTS "IX_AuthorityAvailabilityEvidence_TenantId_UserId_ObservedAt" ON "AuthorityAvailabilityEvidence" ("TenantId", "UserId", "ObservedAt");
CREATE INDEX IF NOT EXISTS "IX_AuthorityAvailabilityEvidence_UserId" ON "AuthorityAvailabilityEvidence" ("UserId");
CREATE INDEX IF NOT EXISTS "IX_AuthorityAvailabilityEvidence_CommandStageMemberId" ON "AuthorityAvailabilityEvidence" ("CommandStageMemberId");

CREATE TABLE IF NOT EXISTS "AuthorityRecordVersions" (
    "Id" uuid NOT NULL,
    "TenantId" uuid NOT NULL,
    "RecordType" character varying(50) NOT NULL,
    "RecordId" uuid NOT NULL,
    "Version" integer NOT NULL,
    "Snapshot" jsonb NOT NULL,
    "ChangedAt" timestamp with time zone NOT NULL,
    "ChangedByUserId" uuid NULL,
    "ChangeReason" character varying(2000) NULL,
    CONSTRAINT "PK_AuthorityRecordVersions" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_AuthorityRecordVersions_Tenants_TenantId" FOREIGN KEY ("TenantId") REFERENCES "Tenants" ("Id") ON DELETE CASCADE
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_AuthorityRecordVersions_RecordType_RecordId_Version" ON "AuthorityRecordVersions" ("RecordType", "RecordId", "Version");
CREATE INDEX IF NOT EXISTS "IX_AuthorityRecordVersions_TenantId_RecordType_ChangedAt" ON "AuthorityRecordVersions" ("TenantId", "RecordType", "ChangedAt");

COMMIT;
