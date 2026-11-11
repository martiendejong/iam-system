# Device login (`/api/device-auth`)

Phase 1 hardening, task 5159. Phase 2 (challenge-response certificate login, real HMAC with a recoverable secret,
SDK and docs) is task 5242, with the .NET and JavaScript clients in task 5243.

## What the server enforces

| Rule | Behaviour |
|------|-----------|
| One-time nonce | The nonce in `timestamp:nonce:secret` works once per device. It is remembered for 11 minutes, longer than the 5-minute timestamp window on both sides of "now". Only a request that proved the secret spends its nonce. |
| Per-device lockout | `DeviceAuth:MaxFailedAttempts` (5) failures inside `DeviceAuth:FailureWindowMinutes` (15) lock that device for `DeviceAuth:LockoutMinutes` (15). Other devices are unaffected. Attempts during the lock do not extend it, and a correct login clears the count. Ids of devices that do not exist are never tracked. |
| Certificate login only through a trusted gateway | `POST /api/device-auth/certificate` needs the header `X-Device-Gateway-Key` to match one of `DeviceAuth:GatewayKeys` (or the single `DeviceAuth:GatewayKey`). A certificate PEM is public, so a bare PEM from anyone else gets no token. With no key configured, certificate login is off. Keys shorter than 32 characters are ignored. |
| SHA-256 thumbprints | `DeviceCertificate.Thumbprint` is the SHA-256 of the DER certificate (upper-case hex) when issued, validated and compared at login. Rows stored earlier with the SHA-1 value are rewritten from their stored PEM at API start-up (log line "Device certificate thumbprint backfill"). |
| One generic failure | Every failed login (unknown or inactive device, wrong secret, locked device, replayed nonce, stale timestamp, malformed password, untrusted caller, unknown certificate) returns `401 { "error": "Authentication failed" }` after the same amount of BCrypt work. The reason goes to the server log only. |

## Configuration

```json
"DeviceAuth": {
  "MaxFailedAttempts": 5,
  "FailureWindowMinutes": 15,
  "LockoutMinutes": 15,
  "GatewayKeys": [ "<at least 32 random characters, kept out of source control>" ]
}
```

Give the edge gateway its key and have it send `X-Device-Gateway-Key` on certificate logins only. Rotate by adding the
new key next to the old one, switching the gateway, then removing the old key.

## Limits

- The lockout and nonce state is in memory, per API process. With several instances behind one address a lock needs
  its attempts to reach the same instance, and a replay can pass once on each instance. A shared store is a follow-up.
- A lock is per device, so someone who knows a device id can keep that device locked by failing on purpose. That is the
  price of not locking by address; the lock is temporary and the failures are logged.
- The HMAC step still verifies the stored BCrypt hash against the secret the device sends (phase 2 replaces this), so
  the secret travels in the request. Use TLS.
