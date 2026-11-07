# MOBILE-01: linked-app sign-in from ChatGPT (decision note, DRAFT)

- **Status:** DRAFT from desk research and code reading (task 5623). **Nothing here is proven on a device.** The real-phone
  proof (ChatGPT -> app -> confirm -> back to the same screen) still has to be run by Martien; every device claim below is
  marked *unverified* until then.
- **Scope:** proof only, test accounts only, production login unchanged. Reuses OpenIddict authorize / `prompt=none`
  (`docs/SILENT-SIGN-IN.md`, task 5053) and ADR-0001 (task 5097, branch `feat/5097-auth-00-trusted-device-authenticator`).
  No new session architecture.

## 1. Protocol choice

| Option | Verdict |
|---|---|
| **OIDC authorization code + PKCE (S256)** in the browser, with the *approval* step done in the app | **Chosen as base.** Standard, already supported by OpenIddict. The browser keeps the PKCE verifier, so only the window that started the login can finish it. |
| CIBA (OIDC Client-Initiated Backchannel Authentication) | Optional second step for "desktop asks, phone confirms". Not built into OpenIddict out of the box; decide after the device proof shows whether the app-link route works at all. |
| Device authorization grant (RFC 8628) | Fallback candidate for desktop-to-phone (user code), standard, no app link needed. |
| Own cryptography / custom tokens | Rejected. |

Proof logic (`src/IAM.Core/Mobile/LinkedAppSignInProof.cs`, 7 tests): request holds only the PKCE challenge; app approves
after the OS biometric/PIN prompt; the original browser consumes with its verifier; single use, 2-minute lifetime, wrong
verifier does not burn the request. In-memory, not wired into any endpoint.

## 2. Embedded-browser limits (ChatGPT in-app browser) - all *unverified*

- Whether an in-app browser honours Android verified App Links (`assetlinks.json`) or keeps navigation inside its own view is
  unknown and differs per host app. This is the main thing the phone proof must answer.
- Switching to the IAM app may leave the in-app browser in the background; returning to the same screen is not guaranteed.
  No claim is made that it works.
- Cookies of the in-app browser are not shared with Chrome, so a "sign in with the normal browser, come back" route cannot rely on
  an existing IAM session.

## 3. Fallback

If the app link does not open the app, or return fails: show a short code / QR and use the device authorization flow
(RFC 8628) or open the normal system browser with the same PKCE request. Never silently downgrade to password in the embedded view.

## 4. Install form, iOS, estimate (proposals)

- **Install form:** start with a verified-link Android app (sideload/internal test track) for the pilot; PWA cannot do biometric
  confirm bound to a platform key with app-link handoff reliably. Revisit after the proof.
- **iOS follow-up:** Universal Links + ASWebAuthenticationSession; same server logic; separate package.
- **Estimate for the proof app and device run:** about 2-3 days of work plus one 1-hour session with Martien's phone.

## 5. Repeated logout - desk findings, NOT yet reproduced

From the code (`Program.cs`, `AuthController`, `AccessTokenSessionValidator`) these can each end a session early:
1. Access token 15 minutes, refresh token 7 days; the `IAM.Session` cookie lasts 8 hours idle unless *Remember me* is ticked (then 30 days).
2. Since task 5628, an access token dies when its refresh token is revoked (checked within 30 s). Refresh-token reuse revokes the whole user (inventory I7), so two tabs or an app and a browser refreshing the same token can log everything out.
3. Only the password/2FA login creates `IAM.Session` (SILENT-SIGN-IN.md), so other login methods get `login_required` on `prompt=none`.

Hypothesis 2 is the most likely cause of "logged out repeatedly" with several clients; it needs a reproduction with two clients before it is recorded as the cause in #5102 / #5053.

## 6. Open items before this note can be final

- [ ] Real-phone proof from ChatGPT, normal Android browser, desktop-to-phone (Martien)
- [ ] Reproduce the repeated logout and record the cause in #5102 / #5053
- [ ] Martien confirms the protocol choice, which then fixes the design for #5624 / #5625
