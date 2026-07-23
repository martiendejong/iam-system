# Agent Progress

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
