# Manual migrations

IAM creates its schema with `Database.EnsureCreated()` (see `DatabaseSeeder`), which does nothing on an existing database.
A new column or table therefore never reaches the live `iam_db` by itself: a build that maps it fails every query that touches it
(`column u.ManagerUserId does not exist`, `column i.AllowedRedirectUris does not exist`, `relation "ApprovalVotes" does not exist`).

Every change to the model that adds a column, table or index needs a script here, next to its EF migration. Scripts are:

- additive only (no drops, no type changes, no data rewrites), idempotent (`IF NOT EXISTS`, guarded constraints), one transaction;
- named like the EF migration they mirror (`<migration id>.sql`), and applied BEFORE the new build is started;
- safe to run again, and safe for the previous build (it ignores what it does not know), so a code rollback needs no database rollback.

| Script | Adds | Needed by |
|---|---|---|
| `20260926120000_AddAllowedRedirectUrisToIdentityProvider.sql` | `IdentityProviders.AllowedRedirectUris` (text) | social-login redirect allow-list (task 4316); the public login page lists providers through it |
| `20261002120000_AddApprovalVotes.sql` | `ApprovalVotes` table, unique `(ApprovalStepId, UserId)` index, two FKs | one approval per person per access-request step (task 4711) |
| `20261005120000_AddPrincipalKindAndManager.sql` | `ManagerUserId` + `PrincipalKind` on `Users` and `ServiceAccounts` | manager link and principal kind (task 4057 / PR #137) |

## Applying

```
PGOPTIONS="-c search_path=public" psql -h localhost -U iam_user -d iam_db -v ON_ERROR_STOP=1 -f <script>.sql
```

## Checking a build against the live schema before a deploy

1. Generate the create script for the new build's model (`ctx.Database.GenerateCreateScript()` from a throwaway console project that references `IAM.Infrastructure`), load it into an empty scratch schema of `iam_db`, and diff tables, columns, indexes and constraints against `public`. Anything the model has and live lacks needs a script above.
2. Clone live `public` (with data) into a scratch schema, apply the scripts there, run the new build against it with `Search Path=<scratch>` in the connection string on another port (email sending off), and exercise login, `/connect/authorize` and `/connect/token`.
3. Take a `pg_dump -Fc -n public` backup, restore it into another scratch schema and compare per-table row counts before swapping files.
4. Drop the scratch schemas afterwards: they hold a copy of live user data.

`Roles.Category` is `varchar(100)` on live and `text` in the model; this is old drift, harmless, and not worth a script.
