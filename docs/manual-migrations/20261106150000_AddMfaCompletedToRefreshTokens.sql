-- Task 5569: record whether a session's sign-in completed a second factor. Purely additive and idempotent.
-- Existing sessions default to false; that only affects members of an organization with Require MFA on,
-- whose old sessions can no longer be refreshed (they sign in again and enter the e-mailed code).
ALTER TABLE "RefreshTokens" ADD COLUMN IF NOT EXISTS "MfaCompleted" boolean NOT NULL DEFAULT false;
