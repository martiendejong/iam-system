# AUTH-00 compatibility matrix

Task 5097. What the design depends on, what is known, and what is still unproven. The point of this matrix is to
keep "expected from the standard" apart from "verified on a device". **Nothing in the "Verified" column was proven
on a physical Android or desktop device in this task**; that proof is an acceptance line of AUTH-02 and AUTH-04.
Which browsers and devices Martien actually uses is unknown and is the first input needed (section 3).

Status key: **Verified here** = exercised by a test or command in this task. **Expected** = taken from the
standard or vendor documentation, not yet tried on a device. **Unknown** = needs a real check.

## 1. Platform capabilities

| # | Capability | Needed for | Status | Notes / source | Verify in |
|---|-----------|------------|--------|----------------|-----------|
| 1 | Server-side WebAuthn assertion check with `userVerification: required` (UV flag enforced, origin, RP ID, counter) | Fresh confirmation (D7) | **Verified here** (server side, real ES256 virtual authenticator, fido2-net-lib 4.0.1) | PoC tests `AssertionWithoutUserVerification…`, `…AnotherOrigin…`, `…AnotherRpId…` | AUTH-02 on real devices |
| 2 | Platform authenticators that can satisfy UV: Windows Hello (PIN/biometric), Android screen lock/biometric, Touch ID / Face ID | Daily flow on one device | Expected | W3C WebAuthn L3 (UV, local biometric/PIN, PIN never sent to the server) | AUTH-02: one Android + one desktop |
| 3 | Roaming security keys with FIDO2 PIN | Second authenticator / recovery (D10) | Expected | A key with no PIN set cannot satisfy UV and the ceremony fails closed, which is the desired behaviour | AUTH-02 |
| 4 | Hybrid transport (phone as authenticator for a desktop browser) | Android + desktop proof; "phone confirms, laptop shows code" | Expected | Needs Bluetooth proximity on the desktop; cannot be exercised headless | AUTH-02 |
| 5 | Synced vs device-bound passkeys; backup-eligible (BE) and backed-up (BS) bits | Truthful labelling (plan: no device-binding claim) | Expected | Read bits `0x08` / `0x10` of authenticator data ourselves on every registration/assertion (D4). Fido2 4.0.1 does not expose them from the registration result (I12) | AUTH-02 |
| 6 | Third-party passkey providers (password-manager passkeys) | Anyone whose passkeys live in a password manager | Unknown | The server cannot demand OS biometrics; "UV" may mean the provider's own vault unlock (T23). Record AAGUID and show the provider | AUTH-02 |
| 7 | WebAuthn Related Origin Requests (vault RP ID usable from Jengo's page) | Optional in-page ceremony (D2) | Expected, partial | Introduced in Chrome 128/129 per the Chrome team; other browsers reported partial. Not relied on in v1 | AUTH-04 (feature-detect) |
| 8 | `SameSite=Strict` SSO cookie sent on a cross-site redirect to IAM | Passwordless arrival at the vault on a trusted browser | **Verified (Edge, 2026-10-07, task 5053)**: it is **not** sent; `Lax` is | Predecessor 5053 (`prompt=none` + `Lax`) | AUTH-01 (after 5053) |
| 9 | Only password/2FA login sets `IAM.Session` | Trusted browser created by passkey/magic-link/OTP/social login | **Verified here** (code: one `SignInAsync` call site) | I13 | AUTH-01 |
| 10 | `navigator.clipboard.writeText` after an `await` on the WebAuthn prompt | "Copy code" with one confirmation | Expected to be unreliable | Clipboard writes need a secure context and recent user activation; a system prompt in between can consume it, Safari is strictest. Design already has the fallback: show the code with one visible Copy button, then "Copied" | AUTH-04 on Android Chrome, Safari, Edge |
| 11 | `window.open` for the vault confirmation window | Jengo → vault authenticator (D2) | Expected | Must start from a user click; popup blockers and installed web apps may refuse; deep link in the same tab is the fallback | AUTH-04 |
| 12 | Cookies cleared, private/incognito windows, Safari tracking prevention | "Trusted for a long time" promise | Expected to shorten trust | Browsers can drop or cap site data; the UI must say "you may be asked to sign in again" (plan) | AUTH-01 |
| 13 | Screen reader, keyboard-only, 200% zoom, touch targets ≥ 24×24 CSS px (prefer 44) | Accessibility (WCAG 2.2 AA target) | Unknown | No UI exists yet | AUTH-04 |
| 14 | Server clock accuracy (NTP) | Correct TOTP, TTLs | Unknown for the vault host | Monitor drift (T15) | AUTH-05 |

## 2. Component compatibility

| Component | Version / state | Impact |
|-----------|-----------------|--------|
| Fido2 / Fido2.AspNet | 4.0.1 (IAM, PM); 4.2.0 available and proposed by Dependabot; 5.0 only preview; MIT | Stay on 4.x (D4) |
| .NET | PM `net8.0`; this build host runs 9/10 with `DOTNET_ROLL_FORWARD=LatestMajor` | New PM code is `net8.0`-clean (built and tested here) |
| PM test host | 298 pass / 16 fail on untouched develop (HTTP integration tests, host gap) | New security tests are controller/service level; an HTTP-level proof needs a real browser run |
| IAM | live deploy behind develop (task 5000 in `testing`); live `Fido2` config absent (I9) | Any IAM change waits on 5000; fix the `Fido2` origin config separately |
| Jengo web | self-minted 30-day JWT in `localStorage` (J1) | Replaced in AUTH-01 |
| Lango | step-up is freshness-based | Not in the release path in v1 (D8) |
| Bitwarden | Public API org-level only; CLI needs an unlocked vault (B1-B3) | Not used as the backend (D5) |

## 3. Inputs needed from Martien

1. Which browsers and operating systems do you use day to day (desktop and phone)? Is the phone Android?
2. Where do your passkeys live today (platform, Google/Apple account sync, a password manager)?
3. Do you have a hardware security key, or should one be bought for the second-authenticator rule?
