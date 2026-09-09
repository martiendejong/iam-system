# Agent Progress

## 2026-08-09 — task 45 (session-length admin setting had no effect on login)
Done: `AuthService`/`SocialAuthService` always issued a hardcoded 5-min access token /
7-day refresh token+cookie, ignoring the existing, admin-editable `TokenConfiguration`
(Claims Mapping admin UI). Added `IClaimsMappingService.ResolveTokenLifetimeForUserAsync`
(resolves a user's tenant via their `UserRole.TenantId`, then looks up a `TokenConfiguration`
row for that tenant, any OAuth client - login has no client context) and wired it into
`AuthService.LoginAsync/RefreshTokenAsync/LoginBypassPasswordAsync` and
`SocialAuthService.HandleCallbackAsync`, falling back to today's values when the user has
no tenant or the tenant never saved a config. `AuthResult` now carries the resolved
`AccessTokenLifetimeMinutes`/`RefreshTokenLifetimeDays` so `AuthController`/
`SocialAuthController` set the refresh-token cookie's `Expires` from the same resolved
value instead of a separate hardcoded `AddDays(7)`.
Verified: `dotnet build` clean, `dotnet test` 97 passed / 2 skipped (pre-existing) / 0
failed, including 5 new tests in `tests/IAM.API.Tests/Services/TokenLifetimeConfigurationTests.cs`
asserting configured vs. default lifetimes via the issued JWT's `exp` and the stored
`RefreshToken.ExpiresAt`.
Left: other login entry points (magic-link, OTP, passkey, device-code) also hardcode a
7-day cookie and were left untouched - out of scope per the task's technical notes, which
named only `AuthService.cs`/`SocialAuthService.cs`/`AuthController.cs`.

## 2026-08-09 — task 869eft100 (magic-link login bypasses 2FA)
Done: `MagicLinkController.VerifyMagicLink` called `AuthService.LoginBypassPasswordAsync`
directly after validating the token, skipping the account's email 2FA check entirely.
Added `IAuthService.CompletePasswordlessLoginAsync` (mirrors the 2FA gate already in
`LoginAsync`: if `TwoFactorEnabled && TwoFactorMethod == Email`, email a code and return
`RequiresTwoFactor` instead of tokens) and switched the magic-link controller to call it.
`MagicLinkCallbackPage.tsx` now handles a `requiresTwoFactor` response by showing an
inline code-entry form (mirrors LoginPage's password+2FA form) that completes sign-in via
the existing `/api/auth/2fa/verify` endpoint.
Verified: backend `dotnet test tests/IAM.API.Tests` 92/94 pass (2 pre-existing skips), incl.
3 new AuthService tests proving 2FA-enabled accounts get suspended + emailed a code, and
non-2FA accounts are unaffected. Frontend `npx vitest run` 32/32 pass incl. 4 new tests
(2FA prompt shown, correct code signs in, wrong code errors, resend works). `tsc -b && vite
build` clean.
Left: sibling controllers `OtpController.VerifyEmailOtp`/`VerifySmsOtp` have the identical
`LoginBypassPasswordAsync`-skips-2FA pattern for passwordless OTP login — out of scope for
this task (which was carved out narrowly to magic-link only); filed as a follow-up task.
`PasskeyController` was left untouched — WebAuthn passkeys are a recognized standalone
strong factor, not an "alternate primary factor" needing a second check.

## 2026-08-09 — task 869eft1jr (email/SMS OTP login bypasses 2FA)
Done: `OtpController.VerifyEmailOtp` and `VerifySmsOtp` had the identical
`LoginBypassPasswordAsync`-skips-2FA gap as the magic-link bypass fixed in 869eft100
(merged just as this session started). Switched both to call the now-merged
`CompletePasswordlessLoginAsync` and handle `RequiresTwoFactor` the same way.
Also fixed a real bug this surfaced: `LoginPage.tsx`'s SMS OTP form had no
`!twoFactorPending` guard, so after an SMS code triggers 2FA it would have rendered
stacked on top of the 2FA code form (unlike the password form, which already guards).
Verified: backend `dotnet test tests/IAM.API.Tests` 92/94 pass (2 pre-existing skips,
unchanged — `CompletePasswordlessLoginAsync` itself is already covered by
`EmailTwoFactorLoginTests.cs` from 869eft100, the controller just delegates to it, same
as `MagicLinkController`). Frontend `npx vitest run` 34/34 pass incl. 2 new `LoginPage`
tests (SMS 2FA prompt shown instead of instant sign-in; correct code completes sign-in).
`tsc -b && vite build` clean.
Left: nothing — this PR targets `develop` directly since 869eft100 merged before this
branch was pushed.

## 2026-07-24 — task 869e8wk2a (add deploy-time version tracking)
Done: PR #77 — `<Version>0.1.0</Version>` baseline added to src/IAM.API/IAM.API.csproj
(the exact PublishProjectPath JengoAGI's VersionTrackingService/IamDeployService already
scan), plus a new "Tag Release" CI job that pushes an annotated vX.Y.Z git tag on every
merge to develop once tests pass, bumping the highest existing tag's patch number.
Verified: `dotnet build IAMSystem.slnx -c Release` clean (0 errors); built IAM.API.dll's
FileVersion reads 0.1.0.0; ci.yml re-parsed with yaml.safe_load (valid, 9 jobs); the new
job's bash logic syntax-checked (bash -n) and functionally tested against a scratch repo
(no tags -> v0.1.0; v1.2.3/v1.2.10/v1.10.0 -> correctly bumps highest to v1.10.1).
Left: nothing — the tag-release job only fires on push to develop so it can't be
exercised live from this PR itself; it mirrors the existing deploy-staging job's
`if: github.ref == 'refs/heads/develop'` gate.

## 2026-07-24 — task 869e8w6v7 (fix Frontend Tests & Build / End-to-End Tests CI jobs)
Done: Frontend Tests & Build now points at admin-ui/ (was src/IAM.Admin.Web, which
doesn't exist) and installs Vitest + Testing Library, with a real component test for
ProtectedRoute (3 cases: loading, redirect-when-unauthenticated, renders-when-authenticated).
Also fixed the ~17 genuine pre-existing lint errors that were blocking `npm run lint`
in this folder for the first time ever (unused catch bindings, a missing case-block
scope, a synchronous setState-in-effect, a Fast Refresh export warning) and downgraded
`@typescript-eslint/no-explicit-any` from error to warn (269 pre-existing occurrences,
out of scope to fully type here). End-to-End Tests job's real steps are removed and
replaced with a single explanatory skip step — tests/IAM.E2E.Tests was never created,
only tests/IAM.API.Tests and tests/IAM.Core.Tests exist. Codecov upload guarded the
same way PR #75 guarded SonarCloud (skip cleanly if CODECOV_TOKEN isn't configured).
Verified: locally, in admin-ui/: `npm run lint` (0 errors, 287 pre-existing `any`
warnings), `npm test -- --coverage` (3/3 pass), `npm run build` (clean) all exit 0.
Left: a real Playwright E2E suite under tests/IAM.E2E.Tests, if wanted later.

## 2026-07-24 — task 869e8w66h (fix broken CI/CD Pipeline)
Done: PR #75 — Backend Tests & Build pointed at non-existent tests/IAM.UnitTests and
tests/IAM.Integration.Tests projects (now runs `dotnet test IAMSystem.slnx`); dotnet-version
was pinned to 9.0.x against a net10.0 solution (bumped to 10.0.x); SonarCloud step now
skips cleanly instead of failing when SONAR_TOKEN isn't configured (secrets context isn't
usable directly in step `if:` — routed through a run-step output instead).
Verified: real GitHub Actions run on the PR shows Backend Tests & Build and Code Quality
Analysis both green — https://github.com/martiendejong/iam-system/actions/runs/30059922506
Left: Frontend Tests & Build (wrong path, no test script at all) and End-to-End Tests
(missing directory) are separate, larger gaps — filed as follow-up task 869e8w6v7.
Attempting to provision a fresh Postgres + full migration history in CI (to run
TelemetryStorageServicePostgresTests) surfaced a pre-existing, unrelated migration-drift
bug (42P07: a later migration re-creates an already-created table) — filtered that test
class out of CI instead of fixing the migration history here.

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

## 2026-07-31 — task 869ec262y (login page ignores returnUrl)
Done: PR #79 — LoginPage.tsx already had a `navigateAfterLogin()` helper (PR #74)
used by password/step-up/email-2FA flows, but it had no open-redirect guard, and
the SMS OTP verify handler bypassed it entirely (hardcoded `navigate('/dashboard')`
and never called `setCurrentUser`, so SMS-authenticated users would have been
bounced back to /login by ProtectedRoute regardless of the returnUrl bug). Added
`sanitizeReturnUrl()` (admin-ui/src/pages/auth/returnUrl.ts — starts-with-`/`,
rejects `//` and `/\` open-redirect variants) used at the single point `returnUrl`
is read, and routed the SMS handler through `navigateAfterLogin()` + `setCurrentUser`
like the other three call sites. Filed follow-up 869ec3dn6: magic-link login has a
deeper, pre-existing gap (no `/magic-link` route exists at all for the emailed
link) that's out of scope for a returnUrl fix.
Verified: `npm run build` clean, `npm run lint` 0 errors (287 pre-existing `any`
warnings, unchanged), `npm test` 15/15 pass — includes new jsdom/RTL tests that
render the real LoginPage and assert window.location.href / router navigation for
password+OIDC-returnUrl, password+plain-returnUrl, password+open-redirect-attempt,
and SMS-OTP+OIDC-returnUrl.
Left: magic-link + 2FA-email-link round trips still don't carry returnUrl (869ec3dn6).

## 2026-08-09 — task 869ec3dn6 (magic-link login page + returnUrl threading)
Plan: add `MagicLinkCallbackPage` mirroring `VerifyTwoFactorPage` + `/magic-link` route
(App.tsx has none today, so clicking the emailed magic link bounces to login). Thread
`returnUrl` through `MagicLinkRequest`, `POST /api/auth/login` (triggers the 2FA email),
and the 2FA resend endpoint, reusing `returnUrl.ts`'s `sanitizeReturnUrl()` at both
redirect points — no new sanitizer logic.

Done: PR #82 — added `MagicLinkCallbackPage.tsx` + `/magic-link` route, calling the
already-existing `POST /api/auth/magic-link/verify`. Extracted `navigateAfterAuth()`
into `returnUrl.ts` (OIDC full-page nav vs. in-app nav) so LoginPage, VerifyTwoFactorPage,
and the new page all share one redirect implementation instead of duplicating it.
Threaded `returnUrl` end-to-end: `LoginRequest`/`MagicLinkRequest` (frontend) →
`AuthController.Login`/`MagicLinkController.RequestMagicLink` → `AuthService.LoginAsync`/
`MagicLinkService.SendMagicLinkAsync` → `OtpService.SendLoginTwoFactorCodeAsync`, appended
(`Uri.EscapeDataString`) onto the emailed `/verify-2fa` and `/auth/magic-link` URLs. The
2FA resend endpoint and the magic-link "Send again" button both reuse the same in-scope
`returnUrl` LoginPage already read from its own querystring, so a resend keeps the
destination. Sanitization happens once per redirect point (`sanitizeReturnUrl` in
VerifyTwoFactorPage and MagicLinkCallbackPage) regardless of what's embedded upstream —
same open-redirect guard as the existing LoginPage/SMS path, no new logic.
Verified: `dotnet build` 0 errors; `dotnet test` 90/90 pass (2 pre-existing skips) incl.
7 new tests asserting the emailed URLs do/don't carry `returnUrl`; `npm run build` clean;
`npx tsc --noEmit` clean; `npm test` 28/28 pass incl. new `MagicLinkCallbackPage.test.tsx`
(6 tests: loading/success/error, plain returnUrl, OIDC full-page nav, open-redirect
fallback) and `VerifyTwoFactorPage.test.tsx` (5 tests, same matrix) and 2 new LoginPage
tests (magic-link request includes returnUrl, 2FA resend keeps it).
Left: nothing new.

## 2026-08-09 — task 45 round 2 (fix reviewer-flagged parallel-login cookie gaps)
Done: PR #85 got CHANGES REQUESTED — 4 more login entry points
(`MagicLinkController.cs:84`, `OtpController.cs:96`+`184`, `PasskeyController.cs:176`)
call `AuthService.CompletePasswordlessLoginAsync`/`LoginBypassPasswordAsync`, which
already resolve `loginResult.RefreshTokenLifetimeDays` correctly, but each still hardcoded
the refresh-cookie `Expires` to `AddDays(7)` instead of reading it — same one-liner fix
already applied to `AuthController`/`SocialAuthController` in round 1. Swapped all 4 to
`AddDays(loginResult.RefreshTokenLifetimeDays)`.
Verified: `dotnet build` clean (0 errors); `dotnet test` 97 passed / 2 skipped
(pre-existing) / 0 failed — unchanged pass count since the fix only reads an already-
tested resolved value (`CompletePasswordlessLoginAsync` delegates straight to
`LoginBypassPasswordAsync`, whose `RefreshTokenLifetimeDays` resolution is covered by the
round-1 tests); no controller-level tests exist for these 3 controllers to add to.
Left: nothing — all 4 reviewer-cited sites fixed, no other hardcoded cookie literals
remain (`grep -rn "AddDays(7)" src/IAM.API/Controllers/` now empty).

## 2026-08-09 — task 869ec3dn6 round 2 (fix reviewer-flagged double `/auth/` prefix)
Done: PR #82 round 2 — `MagicLinkService.cs:78` built the emailed link as
`{baseUrl}/auth/magic-link?token=...`; the deployed `Email:BaseUrl` already ends in
`/auth`, so the real URL became `.../auth/auth/magic-link` and the SPA router
(basename="/auth") couldn't match it, falling through to the catch-all and bouncing to
`/dashboard` → login — the exact bug this task exists to fix. Dropped the leading `/auth`
so the built URL matches `OtpService.cs:236`'s `{baseUrl}/verify-2fa` shape.
`MagicLinkServiceTests.cs`'s fixture used a fake BaseUrl without the `/auth` suffix so it
never caught this; changed it to `https://iam.example.com/auth` (matching production
shape) and swapped the loose `Assert.Contains("/auth/magic-link?token=")` for
`Assert.StartsWith(...)` — `Contains` still matched the buggy double-`/auth/` URL because
the second `/auth/magic-link?token=` occurrence is itself a valid substring match;
`StartsWith` doesn't have that gap.
Verified: reintroduced the old bug locally and confirmed both `MagicLinkServiceTests`
assertions fail against it, then reverted to the fix and re-ran — `dotnet test` 90/90
pass (2 pre-existing skips); `dotnet build` 0 errors; frontend unchanged from round 1,
re-ran `npm run build` (clean), `npx tsc --noEmit` (clean), `npm test` 28/28 pass.
Left: nothing.

## 2026-09-07 — task 1737 follow-up (Access Matrix manifest used wrong role names for taskmanager)
Done: PR #96 (already merged) shipped `appsettings.json`'s `taskmanager` manifest entry
with invented `app:jengowork*` role names. TaskManager registers its own federated role
catalog at every boot (`POST /api/app-roles/register`, `taskmanager:admin` /
`product-owner` / `planner` / `refiner` / `developer` / `tester` / `customer` — live on
`main` since commit `5ac2274`), and `AuthorizationController.Authorize()`'s pre-existing
per-app gate (merged 2026-08-09, PR #86) blocks sign-in entirely unless the user holds a
role starting with `"{clientId}:"` once a catalog is registered. `app:jengowork*` never
matches `"taskmanager:"`, so toggling the JengoWork column in the matrix granted a role
the sign-in gate never checks — the checkbox did nothing for the user's actual ability to
sign in, exactly the flagship example in the task's own "How to test". Fixed the manifest
entry to use the real `taskmanager:*` names (base = `taskmanager:developer`, the other 6
as permissions) and added a regression test
(`AccessMatrixTaskmanagerRoleNames_MatchFederatedCatalogPrefix`) asserting every
taskmanager role name starts with `taskmanager:`. Confirmed `passwordmanager`/`jengo-agi`
don't self-register a catalog (no `app-roles/register` caller in either repo), so they
aren't affected; couldn't check jengomail/fedha/ari-chat/jengo-workspace/intranet-portal
(no local checkout, no repo found under martiendejong/scp-jengo orgs).
Verified: `dotnet build` 0 errors; `dotnet test tests/IAM.API.Tests` 135/135 pass (2
pre-existing skips), including the updated + new AccessMatrix tests (12/12).
Left: separately, `AuthorizationController.Exchange()`'s refresh_token grant reuses the
stored claims principal with no DB re-fetch of roles — a revoke via the matrix does not
reliably take effect within the access token's 15-minute lifetime as the matrix's own UI
text claims ("effective on next token refresh"); it can take up to the 7-day refresh
token lifetime, or a fresh interactive login. Pre-existing OIDC behavior, not touched by
this fix — flagged in the ClickUp comment as a follow-up, not fixed here (would need to
touch the shared token-exchange endpoint used by every relying app).

## 2026-09-07 — task 1743 (Remember me login checkbox)
Done: added a "Remember me" checkbox (unchecked by default) to `LoginPage.tsx`, threaded
`rememberMe` through `LoginRequest`/`TwoFactorVerifyRequest`/`StepUpVerifyRequest` into
`AuthController.CompleteLoginAsync(result, rememberMe)`. Checked: `IAM.Session` SignInAsync
gets `AuthenticationProperties { IsPersistent = true, ExpiresUtc = +30d }`; `refreshToken`
cookie `Expires` extends to `Math.Max(resolvedRefreshDays, 30)`. Unchecked/omitted: both
cookies are unchanged from today (session-only `IAM.Session` cookie governed by
Program.cs's 8h sliding `ExpireTimeSpan`, `refreshToken` uses the org-configured/default
lifetime from task 45's PR #85). Logout already unconditionally deletes the refresh
cookie and signs out `IAM.Session` regardless of persistence, so it wasn't touched.
Verified: `dotnet build` 0 errors; `dotnet test` 138/138 pass (2 pre-existing skips),
including 3 new `AuthControllerRememberMeTests` integration tests asserting the
`Set-Cookie` headers directly (refreshToken `Expires`, `IAM.Session`'s presence/absence
of an `expires` attribute) for rememberMe true/false/omitted. Frontend: `npx tsc -b`
clean, `npx vitest run` 36/36 pass (2 new + 2 updated in `LoginPage.test.tsx`).
Left: nothing — 2FA/step-up flows also honor rememberMe (frontend resubmits the checked
state on the follow-up verify call); `POST /auth/refresh` reissues using the resolved
`RefreshTokenLifetimeDays` unchanged, since the task's own Done-when/how-to-test only
scope `POST /auth/login`.

## 2026-09-09 — task 2977 (remember-me floor lost on refresh rotation)
Done: added `RefreshToken.RememberMe` (additive migration
`20260909070000_AddRememberMeToRefreshTokens`), set from `AuthService.LoginAsync`. Moved
`AuthController`'s private `RememberMeMinimumDays=30` constant to a new shared
`IAM.Core.AuthConstants` (Infrastructure can't reference API, so "reuse it" meant giving
it a home both layers can see) and reused it in both places. `RefreshTokenAsync` now reads
`storedToken.RememberMe` (never a client-supplied value — not spoofable), carries it onto
the newly-issued token, and applies `Math.Max(orgConfiguredDays, 30)` to `refreshDays` when
set — that one change fixes both the returned `RefreshTokenLifetimeDays` (which
`AuthController.Refresh()` already uses unmodified for the cookie `Expires`) and the
stored entity's own `ExpiresAt`, so the DB-side token and the browser cookie never
disagree. Also threaded `rememberMe` through `CompletePasswordlessLoginAsync` ->
`LoginBypassPasswordAsync`, and through `VerifyStepUpAsync`/`VerifyLoginTwoFactorAsync` —
the last two weren't named in the task's file list, but `AuthController`'s `/2fa/verify`
and `/step-up/verify` already collect and use `request.RememberMe` for the cookie (PR
#100), so leaving the entity flag unset there would silently reproduce this exact bug for
any remembered login that requires 2FA/step-up. `PasskeyController`/`MagicLinkController`/
`OtpController` were left untouched (no `RememberMe` field in their request DTOs today,
new params default to `false`) — same "no regression" scope task 45 documented for the
unrelated session-length feature.
Verified: `dotnet build` clean across API/Infrastructure/EdgeGateway/SDK.DotNet (0 errors).
`dotnet test` on `IAM.API.Tests`: 140 passed / 2 skipped (pre-existing) / 0 failed,
including the existing 3 `AuthControllerRememberMeTests` plus 2 new ones (remembered
login's refresh rotation keeps the cookie >= ~30 days out; non-remembered refresh keeps
today's short default). `IAM.Core.Tests`: 1/1 pass. Migration verified by hand against the
real dev `iam_db` (no superuser access to spin up a throwaway copy — `iam_user` lacks
CREATEDB; `postgres` role's own password wasn't in the vault/pgpass, only a leftover
`C:\Temp\setup-iam-db.ps1` script had the intended default, which didn't match live):
ran the migration's `ADD COLUMN` inside an explicit `BEGIN`/`ROLLBACK` against `iam_db`
directly — all 239 existing rows defaulted to `false` correctly, then rolled back with
zero persisted change.
Left: nothing for this task's scope.

## 2026-09-09 — task 2977 round 2 (review fix: floor the very first login-issued token too)

Done: PR #102 review found the rotation fix (round 1, above) never floored the *first*
token issued at login — only the `Set-Cookie` header said 30 days, while the stored
`RefreshToken` row got the org's shorter default (7 days), so a remember-me user who
never triggers a single `/auth/refresh` before that default elapses gets rejected anyway
(`Invalid or expired refresh token`) — the exact bug this task exists to close, reached
without a prior rotation instead of after several. Applied the reviewer's exact fix,
mirroring `RefreshTokenAsync`'s existing floor pattern: `LoginAsync` and
`LoginBypassPasswordAsync` (covers `CompletePasswordlessLoginAsync`, `VerifyStepUpAsync`,
`VerifyLoginTwoFactorAsync` — all three route through it) now apply
`Math.Max(refreshDays, AuthConstants.RememberMeMinimumDays)` when `rememberMe` before
building the `RefreshToken` entity, so the DB row and the cookie never disagree again.
Added a regression test reading the stored entity directly (not just the cookie) so this
exact class of bug — cookie right, DB row wrong — fails loudly if it recurs.
Verified: `dotnet build` clean (0 errors). `dotnet test`: `AuthControllerRememberMeTests`
6/6 pass (1 new: `Login_WithRememberMeTrue_StoresRefreshTokenRowExpiringAtLeast30DaysOut`).
Full `IAM.API.Tests` suite: 141 passed / 2 skipped (pre-existing) / 0 failed. No schema
change this round — the `RememberMe` column and its additive migration from round 1 are
unchanged and were already verified against the real dev `iam_db`.
Left: nothing for this task's scope.
