# AUTH-00 estimate

Task 5097. Ranges, not promises. Two scales are given because "hours" means different things for an agent and
for a person.

**Calibration (measured, this task).** AUTH-00 itself - inventory across 4 repos and the task boards, a 5-file
PoC with 92 tests (mutation-checked), 6 documents, 2 PRs - took about 0.5 hours of active agent time and roughly
0.5M tokens, with no reviews, deploys or device tests in it. Later packages add exactly those things, so the agent
numbers below are scaled from this by scope, not extrapolated linearly. "Engineer-equivalent" is the conventional
estimate for a person doing the same work, shown so the plan can be compared with normal planning.

| Package | Owner (per ADR) | Engineer-equivalent | Agent tokens | Agent active time | Main cost drivers | Largest uncertainty |
|---------|-----------------|--------------------:|-------------:|------------------:|-------------------|---------------------|
| AUTH-01 Trusted browsers | IAM, plus Jengo web and PM session changes | 32-48 h | 1.5-2.5 M | 2-4 h | Browser registration + token family + migration; `bid` checks in `JwtBearer` and cookie; shared complete-login for all 5 login methods; SSO propagation to apps with their own sessions; removing Jengo's `localStorage` JWT; revocation and reuse tests | Three session systems must change together; depends on 5053 and the develop deploy (5000) |
| AUTH-02 Fresh confirmation | Password Manager (ADR D1; IAM supplies claims) | 20-32 h | 0.8-1.4 M | 1-2 h | Durable challenge/grant store with conditional-update consume, enrolment/removal step-up, AAGUID + BE/BS capture, rate limits, vault-origin window | Real Android + desktop proof needs Martien's devices and time |
| AUTH-03 TOTP + vault adapter | Password Manager | 24-36 h | 0.9-1.5 M | 1-3 h | DPAPI KEK provider, EF entities + startup migration, import (QR decoded locally in the browser, manual otpauth), encrypted backup format + restore test, per-item AAD | Vault host capabilities (DPAPI, ACL, backup scope) cannot be checked from here |
| AUTH-04 UI and accessibility | Password Manager + IAM | 28-44 h | 1.2-2.0 M | 2-4 h | Devices overview, authenticator list, show/copy/expiry, clipboard fallback, WCAG 2.2 AA checks (keyboard, screen reader, zoom, targets), Playwright flows | Clipboard and popup behaviour per browser; screen-reader testing needs a human pass |
| AUTH-05 Recovery, security test, pilot | IAM + reviewer | 20-32 h | 0.7-1.2 M | 1-2 h | Restore test in an isolated environment, lost-device drill, CSRF/XSS hardening, race/replay and tenant tests, agent-bypass tests, separate reviewer | Reviewer availability; pilot acceptance timing |
| **Total** | | **124-192 h** | **5.1-8.6 M** | **7-15 h** | | |

**People time (not agent time).**

| Who | What | Estimate |
|-----|------|----------|
| Martien | Confirm 3 ADR points, name recovery-package holders, enrol 2 authenticators, enrol one GitHub test account, device checks (Android + desktop), pilot acceptance over about a week | 6-9 h in total, spread out |
| Separate reviewer | Security review of AUTH-02/03/05 evidence, a11y pass | 6-10 h (not yet assigned; nobody is assumed) |

**Calendar.** Roughly 4-6 weeks if reviews are prompt: AUTH-01 and AUTH-02 in parallel after Martien confirms the
ADR, then AUTH-03 and AUTH-04, then AUTH-05. Calendar time is dominated by review, the IAM deploy gate (task 5000),
device proofs and pilot acceptance, not by coding.

**Not in the estimate.** Moving Jengo web and Password Manager onto IAM-issued tokens (cleaner end state, D3);
the out-of-process seed service (key custody decision, point 3); a client-side/E2EE design (task 499); related
origin requests in Jengo's own page (D2); fixing the live IAM `Fido2` origin configuration (I9, small, separate).

**Assumptions.** The pilot is one account; no mass migration; no new standalone app, push infrastructure or voice
unlock (plan); dependencies land in the order of the ADR; the vault host allows DPAPI and a restricted key
folder (to be proven in AUTH-03, if not, option A in the key custody decision is the fallback with its stated
weakness).
