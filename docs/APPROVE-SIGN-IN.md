# Sign in on a new device by approving on a signed-in one

You sign in once on a phone (the normal way: password, passkey, magic link). After that, every other browser, in-app
browser (ChatGPT, Claude), app or computer can sign in with one tap on that phone, or from a popup on a computer you are
already signed in on. No password typing on the new device.

## How it works

1. The **new device** opens the sign-in page, tab **Another device**. It shows a two-digit number and a QR code.
   - With an e-mail address typed: the user's registered devices get a push notification and a popup in every signed-in tab.
   - Without an e-mail address: only a signed-in device that scans the QR code can approve (the sign-in is then for the
     account of whoever approves).
2. The **signed-in device** (installed app, the popup in a signed-in tab, or the page the QR code opens) shows where the
   request comes from and **three numbers**. The user chooses the one the new device shows.
3. The new device polls its request, and as soon as it is approved it finishes the sign-in with a secret only it holds.
   It gets its own refresh-token family, so it shows up in Portal > Sessions and can be revoked on its own.

## Safety rules (see `LoginApprovalService`)

| Rule | Why |
|------|-----|
| The starting browser keeps a random secret (only its SHA-256 is stored); only that browser can finish the sign-in | A leaked request id or QR code is useless to anyone else |
| The approver must pick the matching number out of three; 3 wrong picks deny the request | Defeats "scan this QR to log in" phishing and push fatigue |
| A request lives 3 minutes and signs in at most once (optimistic concurrency) | No replay |
| A request that names a user can only be seen or approved by that user | Nobody else can approve for you |
| At most 10 requests per IP per 10 minutes; at most 3 pending pushes per user | Limits push bombing |
| Unknown / inactive addresses get the same answer as real ones (no push) | No account enumeration |
| The final step is `LoginBypassPasswordAsync`: active check and network gate still apply | A blocked network or deactivated account cannot get in |
| Every step is written to the audit log (`LoginApproval*`) | Trace who approved what, from which device |

The first sign-in on the first device still needs a normal login. Approval needs a device that is signed in.

## Endpoints

| Method + path | Who | Purpose |
|---------------|-----|---------|
| `POST /api/auth/approval/request` | anyone | start; returns `id`, `pollSecret`, `matchNumber`, `expiresAt` |
| `POST /api/auth/approval/{id}/status` | holder of the secret | `pending/approved/denied/expired/consumed` |
| `POST /api/auth/approval/{id}/complete` | holder of the secret | finish: sets the refresh cookie and the `IAM.Session` cookie like a password sign-in |
| `GET /api/auth/approval/pending` | signed in | requests that name me (drives the popup) |
| `GET /api/auth/approval/{id}` | signed in | one request with its three numbers |
| `POST /api/auth/approval/{id}/approve` `{number}` | signed in | approve |
| `POST /api/auth/approval/{id}/deny` | signed in | refuse |
| `GET /api/push/vapid-public-key` | anyone | public VAPID key |
| `POST /api/push/subscriptions`, `/remove` | signed in | register / drop a push endpoint |

## Deploy

1. **Database first** (IAM only runs `EnsureCreated`): run `docs/manual-migrations/20261124120000_AddLoginApprovals.sql`
   on `iam_db` BEFORE swapping the build. Additive and idempotent (creates `LoginApprovals` and `WebPushSubscriptions`).
2. **Push keys** (optional; without them the popup and the QR route work and only the push is off). Generate a VAPID key
   pair once and set, as environment variables or in the secrets file:

   ```
   WebPush__PublicKey   = <base64url uncompressed P-256 public key>
   WebPush__PrivateKey  = <base64url 32-byte private key>
   WebPush__Subject     = mailto:<a monitored contact address>
   ```

   Keep the private key in the vault, never in the repo. Changing the key pair invalidates every registered push endpoint
   (users turn notifications on again).
3. The admin-ui now ships `manifest.webmanifest`, `sw.js` and icons. `sw.js` and the manifest are served `no-cache`
   (stable file names); hashed build assets stay `immutable`.

## Install as an app (Android)

Open the IAM site in Chrome or Brave, menu > **Install app** / **Add to Home screen**, sign in once, then in
**Portal > Sessions** turn on **Approve sign-ins from this device**. The next sign-in request arrives as a notification;
tapping it opens the approve page in the app.

Not yet covered (follow-ups): iOS (needs the app installed to the Home Screen; web push works from iOS 16.4), a QR scanner
inside the installed app (today the phone camera opens the link in the default browser, which must be signed in), a
trusted-device list with names, and a Play Store wrapper (TWA) like Jengo Meet.
