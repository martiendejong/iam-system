# ADR-0001: Trusted browsers and a fresh-confirmation authenticator

- **Status:** Proposed by Jengo AGI, 2026-10-08 (task 5097, AUTH-00). Three choices need Martien's confirmation
  (section 7). Nothing here switches production authentication.
- **Scope:** IAM (identity, sessions), Password Manager (TOTP items and release), Jengo web (UI entry point).
- **Companion documents:** [inventory](inventory.md), [threat model](threat-model.md),
  [compatibility matrix](compatibility-matrix.md), [key custody decision](key-custody-decision.md),
  [estimate](estimate.md). Proof of concept: `passwordmanager` PR for task 5097, `docs/authenticator/POC.md`.

## 1. Context

Martien wants to open Jengo without typing a password every time, and to fetch a GitHub 2FA code inside Jengo with
one local PIN/biometric confirmation. Someone holding only an open Jengo session or a stolen session cookie must
not be able to read, export or enrol a code. The plan (v1.0, 2026-10-08) states the intent; the inventory shows what
the code does today. The facts that shape the decisions:

- Revocation does not currently reach access tokens or the `IAM.Session` cookie (inventory I6), refresh-token replay
  revokes the whole user (I7), and there is no browser registration (I3).
- Jengo web mints its own 30-day JWT into `localStorage` after the IAM login (J1); Password Manager mints its own
  7-day JWT (P1). Neither can be revoked from IAM.
- Password Manager already has a working WebAuthn action gate with `UserVerification = Required` (P2), but with two
  gaps (P3). IAM's own passkey code is login-grade only (I8) and is not configured for the live origin (I9).
- Bitwarden offers no supported server-side TOTP API (B1-B3).
- The vault is on a different registrable domain than IAM and Jengo (J2), and a WebAuthn credential belongs to one
  RP ID.

## 2. Decisions

### D1. The verifier of the fresh confirmation sits next to the seeds (Password Manager), not in IAM

Password Manager verifies the WebAuthn assertion and issues the one-time grant itself. IAM supplies who the human
is and whether their browser registration is still valid, but IAM never signs anything that releases a code.

*Why:* with the verifier in IAM, one compromised IAM host could mint grants and read codes without ever touching the
authenticator. With the verifier next to the seeds, IAM compromise can forge a *session* but cannot produce a
user-verified assertion, so the authenticator's private key stays an independent trust root. It also reuses the task
914 code that is live and tested instead of building a second ceremony stack, and it avoids a signed cross-service
grant format.

*Deviation from the plan:* the plan lists "WebAuthn credentials and challenges" under IAM. For the **action
confirmation** that ownership moves to Password Manager. IAM still owns login passkeys, sessions, tenants, browser
registration and revocation.

### D2. RP ID `vault.prospergenics.com`; the confirmation runs on the vault origin

A passkey is bound to its RP ID, and the vault, IAM and Jengo web do not share a registrable domain. v1 therefore
runs the confirmation on the vault origin, which already hosts the passkeys: Jengo's "Copy code" opens a small vault
window (popup or deep link), the browser shows its own PIN/biometric prompt, the window shows or copies the code. Browser
single sign-on from IAM makes arriving there passwordless on a trusted browser.

*Optional later:* WebAuthn Related Origin Requests would let Jengo's own page run the ceremony for the vault RP ID.
Browser support is partial (Chrome/Edge 128+, Safari 18+ reported; Firefox not assumed), so it is a feature-detected
enhancement in AUTH-04, never a requirement.

*Deviation from the plan:* "Jengo HCI/Web shows the interface" becomes "Jengo links to the vault authenticator
window" for v1.

### D3. Trusted browser = a named registration that owns a refresh-token family (IAM, AUTH-01)

- New `BrowserRegistration` (id, user, name chosen by the user, browser/OS hint, created / last seen, duration,
  optional expiry, revoked at/reason). Each registration owns one **token family**: `RefreshToken.FamilyId`.
- Durations offered after strong sign-in: this session only, 30 days, 90 days, until I revoke. Default on a device the
  user marks as their own: until revoked. Shared device: session only. "Until revoked" means refresh tokens carry a
  30-day idle timeout, so the browser must be used at least that often (configurable up to 90 days); state this in
  the UI instead of promising "forever".
- Rotation per use stays (opaque 256-bit token, SHA-256 hash at rest, already true). Reuse of a rotated token
  revokes **that family only**, audits it and raises a security alert; the current user-wide revoke stays as an
  explicit "sign out everywhere" action.
- Revocation reaches live requests: the registration id travels as `bid` in the access token and in the
  `IAM.Session` ticket; `JwtBearer` `OnTokenValidated` and the cookie `ValidatePrincipal` check it against a cache
  of at most 30 seconds (so "revoked within 60 s" holds). OpenIddict authorizations are revoked with the family.
  OpenIddict access tokens drop from 15 to 10 minutes (plan maximum).
- Apps that keep their own session (Jengo web, Password Manager) stop minting 30-day / 7-day sessions: they carry the
  IAM session id (`sid`) and check it against a small IAM endpoint with the same 30-second cache, and their own
  token lifetime drops to the access-token scale. Moving them to IAM-issued tokens (JWKS validation, documented in
  `docs/ACCESS-TOKEN-VALIDATION.md`) is the cleaner end state and is not required for v1.
- Cookies stay `Secure`, `HttpOnly`, `SameSite` as today; the refresh endpoint requires a custom request header
  (CSRF). Jengo's `localStorage` JWT and the `?token=` redirect are removed in this package.
- Every login method (password, magic link, SMS OTP, passkey, social) ends in one shared "complete login" step that
  creates the browser registration and the SSO cookie; today only the password/2FA path sets `IAM.Session` (I13).
  Task 5053 (`prompt=none`, cookie `SameSite=Lax`) is a **hard predecessor**: without it a trusted browser still
  meets a login page at the vault. IAM changes also wait on the develop deploy (task 5000).
- `TrustedDevice` (client-supplied fingerprint) stays a risk-score input and is never read as trust.
- Tokens gain `principal_kind` (task 4057) and `amr` so downstream services can refuse non-humans.

### D4. WebAuthn library: Fido2NetLib 4.x

Already used by IAM and Password Manager, passed every refusal case in the PoC against a real ES256 virtual
authenticator (UV off, wrong origin, wrong RP ID, bad signature, replay), MIT licensed. Stay on 4.x, take 4.2.0 via
the open Dependabot PRs (#169/#170) once the IAM suite is green, and re-evaluate 5.x when it leaves preview. The
backup-eligible / backed-up bits are read from the raw authenticator-data flags (`0x08`, `0x10`) on registration and
on every assertion, because the 4.x registration result does not expose them. This is what lets the UI say
"synced passkey" instead of "this device" (plan: do not claim device binding).

### D5. Authenticator backend: a small, owned TOTP component in Password Manager; not Bitwarden

Bitwarden's supported surfaces are the organisation-level Public API (no vault items) and the CLI (unlocked vault
session). A supported server-side TOTP API is not documented. The only route is a permanently unlocked CLI with the
whole vault in reach, which the plan rules out. Password Manager already is the vault, has tenancy and audit, and
already owns the WebAuthn gate. The generator is ~60 lines on the BCL's HMAC, checked against the RFC 6238
Appendix B vectors (SHA-1/256/512); a third-party OTP library would add supply chain for no gain. Bitwarden stays the
user's personal manager and the *source* for re-enrolment guidance only; no seeds are imported from it
automatically.

### D6. Seeds: AES-256-GCM envelope encryption with bound context; the KEK lives outside the database

Per-item random data key wrapped by a versioned key-encryption key, associated data = format | tenant | owner |
item | KEK version. The legacy `EncryptionService` is not used (P4). Where the KEK lives and who holds the backup is
the [key custody decision](key-custody-decision.md): a DPAPI-protected key file outside web root and outside DB
backups, with an offline recovery package held by Martien. Implemented and tested in the PoC (`SeedProtector`:
tamper, wrong tenant/owner/item, relabelled version, rotation).

### D7. Challenge and grant model

- Begin returns a random 256-bit challenge (60 s), bound to *(user, tenant, session id, action, target)*.
- Complete consumes the challenge **first** (atomic remove; in the durable version a conditional
  `UPDATE … WHERE Used = 0`), then verifies signature, challenge, origin, RP ID, **UV flag**, credential ownership and
  counter. Any failure leaves the challenge spent.
- Success returns an opaque 256-bit grant, hashed at rest, 30 s, single use, same binding. The release adapter
  re-checks the grant and recomputes tenant/owner/item binding itself before decrypting.
- Near the end of a time step the adapter returns the next code with "starts in N s" (below 5 s remaining); no second
  prompt inside the same bounded action.
- A grant is opaque and local, not a JWT: verifier and releaser are the same service, so no signature scheme or key
  distribution is needed.
- WebAuthn does not show the user *what* they are confirming; the binding is server-side state. The window therefore
  states the service, account and action in plain text next to the system prompt, and the PoC never relies on the
  prompt text. This is stated as a limit, not solved.

### D8. Humans only, by construction

The authenticator controller accepts the JWT bearer scheme only (API-key principals are never authenticated on it),
requires a `jti` session id, and refuses any session whose `principal_kind` claim is not `Human`. No MCP tool, agent
skill or Lango rule is given a route to `/api/authenticator/*`; Lango is not in the release path in v1 (its step-up is
freshness-based, the opposite of one-confirmation-per-action). A recovery path never mints a grant. Voice is not an
unlock method.

### D9. Rollout: flag per user, old login stays, pilot is Martien's own GitHub test account

`Authenticator:Enabled` plus `Authenticator:AllowedUserIds`; empty list means nobody and the endpoints answer 404.
No real seed is added, no existing account changes and no production authentication switches before Martien accepts
the pilot. Real-seed prerequisites, enforced in code in AUTH-03: at least two enrolled passkeys on different
authenticators, a completed backup-export dry run, and a restore test.

### D10. Recovery without a chicken-and-egg problem

At least two independent strong recovery means before the first real item: (a) a second passkey on a different
authenticator (e.g. platform key plus hardware security key), (b) an offline recovery package outside Jengo: the KEK
backup and an encrypted export of the item set, key held separately. Mail in Jengo is never the only route. Recovery
codes are high-entropy, single-use, hashed server-side. A recovery action revokes existing sessions and notifies
the pre-registered independent channel. "Never lost" is not claimed; restore drills are the evidence (AUTH-05).

## 3. Options considered

| Question | Chosen | Rejected, and why |
|----------|--------|-------------------|
| Who verifies the confirmation | Password Manager (D1) | IAM verifies and signs a grant: single point whose compromise releases codes, needs a cross-service signed format, and passkeys would have to be enrolled in a not-yet-configured IAM RP ID (I9). |
| Where the ceremony runs | Vault origin window (D2) | Ceremony inside Jengo's page: needs Related Origin Requests everywhere, support is partial. Iframe: cross-origin WebAuthn in frames is restricted. |
| TOTP source | Owned component (D5) | Bitwarden CLI/`serve`: unlocked whole-vault process. Bitwarden Public API: org-level only. |
| OTP library | In-house on BCL HMAC | Third-party OTP package: no benefit over 60 lines plus RFC vectors. |
| Session trust | Registration + token family (D3) | Reusing `TrustedDevice`: client-supplied fingerprint. One shared cookie across subdomains: widens blast radius, explicitly out. |
| Grant format | Opaque, local, single use (D7) | JWT grant: needs key distribution between services that are already the same service. |

## 4. Consequences

- AUTH-02 ownership moves to Password Manager (IAM contributes claims and revocation, not the ceremony). The
  work-package descriptions on the boards say so.
- AUTH-01 is the largest package because it touches IAM, Jengo web and Password Manager sessions (estimate).
- The task-914 credential table is shared, so its two enrolment/removal gaps (P3 b, c) are fixed inside AUTH-02
  *before* any real item exists, and the atomic challenge consume replaces `MarkUsedAsync` for the new purpose.
- Live IAM passkey login needs its `Fido2` configuration set before it is useful at all (I9); this is a separate
  small fix and is not on the authenticator's critical path.
- The PoC's in-memory item and challenge stores are throwaway; the durable stores and the DPAPI KEK provider are AUTH-02/03.

## 5. What this does not guarantee (carried into the threat model)

A compromised endpoint device, a malicious extension, XSS at the moment of code release, a compromised vault host
with the KEK, or a user who confirms a prompt they did not start can still expose codes. TOTP stays phishable.
Server-side encryption does not protect against someone who controls the vault host; excluding that needs a
separate client-side (E2EE) design - the WebAuthn PRF extension is the candidate - and is **not** claimed here.
GitHub's own policy is unchanged; a direct GitHub passkey is stronger against phishing and stays preferred where available.

## 6. Rollback

Everything ships behind `Authenticator:Enabled` (default off) and per-user opt-in. Disabling the flag removes the
endpoints from view without data loss. Trusted-browser registration ships behind its own per-tenant flag with the
old login path untouched; turning it off returns to today's behaviour (30-day remember-me floor). Because no real
seed exists before pilot acceptance, the rollback of AUTH-00..03 contains no data.

## 7. Needs Martien

1. Confirm D1/D2 (verification and window on the vault origin) versus the plan's wording, or ask for the IAM-verifier
   variant with its trade-offs above.
2. Name the holder of the offline recovery package and a second holder (key custody decision, section 6).
3. Confirm the pilot account: your own GitHub test account, and that two authenticators (platform + security key)
   are available to enrol before any real seed.
