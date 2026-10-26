# AUTH-00: secure, simple access (trusted browsers + shielded authenticator)

Canonical task: JengoWork 5097 (board 44). Status of this folder: **proposal for Martien's confirmation**; nothing
here switches production authentication, changes a real account or moves a real secret.

## One-paragraph result

Keep today's IAM as the identity provider and add (1) **trusted browsers** in IAM - a named registration that owns a
refresh-token family, with a duration choice, family-level reuse detection and revocation that reaches live
requests within 60 s - and (2) a **fresh-confirmation authenticator** in Password Manager - a TOTP code is released
only against a one-time 30 s grant that a user-verified WebAuthn assertion mints, verified by the same service that
holds the encrypted seeds so that compromising IAM cannot release codes. Bitwarden is not used as the backend
(no supported server-side TOTP API). The vault runs the confirmation on its own origin because a passkey belongs to
one RP ID. Seeds are AES-256-GCM envelope-encrypted; the KEK lives in a DPAPI-protected file outside the database
and backups, with an offline recovery package held by Martien.

## Documents

| Document | Content |
|----------|---------|
| [ADR-0001](ADR-0001-trusted-browsers-and-fresh-confirmation.md) | Decisions D1-D10, options considered, deviations from the plan, rollback, what Martien must confirm |
| [inventory.md](inventory.md) | What the code does today (IAM, Password Manager, Jengo web, Lango, Bitwarden), with evidence, plus the duplicate scan of all boards |
| [threat-model.md](threat-model.md) | 23 threats mapped to mitigations, test evidence and accepted residual risk |
| [compatibility-matrix.md](compatibility-matrix.md) | What is verified vs expected vs unknown per platform capability; inputs needed from Martien |
| [key-custody-decision.md](key-custody-decision.md) | Written key custody decision, options, recovery package, rotation, host prerequisites |
| [estimate.md](estimate.md) | Hours and tokens per package, calibrated on this task |

## Proof of concept

`martiendejong/passwordmanager`, branch `feat/5097-auth-00-authenticator-adr` (`docs/authenticator/POC.md`): 92 tests
against a real ES256 virtual authenticator - cookie/session-only refusal, UV=false, wrong origin / RP ID, wrong
tenant / item / session, expiry, replay, concurrency, agent and API-key refusal, seed tamper and relocation, RFC 6238
vectors - mutation-checked. Fictional data, in-memory stores, feature flag off.

## Follow-up work packages (created in `planned`, not active; dependencies are written in each description)

| Package | Task | Board | Depends on |
|---------|------|-------|-----------|
| AUTH-01 Trusted browsers | 5102 | 44 IAM | 5097 confirmed; **5053** (hard); 5000 (deploy gate) |
| AUTH-02 Fresh device confirmation | 5103 | 12 Password Manager | 5097 confirmed; coordinate 498 |
| AUTH-03 TOTP items + restricted vault adapter | 5104 | 12 Password Manager | 5097 + key custody confirmed; 5103 |
| AUTH-04a Authenticator UI + accessibility | 5105 | 12 Password Manager | 5103, 5104 |
| AUTH-04b Trusted-browser overview | 5106 | 44 IAM | 5102 |
| AUTH-05 Recovery, security test, pilot | 5107 | 44 IAM | 5102-5106, separate reviewer |

AUTH-04 from the plan is split in two because the browser overview lives in IAM and the authenticator UI in the
vault, so each task stays inside one repo. The JengoWork API has no dependency endpoint (it answers 405), so the
dependencies exist as explicit text in the task descriptions.

## What is not done, on purpose

No real seed, no real account change, no production authentication switch, no deploy. Real Android/desktop proof,
durable stores, rate limiting, passkey-enrolment step-up, the DPAPI provider, trusted-browser revocation, UI and
recovery drills are acceptance lines of AUTH-01..05.
