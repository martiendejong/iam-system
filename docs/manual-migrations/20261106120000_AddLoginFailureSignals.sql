-- Task 5852: per-account failure signals for distributed password guessing. Purely additive and idempotent.
-- Mirrors src/IAM.Infrastructure/Data/Migrations/20261106120000_AddLoginFailureSignals.cs. IAM only runs EnsureCreated,
-- a no-op on an existing database, so a build containing task 5852 would fail EVERY failed sign-in with
-- 'relation "LoginFailureSignals" does not exist' (the guard swallows that error, but apply this BEFORE swapping the build).
BEGIN;

CREATE TABLE IF NOT EXISTS "LoginFailureSignals" (
    "AccountKey" character varying(64) NOT NULL,
    "SourceKey" character varying(64) NOT NULL,
    "LastFailureAt" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_LoginFailureSignals" PRIMARY KEY ("AccountKey", "SourceKey")
);
CREATE INDEX IF NOT EXISTS "IX_LoginFailureSignals_LastFailureAt" ON "LoginFailureSignals" ("LastFailureAt");
CREATE INDEX IF NOT EXISTS "IX_LoginFailureSignals_AccountKey_LastFailureAt" ON "LoginFailureSignals" ("AccountKey", "LastFailureAt");

CREATE TABLE IF NOT EXISTS "AccountGuessingAlertMarks" (
    "AccountKey" character varying(64) NOT NULL,
    "AlertedAt" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_AccountGuessingAlertMarks" PRIMARY KEY ("AccountKey")
);
CREATE INDEX IF NOT EXISTS "IX_AccountGuessingAlertMarks_AlertedAt" ON "AccountGuessingAlertMarks" ("AlertedAt");

COMMIT;
