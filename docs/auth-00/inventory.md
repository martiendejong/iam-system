# AUTH-00 inventory: what exists today

Task 5097, 2026-10-08. Everything below was read from code or from the live host, not assumed. "Verified" means
a command or test in this task proved it; "read" means taken from source; "not verified" is said explicitly.
Repos inspected: `martiendejong/iam-system` (develop @ 74f8578), `martiendejong/passwordmanager` (develop @
dabebbc, deployed by the build agent on 2026-09-25), `scp-jengo/jengo-web` (develop), `scp-jengo/lango` (develop).

## 1. IAM (`iam-system`)

| # | Finding | Evidence |
|---|---------|----------|
| I1 | **Two token systems run side by side.** (a) The custom login in `AuthService`: HS256 JWT access token (5 min default via `Jwt:AccessTokenExpirationMinutes`, per-organisation `TokenConfiguration` can override), opaque refresh token (32 random bytes, SHA-256 hash stored), rotation on every refresh. (b) OpenIddict for SSO to other apps: code + PKCE, RS256 access token 15 min, refresh token 7 days, encryption cert persisted since task 3314. The `IAM.Session` cookie (`HttpOnly`, `Secure`, `SameSite=Strict`, 8 h sliding, or 30 days persistent with "remember me") is what lets `/connect/authorize` recognise a browser. | `AuthService.cs:53-60,278-404`, `Program.cs:286-290,337-343`, `AuthController.cs:129-170` |
| I2 | **"Remember me" is a fixed 30-day floor**, not a choice of duration. No 90-day, no "until revoked", no per-browser setting. The flag travels on `RefreshToken.RememberMe` through rotation. | `AuthConstants.cs`, `RefreshToken.cs:37`, task 1743/2977 |
| I3 | **There is no browser registration.** A refresh token knows only IP and user agent. No browser id, no token family id, no first/last-seen record per browser, no name. | `RefreshToken.cs` |
| I4 | **`TrustedDevice` is not a trust credential.** It is a risk-score input keyed on a *client-supplied* fingerprint hash, used by `RiskAssessmentService` to lower a login risk score. It must not be reused as "trusted browser": the client picks the value. | `TrustedDevice.cs`, `RiskAssessmentService.cs:72,182,336` |
| I5 | **`UserSession` / `SessionsController` look dead.** The only construction site is `SessionService.CreateSessionAsync`, which no login path calls, so the "active sessions" list cannot reflect real logins. The controller matches the "current session" by the `refresh_token_id` claim against `UserSession.Id`, which would never match. (Verified by grep: no callers.) | `SessionService.cs:23`, `SessionsController.cs:179-195` |
| I6 | **Revocation does not reach live access tokens or the SSO cookie.** The custom access token is validated stateless (`JwtBearer`, no `OnTokenValidated` revocation check). It carries a `refresh_token_id` claim that nothing checks. The `IAM.Session` cookie is a self-contained ticket with no server-side store, so revoking refresh tokens does not end it. Net effect: after "revoke", access stays possible for up to the access-token lifetime (5 min default, longer if an organisation configures it) and for the cookie lifetime on `/connect/authorize` (8 h, or 30 days with remember-me). The plan's "revocation blocks protected requests within 60 seconds" is therefore not met today. | `Program.cs:317-343`, `AuthService.cs:695-715` |
| I7 | **Replay detection is per user, not per family.** A replayed (already-rotated) refresh token revokes *all* of that user's refresh tokens. Good as a panic button; wrong for trusted browsers, where one stale browser would sign out every other one. Needs a token family id (plan: "intrekbare tokenfamilies"). | `AuthService.cs:297-325`, `RefreshTokenRevocation.cs` |
| I8 | **IAM's own WebAuthn (`PasskeyService`, Fido2 4.0.1) is login-grade, not action-grade.** Challenges live in two `static Dictionary` fields (one slot per user/email: a second `Begin` overwrites the first; no TTL; not multi-instance; lost on restart; not atomically consumed). User verification is `Preferred`, not `Required`. `IsBackupEligible`/`IsBackedUp` are hard-coded `false` ("not available in v4"). `CompleteAuthenticationAsync` loads every credential in the table into memory to find one. Passkey login now passes the 2FA gate (task 4573). | `PasskeyService.cs:19-20,56-60,79,174-286` |
| I9 | **Live IAM has no `Fido2` configuration.** `C:\Services\IAM\appsettings.Production.json` has no `Fido2` section and the service has no environment override, so `ServerDomain` falls back to `localhost` and the allowed origin to `https://localhost:5161`. A passkey ceremony against `maendeleo.martiendejong.nl` would fail origin/RP-ID checks. Read from the host; *not verified by running a live ceremony*. | `Program.cs:155-163`, host config |
| I10 | **IAM's TOTP is for IAM login only**, and `Users.TwoFactorSecret` is stored as plaintext. It is not a store for third-party seeds and must not become one. SHA-256 default since task 3162 (pending migration PR #168); enforcement at login still waits on PR #142 for TOTP users. | `User.cs:26`, `TotpService.cs`, `MfaController.cs` |
| I11 | **`PrincipalKind` (Human / Agent / Service) exists** on users and service accounts (task 4057/4992), so "humans only" can be a claim instead of a naming convention. It is not yet in the tokens other apps receive. | `PrincipalKind.cs`, commit 472902f |
| I12 | **Versions.** Fido2 / Fido2.AspNet 4.0.1 in IAM and Password Manager. NuGet has 4.1.1 and 4.2.0 (stable) and 5.0.0 previews only. Dependabot PRs #169/#170 propose 4.2.0 and are open. The library does expose `AuthenticatorData.IsBackupEligible`, but not from the registration result, so backup-eligible / backed-up flags have to be read from the raw authenticator-data flags byte (bits `0x08` and `0x10`). | NuGet index, `Fido2.dll` reflection |
| I13 | **Only password/2FA login creates the SSO cookie.** `HttpContext.SignInAsync("IAM.Session", …)` is called from exactly one place, `AuthController.CompleteLoginAsync`. Magic link, SMS OTP, passkey and social login return tokens but never set the cookie, so a browser registered through a passkey login could not complete an `/connect/authorize` round trip. Combined with `SameSite=Strict`, cross-site apps (the vault is `prospergenics.com`, IAM is `martiendejong.nl`) never present the cookie at all: silent single sign-on into the vault does not work today. Task 5053 (refined, board 44) covers `prompt=none` + `SameSite=Lax`; AUTH-01 builds on it instead of repeating it. | `AuthController.cs:166`, `Program.cs:337-343`, task 5053 refinement (verified in Edge 2026-10-07) |

## 2. Password Manager (`passwordmanager`)

| # | Finding | Evidence |
|---|---------|----------|
| P1 | **Own session layer on top of IAM SSO.** IAM login (code + PKCE, `IamAuthController`) ends in a PM-minted HS256 JWT valid **7 days**, with a `jti` revocation list (`RevokedTokens`). API keys are a separate scheme (`X-API-Key`). Controllers that must be human-only select `JwtBearerDefaults.AuthenticationScheme`. No refresh token, no rotation. | `JwtTokenService.cs`, `Program.cs:45-81` |
| P2 | **A proven WebAuthn action gate already exists (task 914).** `WebAuthnService`: RP ID `vault.prospergenics.com`, `UserVerification = Required` at registration and assertion, DB-persisted challenges (`WebAuthnChallenges`, 2 min TTL), a `decide` challenge bound to (requestId, verdict), API-key principals hard-rejected, secret-links disabled once a passkey exists. This is the pattern AUTH-02 generalises. | `WebAuthnService.cs`, `AccessApprovalsController.cs` |
| P3 | **Two gaps in that gate.** (a) Consuming a challenge is read-then-save (`MarkUsedAsync`), not a conditional update, so two concurrent completions can both pass the `Used == false` check. (b) Passkey *enrolment* needs only a valid JWT of an admin/approver: no assertion from an existing passkey and no recent-login check, so a stolen session can enrol the attacker's own key. (c) `DELETE /api/webauthn/credentials/{id}` also needs only the JWT, and removing the last admin passkey switches the task-914 "passkey required" mode off again. (Lango has the enrolment finding open as task 4619.) Any AUTH-02 reuse of this credential table inherits (b) and (c) until they are closed. | `WebAuthnService.cs` `TakeChallengeAsync`/`MarkUsedAsync`, `WebAuthnController.cs:58-116` |
| P4 | **`EncryptionService` must not hold TOTP seeds.** AES-CBC without authentication, key = `SHA256(passphrase)` from `ENCRYPTION_KEY`/`appsettings`, a legacy fixed MD5-derived IV path, new-vs-legacy format guessed by trial decryption, no associated data (a ciphertext can be moved between rows/tenants unnoticed), no key version. | `EncryptionService.cs` |
| P5 | **Storage and deploy.** SQLite, schema "self-heal" `ALTER TABLE` blocks at startup in `Program.cs`. Deployed by the build agent (`C:\buildagents\passwordmanager`) to a remote IIS host over SFTP (`vault.prospergenics.com`); the host's key-storage capabilities (DPAPI, ACLs, backup scope) could not be inspected from here. Default branch is `master`, PRs go to `develop` (37 commits ahead). The old base clone (`E:\projects\passwordmanager`) no longer exists and the `C:\projects\passwordmanager-*` worktrees are orphaned; use a fresh clone. | `build-status.json`, `DEPLOYMENT_GUIDE.md`, `git worktree list` |
| P6 | **Test baseline on this host:** 298 pass / 16 fail on untouched develop. The 16 are `TenantIsolationIntegrationTests` and `ApiKeySystemIntegrationTests` (known `WebApplicationFactory` host gap). New security tests must be controller/service level; HTTP-level proof needs a real browser or deployed instance. | `dotnet test`, build-agent baseline exclusions |

## 3. Jengo web (`jengo-web`)

| # | Finding | Evidence |
|---|---------|----------|
| J1 | **Jengo web does not use IAM sessions after login.** After IAM code + PKCE it mints its own HS256 JWT valid **30 days**, redirects to `…/?token=<jwt>` (token in the URL, so in history and possibly referrers), and the frontend keeps it in `localStorage`. XSS can read it; IAM revocation does not touch it. `JWT_SECRET` falls back to the literal `jengo-dev-secret` when the environment variable is unset. A second login mode sets an 8 h `HttpOnly` cookie instead. | `backend/src/routes/iam.ts`, `services/authService.ts:7,53`, `frontend/src/lib/api.ts` |
| J2 | **IAM and Jengo web share the host** `maendeleo.martiendejong.nl` (IAM under `/auth`, Jengo under `/jengo`), while the vault is on `vault.prospergenics.com`: a different registrable domain, which matters for WebAuthn RP-ID scoping (see ADR-0001, D2). | IAM `Jwt:Issuer`, jengo-web `APP_PATH`, PM `WebAuthn:ServerDomain` |

## 4. Lango (`scp-jengo/lango`)

| # | Finding | Evidence |
|---|---------|----------|
| L1 | Lango's step-up is **freshness-based** ("authenticated with a passkey in the last 10 minutes"). The plan needs the opposite for TOTP release: one confirmation per action. Invariants worth keeping: an agent can never approve; deciding needs a human principal with step-up. | `README.md` |
| L2 | Open Lango tasks that touch the same ground but are not duplicates: 4604, 4619, 4633 (step-up must only credit what was actually verified; real passkey registration ceremony). | board 64 |
| L3 | The vault adapter in Lango wraps credential release; **TOTP release is not routed through it in v1** (ADR-0001, D8). | ADR |

## 5. Bitwarden (external, checked against vendor docs on 2026-10-08)

| # | Finding | Source |
|---|---------|--------|
| B1 | The Bitwarden **Public API is organisation-level only**: members, collections, groups, events, policies. "This API does not allow for management of individual vault items." Auth is OAuth2 client-credentials with `scope=api.organization`. | bitwarden.com/help/public-api |
| B2 | `bw get totp` works through the CLI and needs an **unlocked vault session** (`BW_SESSION`, a decryption key). | bitwarden.com/help/cli |
| B3 | The integrated-authenticator documentation says nothing about server-side access, an API to generate codes, or Bitwarden servers reading seeds. Generating codes needs Premium or a paid organisation. | bitwarden.com/help/integrated-authenticator |
| B4 | `bw serve` (local REST wrapper around the CLI): the vendor pages fetched here did not contain its security notes, so **not verified**. Assumed to need the same unlocked vault. | n/a |

Conclusion used in the ADR: there is **no documented, supported server-side TOTP API** in Bitwarden. Reaching
it means a permanently unlocked CLI/`serve` process that can read the whole vault, which the plan forbids.

## 6. Duplicate scan (JengoWork, all boards, 2026-10-08)

Swept every list (≈3,800 tasks) for authenticator / TOTP / passkey / WebAuthn / trusted-device / remember-me /
step-up / biometric, then read every non-done title on boards 44 and 12 in full. No existing task covers trusted
browsers, action grants or an external-2FA store.

| Task | Status | Relation |
|------|--------|----------|
| 914 (board 1) WebAuthn gate on JIT approvals | done | Prior art, reused as the pattern for AUTH-02. Not a duplicate. |
| 498 (board 12) Biometric quick unlock | planned | Overlaps AUTH-02/04 on passkey enrolment and device list, but unlocks a *session*, not an *action*. Kept separate; must reuse the AUTH-02 primitive. |
| 5053 (board 44) Silent IAM sign-in: `prompt=none` + SSO cookie on cross-site sign-ins | refined | **Hard predecessor of AUTH-01** (cookie must be sent cross-site, otherwise a trusted browser still asks for a password at the vault). Not a duplicate: it changes cookie/prompt behaviour, not registration, families or revocation. Missed by a keyword-only scan; found by reading all non-done board-44 titles. |
| 5000 (board 44) Deploy IAM develop to maendeleo | testing | Deploy gate for every IAM change in AUTH-01. |
| 499 (board 12) Zero-knowledge credential class | someday | The "architecture decision first" for client-side encryption; the route if the vault host must be excluded from the trust base. Not started here. |
| 1743, 2977 (board 44) Remember me | done | Baseline for AUTH-01 (30-day floor). |
| 4709, 4748 (board 44) End sessions on deactivate / password change | done | Baseline for AUTH-01 revocation. |
| 4573 (board 44) 2FA on passkey/social login | done | Baseline for the human-assurance claim. |
| 3162, 4521, 4523 (board 44) TOTP at IAM login | review | Different feature (IAM's own 2FA). Do not merge scopes. |
| 4604, 4619, 4633 (board 64, Lango) | planned | Related hardening of step-up; non-blocking. |
| 3142 (board 64) Verify WebAuthn assertions cryptographically | done | Lango side of the same pattern. |

Follow-up work packages AUTH-01..05 were created after this scan; their ids are in the README.
