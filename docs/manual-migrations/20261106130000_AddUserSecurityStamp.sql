-- Task 5490: per-user security stamp for the IAM.Session single sign-on cookie. Purely additive, idempotent.
-- Apply to iam_db BEFORE starting a build that contains this change (EnsureCreated does not alter existing tables).
-- Existing rows get a random stamp; the default '' is only a placeholder so the NOT NULL column can be added.
BEGIN;

ALTER TABLE "Users" ADD COLUMN IF NOT EXISTS "SecurityStamp" character varying(64) NOT NULL DEFAULT '';
UPDATE "Users" SET "SecurityStamp" = md5(random()::text || clock_timestamp()::text || "Id"::text) WHERE "SecurityStamp" = '';

COMMIT;
