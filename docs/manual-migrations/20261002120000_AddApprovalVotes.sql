-- Task 5000 / IAM task 4711 (PR #160): one approval vote per (approval step, approver). Purely additive and idempotent.
-- Mirrors src/IAM.Infrastructure/Data/Migrations/20261002120000_AddApprovalVotes.cs. IAM only runs EnsureCreated, which
-- is a no-op on an existing database, so a build that contains #160 fails every access-request approval with
-- 'relation "ApprovalVotes" does not exist' until this has been applied to iam_db. Apply it BEFORE swapping the build.
-- ApprovalSteps.ApprovalsReceived is untouched: in-flight steps keep their counter, new approvals also write a vote row.
-- Requires the Users and ApprovalSteps tables (created by the original EnsureCreated schema).
BEGIN;

CREATE TABLE IF NOT EXISTS "ApprovalVotes" (
    "Id" uuid NOT NULL,
    "ApprovalStepId" uuid NOT NULL,
    "UserId" uuid NOT NULL,
    "Comment" character varying(2000) NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_ApprovalVotes" PRIMARY KEY ("Id")
);

-- The unique pair is what makes a quorum count distinct people, even under concurrent requests.
CREATE UNIQUE INDEX IF NOT EXISTS "IX_ApprovalVotes_ApprovalStepId_UserId" ON "ApprovalVotes" ("ApprovalStepId", "UserId");
CREATE INDEX IF NOT EXISTS "IX_ApprovalVotes_UserId" ON "ApprovalVotes" ("UserId");

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_ApprovalVotes_ApprovalSteps_ApprovalStepId'
                   AND conrelid = '"ApprovalVotes"'::regclass) THEN
        ALTER TABLE "ApprovalVotes" ADD CONSTRAINT "FK_ApprovalVotes_ApprovalSteps_ApprovalStepId"
            FOREIGN KEY ("ApprovalStepId") REFERENCES "ApprovalSteps" ("Id") ON DELETE CASCADE;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_ApprovalVotes_Users_UserId'
                   AND conrelid = '"ApprovalVotes"'::regclass) THEN
        ALTER TABLE "ApprovalVotes" ADD CONSTRAINT "FK_ApprovalVotes_Users_UserId"
            FOREIGN KEY ("UserId") REFERENCES "Users" ("Id") ON DELETE CASCADE;
    END IF;
END
$$;

COMMIT;
