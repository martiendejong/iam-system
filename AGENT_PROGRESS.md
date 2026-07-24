# Agent Progress

## 2026-07-24 — task 869cmtykg (review fixes)
Done: addressed Martien's CHANGES REQUESTED review on PR #70 — pinned
IAM.API.Tests to explicit EF Core 10.0.10 refs (test project didn't compile
before), gave AggregateAsync's deviceId/tenantId parameters an explicit DbType
so Postgres can plan the query when they're null (was 42P08 on every
dashboard chart, since the frontend never sends a tenantId), and moved the
retention-cleanup test to the Postgres-backed class since ExecuteDeleteAsync
has no EF InMemory translation — that test had never actually run before.
Verified: dotnet build clean (API + tests), 82/82 IAM.API.Tests pass (was 0
running due to the compile break), 1/1 IAM.Core.Tests, admin-ui build clean.
Merged origin/develop into the branch (4 commits behind) before pushing.
Left: nothing new; same TimescaleDB/InfluxDB and frontend-SignalR gaps
already flagged in the original PR #70 description remain out of scope.

## 2026-07-23 — task 869e7ngck
Done: PR #64 (merged) added coach-app + jengo-agi + jengo-meeting + jengo-workspace to
DatabaseSeeder.cs — all 4 existed live in iam_db via manual SQL only, never in source.
Also fixed coach-app's live DB row: its manual insert used invalid OpenIddict permission
strings (`oi:grant_type:...` instead of `gt:...`/`ept:...`), which made /connect/authorize
return 400 unauthorized_client — SSO was actually broken, not just fragile. Deployed the
new build to the dev server (212.227.6.226) and restarted IAMSystem.
Verified: `SELECT count(*) FROM "OpenIddictApplications"` = 11 post-restart, no duplicates.
/connect/authorize for coach-app now returns 302 -> /auth/login (was 400 before the fix).
Left: nothing. Old binaries backed up at C:\Services\IAM_backup_869e7ngck on the dev server.
