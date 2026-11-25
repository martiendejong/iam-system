-- Task 5629: PAM deny-by-default. Purely additive and idempotent; both columns are nullable.
BEGIN;

ALTER TABLE "PamPolicies" ADD COLUMN IF NOT EXISTS "EligibleRoleId" uuid NULL;
ALTER TABLE "PrivilegedSessions" ADD COLUMN IF NOT EXISTS "BreakGlassConfirmations" jsonb NULL;

COMMIT;
