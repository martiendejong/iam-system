-- Task 4992 / IAM #137 (task 4057): manager link + principal kind. Purely additive and idempotent.
-- Users default to Human (0), ServiceAccounts to Service (2), matching the entity initializers.
BEGIN;

ALTER TABLE "Users" ADD COLUMN IF NOT EXISTS "ManagerUserId" uuid NULL;
ALTER TABLE "Users" ADD COLUMN IF NOT EXISTS "PrincipalKind" integer NOT NULL DEFAULT 0;
ALTER TABLE "ServiceAccounts" ADD COLUMN IF NOT EXISTS "ManagerUserId" uuid NULL;
ALTER TABLE "ServiceAccounts" ADD COLUMN IF NOT EXISTS "PrincipalKind" integer NOT NULL DEFAULT 2;

CREATE INDEX IF NOT EXISTS "IX_Users_ManagerUserId" ON "Users" ("ManagerUserId");
CREATE INDEX IF NOT EXISTS "IX_ServiceAccounts_ManagerUserId" ON "ServiceAccounts" ("ManagerUserId");

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_Users_Users_ManagerUserId'
                   AND conrelid = '"Users"'::regclass) THEN
        ALTER TABLE "Users" ADD CONSTRAINT "FK_Users_Users_ManagerUserId"
            FOREIGN KEY ("ManagerUserId") REFERENCES "Users" ("Id") ON DELETE SET NULL;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_ServiceAccounts_Users_ManagerUserId'
                   AND conrelid = '"ServiceAccounts"'::regclass) THEN
        ALTER TABLE "ServiceAccounts" ADD CONSTRAINT "FK_ServiceAccounts_Users_ManagerUserId"
            FOREIGN KEY ("ManagerUserId") REFERENCES "Users" ("Id") ON DELETE SET NULL;
    END IF;
END
$$;

COMMIT;
