# Security Policy

## 🔒 Our Commitment

Authentication security is non-negotiable. We take security seriously and appreciate the security community's help in keeping IAM System safe.

**Our promise:**
- Respond to security reports within 24 hours
- Fix critical vulnerabilities within 72 hours
- Pay bounties up to $10,000 for valid reports
- Give credit where credit is due (unless you prefer anonymity)

## 🐛 Bug Bounty Program

### Scope

**In scope:**
- Authentication bypass
- Authorization bypass
- SQL injection
- XSS (Cross-Site Scripting)
- CSRF (Cross-Site Request Forgery)
- SSRF (Server-Side Request Forgery)
- RCE (Remote Code Execution)
- Privilege escalation
- Cryptographic vulnerabilities
- Session hijacking
- Token forgery/theft
- Passkey/WebAuthn vulnerabilities
- OAuth2/OIDC implementation flaws
- Data leaks (PII, credentials, tokens)

**Out of scope:**
- Social engineering
- Physical attacks
- DoS/DDoS
- Spam
- Issues in third-party dependencies (report to them first)
- Issues requiring unlikely user interaction
- Issues in outdated versions

### Rewards

| Severity | Reward | Example |
|----------|--------|---------|
| **Critical** | $5,000 - $10,000 | Authentication bypass, RCE, massive data leak |
| **High** | $2,000 - $5,000 | Authorization bypass, SQL injection, XSS leading to account takeover |
| **Medium** | $500 - $2,000 | CSRF, non-critical XSS, information disclosure |
| **Low** | $100 - $500 | Security misconfiguration, minor information leak |

**Bonuses:**
- +50% for providing a working patch
- +25% for exceptional write-up
- +25% for first report from a new researcher

**Payment methods:**
- PayPal
- Bank transfer
- Cryptocurrency (BTC, ETH)
- Donation to charity of your choice

### Rules

**To qualify for a bounty:**
1. Be the first to report the vulnerability
2. Provide sufficient detail to reproduce
3. Allow us reasonable time to fix before public disclosure
4. Don't exploit the vulnerability beyond proof-of-concept
5. Don't access, modify, or delete user data
6. Don't perform DoS/DDoS attacks
7. Don't publicly disclose before we've patched

**What we promise:**
- Acknowledge your report within 24 hours
- Keep you updated on fix progress
- Credit you in our security advisories (unless you prefer anonymity)
- Never pursue legal action for good-faith research
- Pay bounties within 14 days of fix deployment

---

## 🚨 Reporting a Vulnerability

### DO NOT create a public GitHub issue for security vulnerabilities.

Instead, report privately to: **[security@iam.dev](mailto:security@iam.dev)**

### What to Include

**Minimum information:**
- Description of the vulnerability
- Steps to reproduce
- Potential impact
- Your suggested severity (Critical/High/Medium/Low)

**Optional but helpful:**
- Proof-of-concept code
- Screenshots/videos
- Suggested fix
- References to similar vulnerabilities

**Template:**
```markdown
# Vulnerability Report

## Summary
Brief description of the vulnerability

## Severity
[Critical / High / Medium / Low]

## Description
Detailed explanation of what the vulnerability is

## Steps to Reproduce
1. Go to X
2. Do Y
3. Observe Z

## Impact
What an attacker could do with this vulnerability

## Proof of Concept
[Code, screenshots, or video]

## Suggested Fix
[If you have one]

## Your Details
Name: [Your name or pseudonym]
Email: [For communication and bounty payment]
Preferred Credit: [Full name / Pseudonym / Anonymous]
```

### Example Report (Fictional)

```markdown
# Vulnerability Report

## Summary
SQL Injection in user search endpoint

## Severity
High

## Description
The `/api/users/search` endpoint doesn't properly sanitize the `query` parameter, allowing SQL injection.

## Steps to Reproduce
1. Send POST to `/api/users/search`
2. Payload: `{"query": "a' OR '1'='1"}`
3. Observe all users returned instead of filtered results

## Impact
An attacker could:
- Dump entire user database
- Modify user data
- Escalate privileges

## Proof of Concept
```bash
curl -X POST https://api.iam.dev/api/users/search \
  -H "Content-Type: application/json" \
  -d '{"query": "a'\'' OR '\''1'\''='\''1"}'
```

## Suggested Fix
Use parameterized queries:
```csharp
// Before (vulnerable)
var sql = $"SELECT * FROM Users WHERE Name LIKE '%{query}%'";

// After (secure)
var users = await _dbContext.Users
    .Where(u => EF.Functions.Like(u.Name, $"%{query}%"))
    .ToListAsync();
```

## Your Details
Name: Alice Security
Email: alice@security-research.com
Preferred Credit: Alice Security (@alice_sec on Twitter)
```

---

## 📅 Supported Versions

| Version | Supported |
|---------|-----------|
| Latest (develop branch) | ✅ |
| Last stable release | ✅ |
| Older releases | ❌ |

**We only fix vulnerabilities in:**
- Current development branch (`develop`)
- Latest stable release

**Recommendation:** Always use the latest version.

---

## 🛡️ Security Best Practices for Users

### For Developers Using IAM System

**1. Use HTTPS in production**
```bash
# ❌ Never in production
http://yourapp.com

# ✅ Always use HTTPS
https://yourapp.com
```

**2. Store secrets securely**
```bash
# ❌ Don't commit secrets
git add .env

# ✅ Use environment variables
export IAM_CLIENT_SECRET=xxx
```

**3. Validate tokens on every request**
```csharp
// ✅ Always validate
[Authorize]
public async Task<IActionResult> GetProfile()
{
    var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
    // ...
}
```

**4. Use short-lived tokens**
```typescript
// ✅ Refresh tokens regularly
const { token, expiresAt } = await auth.refreshToken();
```

**5. Enable MFA for admin accounts**
```bash
iam config set require-mfa-for-admins true
```

### For Self-Hosted Deployments

**1. Keep software updated**
```bash
# Check for updates weekly
iam update check

# Auto-update (recommended)
iam config set auto-update true
```

**2. Use strong database passwords**
```bash
# ❌ Weak
postgresql://postgres:password@localhost/iam

# ✅ Strong
postgresql://postgres:$(openssl rand -base64 32)@localhost/iam
```

**3. Enable audit logging**
```bash
iam config set audit-log enabled
iam config set audit-log-retention 90  # days
```

**4. Restrict database access**
```bash
# PostgreSQL: Only allow localhost
# /etc/postgresql/16/main/pg_hba.conf
local   iam_system      iam_user                        md5
host    iam_system      iam_user        127.0.0.1/32    md5
```

**5. Use reverse proxy (nginx/Caddy)**
```nginx
# nginx configuration
server {
    listen 443 ssl http2;
    server_name auth.yourcompany.com;

    ssl_certificate /path/to/cert.pem;
    ssl_certificate_key /path/to/key.pem;

    location / {
        proxy_pass http://localhost:5161;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
    }
}
```

---

## 🔍 Security Audits

### External Audits

We conduct external penetration tests every 6 months:

| Date | Auditor | Scope | Findings | Status |
|------|---------|-------|----------|--------|
| 2026-Q2 | [Pending] | Full platform | TBD | Scheduled |
| 2026-Q4 | [Pending] | Full platform | TBD | Planned |

**Audit reports:** Published 30 days after fixes are deployed.

### Internal Security Reviews

**Code review checklist:**
- [ ] Input validation on all endpoints
- [ ] Parameterized SQL queries (no string concatenation)
- [ ] CSRF tokens on state-changing operations
- [ ] Rate limiting on authentication endpoints
- [ ] Proper error messages (no sensitive info leaks)
- [ ] Secure random number generation for tokens
- [ ] Constant-time comparisons for secrets
- [ ] HTTPS-only cookies with Secure + HttpOnly flags

---

## 📜 Security Advisories

**Published vulnerabilities:** [GitHub Security Advisories](https://github.com/martiendejong/iam-system/security/advisories)

**Subscribe to alerts:**
```bash
# Watch repository → Custom → Security alerts
```

### Past Vulnerabilities (None Yet)

We'll publish all security advisories here, including:
- CVE ID
- Severity
- Affected versions
- Description
- Fix version
- Credit to reporter

**Example format:**
```markdown
## [CVE-2026-XXXX] SQL Injection in User Search

**Severity:** High (CVSS 8.5)
**Affected versions:** < 1.2.0
**Fixed in:** 1.2.0
**Reported by:** Alice Security (@alice_sec)

### Description
SQL injection vulnerability in `/api/users/search` endpoint...

### Fix
Upgrade to version 1.2.0 or apply patch: [link]

### Credit
Thanks to Alice Security for responsible disclosure.
```

---

## 🤝 Hall of Fame

Security researchers who've helped make IAM System more secure:

| Researcher | Vulnerabilities Found | Bounty Earned |
|------------|----------------------|---------------|
| *No reports yet* | - | - |

**Want to be on this list?** Find a vulnerability and report it responsibly!

---

## 📖 Security Resources

### Our Security Measures

**Authentication:**
- Passkeys (WebAuthn/FIDO2) - Phishing-resistant
- OAuth2 / OpenID Connect - Industry standards
- JWT with short expiry (15 minutes)
- Refresh tokens with rotation
- Rate limiting (10 attempts/minute)

**Authorization:**
- Role-Based Access Control (RBAC)
- Principle of least privilege
- Permission checks on every request
- Audit trail for all actions

**Data Protection:**
- Passwords hashed with Argon2id
- Tokens encrypted at rest (AES-256)
- TLS 1.3 for all connections
- Database encryption
- Sensitive data redacted from logs

**Infrastructure:**
- Regular security updates
- Automated dependency scanning (Dependabot)
- Container scanning (Snyk)
- SAST (Static Application Security Testing)
- DAST (Dynamic Application Security Testing)

### Industry Standards We Follow

- [OWASP Top 10](https://owasp.org/www-project-top-ten/)
- [OWASP ASVS](https://owasp.org/www-project-application-security-verification-standard/)
- [NIST Cybersecurity Framework](https://www.nist.gov/cyberframework)
- [CIS Controls](https://www.cisecurity.org/controls)
- [OAuth 2.0 Security Best Current Practice](https://datatracker.ietf.org/doc/html/draft-ietf-oauth-security-topics)
- [WebAuthn Security Considerations](https://www.w3.org/TR/webauthn-2/#sctn-security-considerations)

### Security Reading List

**For Developers:**
- [OWASP Cheat Sheet Series](https://cheatsheetseries.owasp.org/)
- [Web Application Security Guide](https://owasp.org/www-project-web-security-testing-guide/)
- [OAuth 2.0 Threat Model](https://datatracker.ietf.org/doc/html/rfc6819)
- [WebAuthn Guide](https://webauthn.guide/)

**For Security Researchers:**
- [Bug Bounty Playbook](https://www.bugbountyplaybook.com/)
- [HackerOne Resources](https://www.hackerone.com/resources)
- [PortSwigger Web Security Academy](https://portswigger.net/web-security)

---

## 📞 Contact

**Security reports:** [security@iam.dev](mailto:security@iam.dev)
**PGP key:** [Download public key](https://iam.dev/security-pgp-key.asc)
**Bug bounty questions:** [bounty@iam.dev](mailto:bounty@iam.dev)
**General security inquiries:** [security-general@iam.dev](mailto:security-general@iam.dev)

---

## 🔐 Responsible Disclosure Timeline

**Ideal timeline:**
- **Day 0:** Vulnerability reported
- **Day 1:** Acknowledgment sent (24 hours SLA)
- **Day 3:** Fix in development
- **Day 7:** Fix deployed to production
- **Day 14:** Bounty paid
- **Day 30:** Public disclosure + credit

**If fix takes longer:**
- We'll keep you updated weekly
- You can request early public disclosure if critical
- We may request delayed disclosure if fix is complex

**Coordinated disclosure benefits everyone:**
- We get time to fix
- Users stay safe
- You get credit + bounty

---

## ⚖️ Legal Safe Harbor

IAM System endorses responsible security research and will not pursue legal action against researchers who:

1. Make a good faith effort to avoid harm
2. Don't access, modify, or delete user data beyond PoC
3. Don't perform DoS/DDoS attacks
4. Allow reasonable time to fix before public disclosure
5. Follow this security policy

**This applies even if:**
- You inadvertently violated a law (e.g., CFAA)
- You accessed systems without authorization (for research)
- You bypassed technical security measures (for research)

**We will defend your right to research** and will not assist prosecution of good-faith researchers.

---

**Thanks for keeping IAM System secure!** 🙏

*Together, we're building the most secure open-source IAM solution.* 🔒
