# API keys

IAM authenticates `X-Api-Key` through the shared **Hazina.Security.ApiKeys** middleware (the same module the other Jengo
apps use), not through private code. IAM is the *authority*: its `ApiKeys` table is the source of truth for every key it
issues, and other apps can validate keys against it (see "Introspection").

## Model

| | |
|---|---|
| Header | `X-Api-Key: iam_ab12_...` (no header: JWT/cookie auth carries on; bad key: 401, no fallback) |
| Storage | The database holds only the SHA-256 **hash** and a non-secret prefix. The raw key is shown once at creation and archived in Vault (`VaultReference` points at the credential). |
| Scope | `read` < `write` < `admin` (`admin` implies `write` implies `read`). Enforce with `[Authorize(Policy = HazinaApiKeyPolicies.Read/Write/Admin)]`. |
| Tenant | A key with a `TenantId` reaches only that tenant; a key without one is platform-wide and reaches tenant-addressed data only with `admin` scope. A request that names another tenant (query `tenantId`, `X-Tenant-Id`, route value) is a 403. |
| Rate limit | Per key, `Microsoft.AspNetCore.RateLimiting`: the key's `RateLimitPerMinute`, else `RateLimiting:AuthenticatedLimitPerMinute`. 429 + `Retry-After`. |
| Audit | One line per API-key request on the log category `Hazina.Security.ApiKeys.Audit`: outcome, key prefix, key id, tenant, scope, method, route pattern, status, timestamp, IP. Never the raw key. |

Claims on the principal are unchanged from before (`api_key_id`, `api_key_name`, `api_key_prefix`, `auth_method=api_key`,
`tenant_id`, `sub`, `permission`, email/name for user-owned keys) plus `api_key_scope` and `api_key_platform`.

## Issuing keys

`POST /api/api-keys` takes an optional `scope` (default `read`). A key can never be minted with more power than its
caller: an API-key caller may only issue up to its own scope, inside its own tenant; `admin` scope needs a
SuperAdmin/SystemAdmin user.

Keys that existed before scopes were introduced were backfilled to `write` (what any key could already do). Nothing is
backfilled to `admin`.

The app-role catalog endpoints (`/api/app-roles/*`) are global / cross-tenant: they take **platform-wide** keys
(issued without a tenant); a tenant-scoped key gets 403.

## Introspection (other apps validating against IAM)

`POST /api/api-keys/introspect` with `{ "keyHash": "<sha256 hex of the presented key>" }` returns the key's scope, tenant,
expiry and `active` flag (404 = unknown). Only platform-wide `admin` keys may call it. Consumers wire it with
`AddHazinaApiKeyAuth(...).UseHttpLookup(...)`: validation stays local (hash + in-memory cache, default 2 minutes), so a
revocation reaches them within minutes and an IAM outage does not take them down (stale-if-error, default 15 minutes).

## Configuration

```jsonc
"ApiKeys": {
  "Vault": {                       // where raw keys are archived (Prospergenics vault REST API)
    "BaseUrl": "https://<vault host>/",
    "ProjectId": 0,                // vault project the keys are filed under
    "NamePrefix": "iam-api-key"
    // "ApiKey": set through the environment (ApiKeys__Vault__ApiKey), never in a file
  },
  "Cache": { "CachePositiveTtl": "00:02:00", "CacheMaxStaleOnError": "00:15:00" }
}
```

Without `ApiKeys:Vault:BaseUrl`, existing keys keep validating but issuing or rotating a key **fails closed** (no raw key is
ever stored nowhere). Only `Development` falls back to an in-memory vault.

## Building

`IAM.Infrastructure` references the Hazina module by project. `Directory.Build.props` resolves the Hazina checkout as the
sibling of this repo (`..\hazina\`, i.e. `C:\projects\hazina`); override with `-p:HazinaRoot=...` or the `HAZINA_ROOT`
environment variable (the path must end with a directory separator). CI checks Hazina out next to the build.

## Deploying this change

The EF migration `AddApiKeyScopeAndVaultReference` adds two columns to `ApiKeys`. IAM creates its schema with
`EnsureCreated` (a no-op on an existing database), so apply the migration's SQL to `iam_db` **before** starting the new
build, otherwise every API-key lookup fails:

```sql
ALTER TABLE "ApiKeys" ADD COLUMN "Scope" character varying(16) NOT NULL DEFAULT 'write';
ALTER TABLE "ApiKeys" ADD COLUMN "VaultReference" character varying(64) NULL;
```

Then set `ApiKeys__Vault__BaseUrl`, `ApiKeys__Vault__ProjectId` and `ApiKeys__Vault__ApiKey` on the service.
