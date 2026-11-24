-- Adds LoginApprovals (sign in on a new browser by approving on an already signed-in device) and WebPushSubscriptions
-- (push endpoints that get the notification). Purely additive and idempotent.
-- Mirrors src/IAM.Infrastructure/Data/Migrations/20261124120000_AddLoginApprovals.cs. IAM only runs EnsureCreated, a
-- no-op on an existing database, so apply this BEFORE swapping the build (the new endpoints query these tables).
BEGIN;

CREATE TABLE IF NOT EXISTS "LoginApprovals" (
    "Id" uuid NOT NULL PRIMARY KEY,
    "PollSecretHash" varchar(64) NOT NULL,
    "UserId" uuid NULL,
    "Status" integer NOT NULL,
    "MatchNumber" integer NOT NULL,
    "FailedMatches" integer NOT NULL,
    "RememberMe" boolean NOT NULL,
    "RequestIp" varchar(50) NULL,
    "RequestUserAgent" varchar(500) NULL,
    "RequestDevice" varchar(100) NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    "ExpiresAt" timestamp with time zone NOT NULL,
    "DecidedAt" timestamp with time zone NULL,
    "ConsumedAt" timestamp with time zone NULL,
    "ApprovedByUserId" uuid NULL,
    "ApprovedByRefreshTokenId" uuid NULL,
    "Version" integer NOT NULL
);
CREATE INDEX IF NOT EXISTS "IX_LoginApprovals_UserId_Status_ExpiresAt" ON "LoginApprovals" ("UserId", "Status", "ExpiresAt");
CREATE INDEX IF NOT EXISTS "IX_LoginApprovals_CreatedAt" ON "LoginApprovals" ("CreatedAt");

CREATE TABLE IF NOT EXISTS "WebPushSubscriptions" (
    "Id" uuid NOT NULL PRIMARY KEY,
    "UserId" uuid NOT NULL,
    "Endpoint" varchar(1000) NOT NULL,
    "P256dh" varchar(200) NOT NULL,
    "Auth" varchar(100) NOT NULL,
    "Label" varchar(100) NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    "LastSuccessAt" timestamp with time zone NULL,
    "FailureCount" integer NOT NULL
);
CREATE INDEX IF NOT EXISTS "IX_WebPushSubscriptions_UserId" ON "WebPushSubscriptions" ("UserId");
CREATE UNIQUE INDEX IF NOT EXISTS "IX_WebPushSubscriptions_Endpoint" ON "WebPushSubscriptions" ("Endpoint");

COMMIT;
