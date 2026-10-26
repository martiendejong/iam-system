-- Task 5000 / IAM task 4316 (commit f0ebe44): per-provider redirect URI allow-list. Purely additive and idempotent.
-- Mirrors src/IAM.Infrastructure/Data/Migrations/20260926120000_AddAllowedRedirectUrisToIdentityProvider.cs, but the column
-- is created as text, not jsonb: the entity property is a plain string and IdentityProvider.AllowedRedirectUris has no
-- jsonb column-type mapping in IAMDbContext, so EF binds it as text and Npgsql cannot write text into a jsonb column.
-- text is also what EnsureCreated produces on a fresh database. Reads (the JSON array is parsed in SocialAuthService) work either way.
-- Live iam_db did not have this column although a005238 already maps it: any query that reads IdentityProviders failed
-- with 'column i.AllowedRedirectUris does not exist'. Apply it BEFORE (or together with) the next build swap.
BEGIN;

ALTER TABLE "IdentityProviders" ADD COLUMN IF NOT EXISTS "AllowedRedirectUris" text NULL;

COMMIT;
