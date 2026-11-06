# IAM production secrets & deploy (task 5801)

Since 2026-10-18 the production IAM (`IAMSystem` Windows service, `C:\Services\IAM`
on the dev server) reads **all secrets from service-scoped environment variables**;
`appsettings.Production.json` contains no secret values. Every secret also lives in
the Jengo vault (project 6) as the source of truth.

## Where the secrets live

The Windows Service Control Manager passes per-service environment variables from
the registry value `HKLM\SYSTEM\CurrentControlSet\Services\IAMSystem\Environment`
(REG_MULTI_SZ, one `NAME=value` per line). ASP.NET Core's configuration reads them
with the standard `__` section separator, overriding anything in the JSON files.

| Environment variable | Vault (project 6) | Notes |
|---|---|---|
| `ConnectionStrings__DefaultConnection` | 337 (full string) / 51 (iam_user password) | rotated 2026-10-18 |
| `Jwt__SecretKey` | 341 | admin-UI JWT signing; rotated 2026-10-18 (rotation signs out all admin-UI sessions) |
| `Email__SmtpPassword` | 338 | noreply@prospergenics.com via mail.prospergenics.com (zxcs shared hosting). NOT rotated yet: rotation happens in the external zxcs/DirectAdmin panel and must be coordinated with every other sender using this mailbox. |
| `SecretsVault__MasterKey` | 342 | NOT rotated: rotating requires re-encrypting all stored vault rows (follow-up). |
| `OpenIddict__SigningCertificatePassword` | 343 | protects signing.pfx on disk |
| `OpenIddict__EncryptionCertificatePassword` | 344 | protects the encryption pfx; rotating either means re-protecting the pfx files |
| `Clients__JengoVpsMcp__Secret` | 339 | rotating breaks that client until it is updated too |
| `Clients__OpenWebUi__Secret` | 340 | idem |
| `Ca__CertificatePassword` | 345 | CA root key password, freshly generated 2026-10-18 (task 5165 startup check); no ca.pfx existed, so the CA is created with this password on first use |

## Updating a secret

1. Change the value in the vault entry (source of truth).
2. Update the matching line in the service `Environment` registry value
   (PowerShell `Set-ItemProperty ... -Type MultiString` with the **full** list —
   partial writes replace the whole value).
3. `sc stop IAMSystem && sc start IAMSystem` and run the smoke checks below.

## Deploying

Use `deploy-iam.py` in the repo root. It builds (admin-ui + `dotnet publish` with
`HAZINA_ROOT`), makes a server-side backup **after stopping the service**, uploads
everything except `appsettings*.json`, restarts, and smoke-checks. Key rules it
enforces (learned 18-10, see JengoWork 5814/5801):

- `appsettings.Production.json` is never uploaded or overwritten; it must stay
  free of secrets. Deploy artifacts and backups must not reintroduce them.
- Before deploying, diff startup-config requirements: new `InvalidOperationException`
  startup checks on develop (e.g. `Ca:CertificatePassword`, task 5165) need their
  env var present BEFORE the restart, or SSO goes down.
- Smoke after start: `/auth/` 200 · `/auth/api/users/me` 401 ·
  `/auth/.well-known/openid-configuration` contains `end_session_endpoint` ·
  calendar login redirect 302 to `/connect/authorize`.

## Rotation status (2026-10-18)

- ROTATED: DB password (iam_user), Jwt SecretKey. CA password was newly generated
  the same day (never used before).
- DEFERRED, tracked on JengoWork 5801: SMTP password (external zxcs panel +
  cross-system senders), OpenIddict pfx passwords (requires re-protecting the pfx
  files), OIDC client secrets (requires coordinated update on the client side),
  SecretsVault MasterKey (requires data re-encryption).
