-- Task 5491: TOTP replay guard. Purely additive and idempotent.
-- IAM creates its schema with EnsureCreated (a no-op on an existing database), so apply this to iam_db
-- BEFORE starting a build that contains task 5491; otherwise every Users query fails with
-- "column u.TwoFactorLastUsedStep does not exist".
-- Deploy order: (1) apply this SQL, (2) deploy the build (reads plain-text AND encrypted secrets),
-- (3) set Totp:EncryptLegacySecrets=true and restart once to encrypt the existing secrets
--     (idempotent; set it back to false afterwards), (4) verify with:
--     SELECT count(*) FROM "Users" WHERE "TwoFactorSecret" IS NOT NULL AND "TwoFactorSecret" <> '' AND "TwoFactorSecret" NOT LIKE 'enc:%';
BEGIN;

ALTER TABLE "Users" ADD COLUMN IF NOT EXISTS "TwoFactorLastUsedStep" bigint NULL;

COMMIT;
