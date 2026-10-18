# IAM MCP server — agent-safe user onboarding (task 5800)

IAM exposes a remote MCP (Model Context Protocol) server at **`/mcp`** (streamable-http), built on
the shared Jengo MCP template (`Jengo.Mcp.AspNetCore`, vendored under `src/IAM.API/nuget-local/`).
It lets AI agents onboard and manage users **without ever touching a credential**.

Implementation: `src/IAM.API/Mcp/` (`IamMcpExtensions`, `IamMcpApiKeyValidator`,
`IamMcpToolProvider`), wired in `Program.cs` (`AddIamMcp()` + `MapJengoMcp()`).

## Tool surface (9 tools)

Read-only:

| Tool | Arguments | Returns |
|---|---|---|
| `list_users` | `search?` | max 100 × `{id, email, firstName, lastName, isActive, emailConfirmed}` |
| `get_user` | `email` or `id` | the same fields + `roles: [{name, tenantId}]` |
| `list_roles` | — | `{id, name}` per role |
| `list_tenants` | — | `{id, name}` per tenant |
| `list_invitations` | `tenantId?` | max 100 invitations, newest first |

Mutating (each call is audit-logged with the acting user id, tool name and parameters):

| Tool | Arguments | Effect |
|---|---|---|
| `send_invitation` | `email, tenantId, roleId, expiryDays?` | creates the invitation as the acting user and emails the accept link; returns `{id, email, status, expiresAt, emailSent, acceptUrl}` so the link can be handed over manually when the email failed |
| `revoke_invitation` | `id` | invalidates a pending invitation's accept link |
| `assign_role` | `userId, roleId, tenantId?` | grants a role (optionally tenant-scoped), recorded with `GrantedBy` = acting user |
| `remove_role` | `userId, roleId` | removes the user's assignments of that role |

Extra escalation gate: inviting someone as **SuperAdmin** or assigning the SuperAdmin role is
refused unless the acting user holds SuperAdmin themselves (same rule as the bulk-invite path).

## Why there are deliberately NO password-capable tools

The surface contains **no** `create_user`, `set_password`, `change_password`, `reset_password` or
`mint_token` — and never should:

- **A leaked agent key must not become an account-takeover key.** A tool that sets or resets a
  password would let whoever holds the MCP key take over any account; a token-minting tool would
  let it impersonate anyone. The blast radius of this key is capped at "invite people and manage
  role assignments", every act of which is attributable and reversible.
- **Credentials must never flow through an agent.** With invitations, the invited person proves
  control of their mailbox and chooses their own password on the accept page. No password ever
  exists in an agent conversation, a log line or an MCP response.
- **The audit trail stays honest.** Invitations record `InvitedByUserId` and role grants record
  `GrantedBy`, both set to the acting user bound to the key — identical to the same action done by
  that human in the admin UI.

This exclusion list is pinned by `tests/IAM.API.Tests/Mcp/IamMcpToolProviderTests.cs`: if a
password/token-shaped tool is ever added, that test fails. Remove the tool, don't adjust the test.

## Authentication

`/mcp` has its own self-contained auth scheme (from the template) — independent of IAM's JWT
bearer, the `IAM.Session` cookie and the Hazina `iam_` API keys (the Hazina `X-Api-Key` middleware
explicitly skips `/mcp`, otherwise it would 401 the MCP key before the MCP scheme sees it).

Keys are static and live in configuration, each bound to **one IAM user id**:

```jsonc
"Mcp": {
  "Keys": [
    {
      "Id": "jengo-agent",                  // non-secret label, shown in logs
      "Sha256": "<lowercase hex sha-256 of the raw key>",
      "Subject": "7b0e...-the-iam-user-guid", // the user this key acts as
      "Name": "Jengo",                      // optional, for logs
      "Email": "jengo@example.com"          // optional
    }
  ]
}
```

Clients send the raw key as `X-Api-Key` or as a bearer token (what ChatGPT connectors do). The
subject is re-validated on every call: a key bound to a missing or **deactivated** user can do
nothing. Unauthenticated requests get the RFC 9728 challenge with
`/.well-known/oauth-protected-resource` metadata.

Generate the hash for a new key: `IamMcpKey.Hash("the-raw-key")`, or
`echo -n 'the-raw-key' | sha256sum`.
