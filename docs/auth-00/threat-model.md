# AUTH-00 threat model

Task 5097. Scope: trusted browsers (IAM) and the fresh-confirmation authenticator (Password Manager, Jengo entry
point). Decisions referenced as D1..D10 are in [ADR-0001](ADR-0001-trusted-browsers-and-fresh-confirmation.md);
inventory ids (I*, P*, J*) are in [inventory.md](inventory.md).

## 1. Assets, actors, boundaries

| Asset | Where | Sensitivity |
|-------|-------|-------------|
| TOTP seeds (third-party 2FA) | Password Manager, encrypted per item | Highest: a seed is a permanent second factor for the external account |
| TOTP codes (released) | Browser memory/DOM, clipboard, for ~30 s | High but short-lived |
| KEK and its backup | DPAPI file on the vault host; offline recovery package | Highest |
| Passkey public keys, counters | Password Manager table `WebAuthnCredentials` | Medium (cannot forge, can verify) |
| Sessions: refresh families, `IAM.Session`, PM/Jengo JWTs | IAM, PM, browser | High (a session lets an attacker start the ceremony, not finish it) |
| Audit trail | PM audit log | Integrity matters; must hold no secrets |

Actors: the user (Martien); an attacker with a stolen cookie/JWT; an attacker with XSS in a Jengo/vault page; a
malicious extension or malware on the endpoint; an agent or API token; a tenant neighbour; someone with a database
copy or backup; someone with IAM-host or vault-host control; a phisher.

Trust boundaries: browser | Jengo web backend | IAM host (`maendeleo`) | vault host (`vault.prospergenics.com`) | the
authenticator (OS/hardware, outside every server).

## 2. Threats, mitigations, evidence

"PoC" rows are exercised by tests in the `passwordmanager` PR for task 5097 (92 tests, mutation-checked: removing
the UV requirement, the grant check, or challenge consumption each makes named tests fail). "Later" rows are
requirements on a follow-up package, not claims.

| ID | Threat (plan risk) | Mitigation | Evidence | Residual |
|----|--------------------|-----------|----------|----------|
| T1 | Open browser or stolen cookie/JWT reads a code (open browser, stolen cookie) | A session alone never yields a code: a fresh, user-verified assertion mints a one-time grant (D7). UV is `Required` and the UV flag is checked by the library | PoC: `NormalSessionAlone_withNoGrant_cannotReadACode`, `StolenSessionWithOnlyAUserPresentTap_cannotMintAGrant`, `AssertionWithoutUserVerification_isRefused` | Attacker can *start* a ceremony; it only completes if the real user confirms (see T11) |
| T2 | Replay of challenge, assertion or grant (replay) | Challenge consumed first, atomically; 60 s TTL; grant hashed, single-use, 30 s | PoC: `ChallengeIsSingleUse_evenForASecondFullyValidAssertion`, `EightParallelCompletions…`, `Grant_cannotBeReplayed`, `ExpiredChallenge…`, `ExpiredGrant…` | In-memory store is single-instance; durable store must use a conditional update (AUTH-02) |
| T3 | Account / tenant / item / session confusion (account switch) | Challenge and grant are bound to user, tenant, session id (`jti`), action, target; a mismatch refuses and burns | PoC: `ChallengeAnsweredForADifferentContext…` (4 cases), `GrantForOneItem…`, `GrantFromAnotherSession…`, `AddItemGrant_cannotBeUsedToReleaseACode`, `OtherTenant…` | none known |
| T4 | Agent, API token or service account asks for a code (agent token) | JWT-bearer scheme only; API keys are not authenticated on the controller; `principal_kind` must be Human; no MCP/agent route; recovery never mints a grant (D8) | PoC: `AgentOrServicePrincipal…`, `Controller_acceptsBearerJwtOnly…`, `SessionWithoutAJti…` | PM sessions carry no `principal_kind` yet; AUTH-01 adds it. Until then the scheme restriction is the control |
| T5 | Database copy / backup leak (database copy) | AES-256-GCM envelope, KEK outside DB and backups (D6, key custody) | PoC: `ProtectThenUnprotect…NoPlaintext`, `MissingKek_failsClosed` | Anyone with DB + KEK reads seeds (T13) |
| T6 | Ciphertext moved to another row/tenant, or tampered | Associated data binds tenant, owner, item, KEK version | PoC: `CiphertextCopiedToAnotherTenantOwnerOrItem…`, `AnyTamperedByte…`, `RelabelledKekVersion…` | none known |
| T7 | Phishing a TOTP code | Window states service, account, action; origin is the vault RP ID so a look-alike domain gets no assertion; prefer a direct GitHub passkey where possible | PoC: `AssertionForAnotherRpId…`, `AssertionForAnotherOrigin…` | TOTP itself stays phishable; a user can be tricked into pasting a code into a fake GitHub page |
| T8 | Lost or stolen device | Revoke the browser registration (token family, `bid` checks ≤ 60 s), separate passkey removal, "sign out everywhere", independent recovery (D3, D10) | Later: AUTH-01 revocation tests, AUTH-05 lost-device drill | Synced passkeys are not removed from the provider by us (we block at IAM/vault); a lost device whose OS unlock is weak is the user's risk |
| T9 | Enrolling the attacker's passkey with a stolen session (P3 b) | Enrolment of a 2nd+ passkey requires an assertion from an existing one; the first needs a recent login; removal needs the same; cap per user | Later: AUTH-02 (**precondition for any real item**). PoC does not fix this and says so | Until AUTH-02 lands the PoC must stay flag-off for real use |
| T10 | XSS during a release, or a stolen long-lived token in `localStorage` (J1) | Codes only after a confirmation, never preloaded, `no-store`; Jengo's 30-day JWT and `?token=` redirect removed (D3); strict CSP and no third-party scripts on the authenticator window | PoC: `Listing_carriesMetadataOnly…`, `no-store` header asserts. Later: AUTH-01/04 | XSS *at the moment of release* can read the code; this is stated, not solved |
| T11 | Malware or extension on the endpoint; user confirms a prompt they did not start | None that the server can enforce. The prompt appears only after the user's own click in a window that names the action | n/a | **Accepted risk.** A compromised endpoint defeats TOTP and every other client-side secret |
| T12 | IAM host compromised | IAM can forge a *session* but not a user-verified assertion, so it cannot release codes or add items (D1) | By design; AUTH-05 review | It can lock users out and enrol *its own* passkey unless T9 is closed |
| T13 | Vault host or admin compromised | KEK file ACL, separate identity option later, audit | n/a | **Accepted risk in v1.** Server-side encryption does not defend against whoever controls the vault host. Excluding that needs client-side encryption (task 499, WebAuthn PRF) - not claimed |
| T14 | Brute force or flooding of challenge/verify endpoints | Per-user pending-challenge cap, rate limit, lockout counters, generic errors | Later: AUTH-02. PoC only limits TTL | PoC has no rate limit (documented) |
| T15 | Clock skew breaks codes or TTLs | NTP monitoring and alert; TTLs server-side only; codes computed from the vault host clock | Later: AUTH-05 monitoring | A skewed host issues wrong codes; visible as user failures |
| T16 | Clipboard read by another app or page | Copy only on the user's action, show remaining validity, offer the visible-code fallback; no automatic copy on load | Later: AUTH-04 | Browsers cannot reliably clear the clipboard |
| T17 | Secret in logs, traces, cache, screenshots, tasks, prompts | Audit holds actor, tenant, item id, action, time, outcome only; responses `no-store`; no analytics/service worker on the window; test evidence redacts | PoC: `SeedAtRest_isEncrypted_andTheAuditTrailHoldsNoSecretAndNoCode` | Operational discipline outside the code: no screenshots of codes in tasks |
| T18 | Recovery used to bypass the confirmation | Recovery re-establishes access and sessions, never a grant; recovery revokes existing sessions; notify an independent channel | Later: AUTH-05 | Social engineering of the human recovery holder |
| T19 | Feature switched on for everyone by mistake | Enabled flag plus allow-list; empty list = nobody; disabled looks like 404 | PoC: `FeatureOff_orUserNotListed_looksLikeNothingIsThere` | Config mistake naming the wrong user |
| T20 | Synced passkey compromise (cloud account takeover) | Record backup-eligible / backed-up bits and AAGUID, label "synced" truthfully, option to require a non-synced credential later (D4) | Later: AUTH-02 | A synced passkey is as strong as its provider account |
| T21 | Race: session revoked while a release is in flight | The adapter re-checks session validity immediately before decrypting; grants are 30 s | Later: AUTH-01/03 | A few seconds of window |
| T22 | An agent with screen control clicks the UI | The OS PIN/biometric prompt requires physical presence; an agent cannot satisfy UV (the task 914 argument) | PoC: UV enforced server-side | An agent on the same endpoint can still wait for the human to approve |
| T23 | Third-party passkey provider satisfies "UV" with its own unlock | The standard does not let a server demand OS biometrics; record AAGUID and show the provider; optional AAGUID allow-list for higher assurance | Later: AUTH-02, compatibility matrix | If the provider's vault unlock is weak, UV is weak |

## 3. Coverage summary

- Server-side refusal cases required by AUTH-02 are all exercised against a real ES256 virtual authenticator
  and fido2-net-lib: UV false, wrong origin, wrong RP ID, bad signature, wrong user, wrong tenant/item/session,
  expired challenge, replay, concurrent completion.
- Not covered by this task (and therefore **not claimed**): real Android and desktop browser proof, durable stores,
  rate limiting, passkey-enrolment step-up, trusted-browser revocation, recovery drills, XSS/CSRF hardening of real
  UI. Each is an acceptance line of a follow-up package.
- Hard constraints respected in the PoC: fictional seeds only (RFC 6238 test key), no real account touched, no
  production authentication switched, feature flag off by default.
