# Re-confirming sensitive account changes (task 5488)

An access token alone is not enough to change an account's second-factor material.
These calls need a fresh proof of identity in the request body:

| Call | Body |
|---|---|
| `POST /api/mfa/email/disable` | `{ "currentPassword": "..." }` or `{ "emailCode": "123456" }` |
| `POST /api/mfa/recovery-codes` | same |
| `POST /api/passkey/register/begin` | `{ "username", "displayName", "currentPassword" \| "emailCode" }` |

`POST /api/mfa/confirmation-code` e-mails a 6-digit code to the verified address (10 minutes, single use,
rate limited). Use it for social/SSO accounts that never set a password.

- Missing or wrong confirmation: `403 { "error", "code": "confirmation_required" }`, nothing changes.
- A wrong password counts toward the login lockout (5 tries, 15 minutes); a locked account gets `423`.
- `POST /api/passkey/register/complete` only succeeds for a challenge issued by a confirmed `register/begin`.
- Every success writes an audit entry (`EmailMfaDisabled`, `RecoveryCodesRegenerated`, `PasskeyRegistered`)
  and e-mails the owner.

Contract change: clients that called these endpoints with an empty body must now send a confirmation.
.NET SDK: `DisableEmailMfaAsync`, `RegenerateRecoveryCodesAsync`, `BeginPasskeyRegistrationAsync(..., currentPassword, emailCode)`, `SendConfirmationCodeAsync`.
