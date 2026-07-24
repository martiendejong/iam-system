# Agent Progress

## 2026-07-24 — task 869cmvq8k (review round 3 — close remaining privilege escalation)
Done: closed the "half-closed" gap from review round 2 on PR #72 — SendInvitation
(InvitationsController), ChangeMemberRole (TenantsController), and bulk CSV invite
now reject granting the SuperAdmin role unless the caller already holds SuperAdmin
(403 for the two synchronous endpoints, a per-row error for bulk CSV rows), mirroring
the existing SuperAdmin-only precedent in UsersController.AssignRole/RemoveRole.
Verified: merged origin/develop (2 commits: telemetry PR #70, Python SDK PR #68,
no conflicts), dotnet build clean (0 errors), 83/85 backend tests pass (2 pre-existing
skips), admin-ui tsc+vite build clean.
Left: nothing new. Frontend role `<select>` still doesn't filter out SuperAdmin for
non-SuperAdmin callers — reviewer explicitly flagged this as non-blocking since the
API is the real control, so left untouched to keep this fix narrowly scoped.

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

## 2026-07-24 — task 869cmvq8k
Done: PR #72 fix round 2 — added `[Authorize(Roles = "SuperAdmin,BuildingOwner,BuildingManager")]`
to the 3 member-management endpoints in TenantsController.cs Martien flagged (any logged-in
user could grant themselves SuperAdmin in any tenant). Also found and closed the identical
gap in InvitationsController.cs: SendInvitation/SendBulkInvitations let any authenticated user
invite themselves into any tenant with any role, and GetInvitations/GetPendingInvitations/
RevokeInvitation leaked/let anyone tamper with any tenant's invitation list.
Verified: dotnet build 0 errors, dotnet test 2/2 pass, admin-ui tsc+vite build clean.
Left: nothing.

## 2026-07-24 — task 869cmvq8k (review round 2)
Done: PR #72 had drifted 3 commits behind develop and gone CONFLICTING (2FA branding
PR #66 + Directory Sync PR #71 both landed since the last fix). Merged develop in,
resolved IEmailService/InvitationService conflicts (kept this branch's custom-email-
template path, wired develop's new tenantId branding param through the fallback call),
and regenerated the 6000-line ModelSnapshot.cs via `dotnet ef migrations add` (verified
zero drift with a follow-up empty-migration check) instead of hand-resolving ~30 conflict
hunks. Also fixed FakeEmailService in EmailTwoFactorLoginTests.cs (missing new interface
members). Pushed as commit 96f8bda — PR is MERGEABLE again, build clean, 75/77 tests pass.
Verified: dotnet build 0 errors, dotnet test 75 passed/2 skipped/0 failed, admin-ui tsc+vite build clean.
Left: found a NEW privilege-escalation gap while reviewing round 2's fix — neither
SendInvitation nor ChangeMemberRole validates the requested roleId against the caller's
own privilege, so a BuildingManager (whose own permissions don't even include User.Invite)
can invite or promote anyone straight to SuperAdmin. Sent back to CHANGES REQUESTED;
UsersController.AssignRole's existing SuperAdmin-only pattern is the fix to mirror.
