# Manual migrations

IAM boots with `EnsureCreatedAsync` (see `DatabaseSeeder`) and **never calls `Migrate()`**. Schema changes reach the
live `iam_db` through the rerunnable SQL scripts in this folder, not through EF migrations.

Rules:

1. **Never run `dotnet ef database update` against the live `iam_db`.** EnsureCreated and the migration chain produce
   slightly different schemas (for example text vs jsonb, see the 20260926 migration and its SQL twin), so EF
   migrations are not a safe way to change live.
2. **Every new migration class must ship a rerunnable SQL script here**, named after the MigrationId. The script must
   end (inside its transaction) with
   `INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion") VALUES ('<its MigrationId>', '10.0.10') ON CONFLICT ("MigrationId") DO NOTHING;`
   so the history table keeps matching the applied schema.

`20261104120000_BackfillEFMigrationsHistory.sql` recorded the 18 migrations that predate this rule (task 7118). Take a
`pg_dump -t "__EFMigrationsHistory" --data-only` of the table before applying any script that touches it.
