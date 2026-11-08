-- Task 5578: Invitations.Token now holds SHA-256 lowercase hex of the e-mailed token (64 chars),
-- same scheme as MagicLinkTokens. Hashes existing raw tokens in place. No rows deleted.
-- Idempotent: a value that already is 64 lowercase hex chars is a hash and is left alone
-- (raw tokens are 43-char base64url, so the two cannot be confused).
-- Pending invitations keep working: the e-mailed raw token still hashes to the stored value.
BEGIN;

UPDATE "Invitations"
SET "Token" = encode(sha256(convert_to("Token", 'UTF8')), 'hex')
WHERE "Token" !~ '^[0-9a-f]{64}$';

COMMIT;
