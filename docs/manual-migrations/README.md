# Manual migrations

IAM creates its schema with `EnsureCreatedAsync` (see `DatabaseSeeder`) and **never calls `Migrate()`**. Schema changes
therefore reach live `iam_db` as rerunnable SQL scripts in this folder.

Rules:

1. **Never run `dotnet ef database update` against the live `iam_db`.** `EnsureCreated` and the migration chain produce
   slightly different schemas (for example text vs jsonb, see the `20260926` migration and its SQL twin), so even with a
   complete history table the command is not safe.
2. **Every new migration class must ship a rerunnable SQL script here**, and that script must end with
   `INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion") VALUES (...) ON CONFLICT ("MigrationId") DO NOTHING;`
   for its own MigrationId (inside the same `BEGIN`/`COMMIT`), so `__EFMigrationsHistory` does not drift from the schema again.

`20261104120000_BackfillEFMigrationsHistory.sql` (task 7118) recorded the 18 migrations that were missing. Apply scripts with
`psql -U iam_user -d iam_db -v ON_ERROR_STOP=1 -f <script>`.
