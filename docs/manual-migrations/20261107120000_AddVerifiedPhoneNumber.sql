-- Task 5492: SMS-OTP login only uses phone numbers the user proved to hold.
-- Additive: two nullable columns plus a partial unique index. Existing numbers stay in
-- "PhoneNumber" and count as UNVERIFIED (VerifiedPhoneNumber is NULL), so SMS login is off for
-- them until the owner confirms the number with a code in the portal.
-- Idempotent.
BEGIN;

ALTER TABLE "Users" ADD COLUMN IF NOT EXISTS "VerifiedPhoneNumber" varchar(50) NULL;
ALTER TABLE "Users" ADD COLUMN IF NOT EXISTS "PhoneVerifiedAt" timestamp with time zone NULL;

CREATE UNIQUE INDEX IF NOT EXISTS "IX_Users_VerifiedPhoneNumber"
    ON "Users" ("VerifiedPhoneNumber")
    WHERE "VerifiedPhoneNumber" IS NOT NULL;

COMMIT;

-- Rollback (reversible, drops only what this script added):
-- DROP INDEX IF EXISTS "IX_Users_VerifiedPhoneNumber";
-- ALTER TABLE "Users" DROP COLUMN IF EXISTS "PhoneVerifiedAt";
-- ALTER TABLE "Users" DROP COLUMN IF EXISTS "VerifiedPhoneNumber";
