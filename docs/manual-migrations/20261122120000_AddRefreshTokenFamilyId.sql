-- Adds RefreshTokens.FamilyId (session family of a refresh token). Purely additive and idempotent.
-- Mirrors src/IAM.Infrastructure/Data/Migrations/20261122120000_AddRefreshTokenFamilyId.cs. IAM only runs
-- EnsureCreated, a no-op on an existing database, so a build containing this change would fail EVERY sign-in
-- ('column "FamilyId" of relation "RefreshTokens" does not exist'). Apply this BEFORE swapping the build.
BEGIN;

ALTER TABLE "RefreshTokens" ADD COLUMN IF NOT EXISTS "FamilyId" uuid NULL;
UPDATE "RefreshTokens" SET "FamilyId" = "Id" WHERE "FamilyId" IS NULL;

COMMIT;
