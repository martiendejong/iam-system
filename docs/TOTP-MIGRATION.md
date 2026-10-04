# Authenticator-app (TOTP) migration after the SHA-256 upgrade

PR #104 changed TOTP from HMAC-SHA1 to HMAC-SHA256 (`algorithm=SHA256` in the `otpauth://` URI, SHA-256 when checking a
code). An authenticator app that was set up **before** that keeps generating SHA-1 codes, which the server can never
accept again: its owner cannot pass a TOTP check, cannot disable TOTP (that needs a valid code) and cannot start a new
setup ("TOTP is already enabled"). Task 3162 is the migration for those members.

## Decision

Martien, 2026-10-04: do it through an **e-mail PIN**, and make the options **configurable per tenant**.

## What happens now

* `Users.TotpAlgorithm` records what an enrollment was provisioned for. It is set to `SHA256` when a member activates a
  new authenticator app. A member with TOTP enabled and no `SHA256` marker is a **legacy enrollment**
  (`User.HasLegacyTotpEnrollment()`; the same predicate is available as the query `User.LegacyTotpEnrollment`).
* When a legacy member **signs in with their password** (the password has to be correct and the e-mail confirmed), the account
  is moved to the **e-mail PIN two-factor method that already exists** (`TwoFactorMethod.Email`): the stored secret and the
  recovery codes of the authenticator enrollment are removed, an audit row `LegacyTotpMigratedToEmailPin` is written, and the
  normal login-2FA step follows: a PIN is e-mailed and has to be entered before any token is issued. The `/api/auth/login`
  response carries `twoFactorMigration { fromMethod, toMethod, message }` so a client can explain why a PIN is asked; the admin
  UI shows that message.
* Magic-link and OTP sign-ins do **not** migrate: they prove only access to the mailbox, and the PIN goes to that same
  mailbox, so they must not be able to strip an authenticator enrollment. The member is migrated at their next password sign-in.
* **Nothing is mailed in bulk.** A PIN goes out only when a member signs in. There is no "notify everyone" step.
* The member can set up an authenticator app again later (new enrollments are SHA-256).
* If a member's authenticator code passes the SHA-256 check while the marker is still empty (enrolled after PR #104 but
  before the marker existed), the marker is stamped `SHA256` and the member is not treated as legacy.
* A SHA-1 code is never accepted. Detection is purely the marker; no SHA-1 code is generated or checked anywhere.

## Tenant configuration

`OrganizationSettings.LegacyTotpMigration` (per tenant, default `EmailPin`):

| Value | Effect |
|---|---|
| `EmailPin` (0) | Legacy members of the tenant move to e-mail PIN at their next sign-in, as above. |
| `Off` (1) | Their legacy enrollment is left untouched; the tenant handles those members itself. `GET /api/mfa/status` keeps reporting `legacyTotpEnrollment: true` for them. |

* API: `GET`/`PUT /api/organization-settings/{tenantId}` (field `legacyTotpMigration`, `"EmailPin"` or `"Off"`; admins of that
  tenant, SuperAdmin and SystemAdmin only, like the other settings). `GET` also returns `legacyTotpUserCount`: how many
  members of the tenant still hold a legacy enrollment (this is the "identify all users with TOTP" step).
* Admin UI: Invitations > Organization Settings > "Members with an outdated authenticator app".
* A member with roles in several tenants is left alone only when **every** one of those tenants says `Off`; otherwise they are
  migrated. Members without a tenant, and tenants without a settings row, get the default.
* The migration needs a confirmed e-mail address (a password sign-in is refused without one anyway) and an active account;
  otherwise the enrollment is left as it is.

## Deploying

The production schema is created with `EnsureCreated`, so apply the two columns **before** the new build starts (a missing
column fails every `Users` query, which is every login):

```sql
ALTER TABLE "Users" ADD COLUMN "TotpAlgorithm" character varying(16) NULL;
ALTER TABLE "OrganizationSettings" ADD COLUMN "LegacyTotpMigration" integer NOT NULL DEFAULT 0;
```

Both are additive and every default is the intended one. `src/IAM.Infrastructure/Data/Migrations/20261004120000_AddLegacyTotpMigration.cs`
holds the same change for a migrations-based setup.

List the affected members (every TOTP member is listed right after the `ALTER`, as the marker is still empty):

```sql
SELECT "Id", "Email" FROM "Users"
WHERE "TwoFactorEnabled" AND "TwoFactorMethod" = 1 AND ("TotpAlgorithm" IS NULL OR "TotpAlgorithm" <> 'SHA256');
```

If PR #104 was already live in production before this build, members who enrolled after it have a working SHA-256 app but no
marker, and would be switched to e-mail PIN once. That is harmless, but if you want to spare them, mark them before starting the
build. This is an approximation (recovery codes are created at activation, but regenerating them resets the date): when in
doubt skip it, because a wrongly marked legacy member is stuck while an unneeded PIN switch only costs a PIN.

```sql
UPDATE "Users" u SET "TotpAlgorithm" = 'SHA256'
WHERE u."TwoFactorEnabled" AND u."TwoFactorMethod" = 1 AND u."TotpAlgorithm" IS NULL
  AND (SELECT MIN(rc."CreatedAt") FROM "RecoveryCodes" rc WHERE rc."UserId" = u."Id") >= TIMESTAMPTZ '<moment PR #104 went live>';
```

## Not part of this change

* **TOTP is not enforced at sign-in on `develop`.** `AuthService` only challenges `TwoFactorMethod.Email`; a member with an
  authenticator app signs in with the password alone, and `/api/mfa/totp/validate` is a stand-alone endpoint nothing calls during
  login. Open PRs #142 (task 4521) and #67 add that enforcement. Once one of them lands, the legacy migration above has to run
  **before** its TOTP branch (it does here: the call sits right before the e-mail-2FA branch in `LoginAsync`), otherwise every
  legacy member would be locked out of password sign-in. Resolve the `AuthService` conflict by keeping
  `LegacyTotpMigration.TryMigrateToEmailPinAsync` first. Magic-link / OTP sign-in does not migrate, so a legacy member who only
  ever signs in passwordless has to use the password sign-in once (or the password reset) after that PR lands.
* Social login and passkey sign-in do not check a second factor at all (unchanged); a legacy member is migrated at the next
  password sign-in.
