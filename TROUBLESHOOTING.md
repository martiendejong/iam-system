# 🐛 Troubleshooting Guide

> **It's 2am. Authentication is broken. Here's how to fix it in 5 minutes.**

## Quick Diagnosis

Run this first:

```bash
iam debug
```

This will:
- ✅ Check API connectivity
- ✅ Verify configuration
- ✅ Test authentication flow
- ✅ Show recent errors
- ✅ Suggest fixes

**If that doesn't work, continue below.**

---

## Common Errors & Fixes

### 1. "Failed to fetch" / Network Error

**Symptom:**
```
Error: Failed to fetch
at LoginPage.tsx:42
```

**Causes:**
- ❌ API is down
- ❌ CORS misconfiguration
- ❌ Wrong API URL in environment variables

**Fix (2 minutes):**

```bash
# Check API health
curl https://your-domain.iam.dev/health

# If 200 OK → API is fine, CORS issue
# If timeout → API is down
# If 404 → Wrong URL

# Fix CORS (add your frontend domain)
iam config set cors-origins "https://yourapp.com"

# Fix API URL
# .env (React/Vue)
VITE_IAM_DOMAIN=your-domain.iam.dev  # No https://
VITE_IAM_CLIENT_ID=your-client-id

# .env.local (Next.js)
NEXT_PUBLIC_IAM_DOMAIN=your-domain.iam.dev
NEXT_PUBLIC_IAM_CLIENT_ID=your-client-id
```

**Verify:**
```bash
npm run dev  # Should work now
```

---

### 2. "NotAllowedError: The operation is not secure"

**Symptom:**
```
DOMException: The operation is not secure
at PasskeyLogin.tsx:55
```

**Cause:** WebAuthn requires HTTPS

**Fix (1 minute):**

**Local Development:**
```bash
# Option 1: Use localhost (always works)
http://localhost:3000  # ✅ Works

# Option 2: Use ngrok (for testing on phone)
npx ngrok http 3000  # Get https://xxx.ngrok.io URL

# Option 3: Use local HTTPS (Vite)
npm run dev -- --https
```

**Production:**
```bash
# Ensure your site is on HTTPS
# If not, add SSL certificate:

# Vercel/Netlify: Automatic ✅
# Custom server: Use certbot
sudo certbot --nginx -d yourapp.com
```

---

### 3. "Token expired" / 401 Unauthorized

**Symptom:**
```json
{
  "error": "invalid_token",
  "message": "Token has expired"
}
```

**Cause:** Access token expired (15-minute lifetime)

**Fix (30 seconds):**

```typescript
// Our SDK auto-refreshes, but if you're using raw tokens:

// Check token expiry
const decoded = jwt_decode(token);
if (decoded.exp < Date.now() / 1000) {
  // Token expired, refresh it
  const newToken = await iam.refreshToken(refreshToken);
}
```

**Or just reload the page** (SDK will refresh automatically).

**Prevention:**
```typescript
// SDK handles refresh automatically
const { user, isLoading } = useAuth();
// If token expires, SDK refreshes and re-renders
// You don't need to do anything
```

---

### 4. "User not found" After Migration

**Symptom:**
```
Error: User not found
Email: user@example.com
```

**Cause:** User migration incomplete

**Fix (3 minutes):**

```bash
# Check migration status
iam migrate --from auth0 --status

# If "In Progress" → Wait (can take 10min for 10K users)
# If "Failed" → Check error logs
iam migrate --from auth0 --logs

# Re-import specific user
iam migrate --from auth0 --user-email user@example.com

# Force full re-migration (last resort)
iam migrate --from auth0 --force
```

**Verify:**
```bash
# Check user exists
iam users get user@example.com
# Should return user data
```

---

### 5. Passkey Registration Fails

**Symptom:**
```
Error: Failed to create credential
```

**Causes:**
- ❌ Browser doesn't support WebAuthn
- ❌ User canceled biometric prompt
- ❌ User already registered this device

**Fix (2 minutes):**

```typescript
// Check WebAuthn support
if (!window.PublicKeyCredential) {
  console.error('WebAuthn not supported');
  // Show password fallback
  return <PasswordLogin />;
}

// Handle user cancellation
try {
  await registerPasskey();
} catch (error) {
  if (error.name === 'NotAllowedError') {
    // User canceled - show message
    alert('Biometric authentication canceled');
  } else if (error.name === 'InvalidStateError') {
    // Device already registered
    alert('This device is already registered');
  } else {
    // Unknown error
    console.error(error);
  }
}
```

**Verify browser support:**
- Chrome 67+
- Firefox 60+
- Safari 13+
- Edge 18+

---

### 6. "Client ID mismatch"

**Symptom:**
```json
{
  "error": "invalid_client",
  "message": "Client ID does not match"
}
```

**Cause:** Wrong client ID in frontend

**Fix (1 minute):**

```bash
# Get your client ID
iam config get client-id

# Update environment variables
# .env
VITE_IAM_CLIENT_ID=abc123...  # ← Paste client ID here

# Restart dev server
npm run dev
```

---

### 7. Social Login (Google/Microsoft) Fails

**Symptom:**
```
Error: redirect_uri_mismatch
```

**Cause:** OAuth redirect URI not configured

**Fix (3 minutes):**

**Google:**
```bash
# 1. Go to https://console.cloud.google.com/apis/credentials
# 2. Find your OAuth 2.0 Client ID
# 3. Add authorized redirect URI:
https://your-domain.iam.dev/callback/google

# 4. Update IAM config
iam connections update google \
  --client-id YOUR_GOOGLE_CLIENT_ID \
  --client-secret YOUR_GOOGLE_SECRET
```

**Microsoft:**
```bash
# 1. Go to https://portal.azure.com/#view/Microsoft_AAD_RegisteredApps
# 2. Add redirect URI:
https://your-domain.iam.dev/callback/microsoft

# 3. Update IAM config
iam connections update microsoft \
  --client-id YOUR_MS_CLIENT_ID \
  --client-secret YOUR_MS_SECRET
```

---

### 8. "Too many requests" / Rate Limiting

**Symptom:**
```json
{
  "error": "rate_limit_exceeded",
  "retry_after": 60
}
```

**Cause:** Too many login attempts (10/minute default)

**Fix (Immediate):**

```bash
# Increase rate limit (careful - security risk)
iam config set rate-limit-login 100

# Or implement exponential backoff in frontend:
let retryCount = 0;
async function loginWithRetry() {
  try {
    await login();
  } catch (error) {
    if (error.status === 429) {
      const delay = Math.pow(2, retryCount) * 1000;
      await sleep(delay);
      retryCount++;
      return loginWithRetry();
    }
  }
}
```

---

### 9. Session Not Persisting (User logged out on refresh)

**Symptom:** User logs in → refresh page → logged out again

**Cause:** Session storage issue

**Fix (1 minute):**

```typescript
// Check storage
console.log(localStorage.getItem('iam_access_token'));
// If null → storage not working

// Fix (use sessionStorage as fallback)
iam.configure({
  storage: {
    get: (key) => sessionStorage.getItem(key) || localStorage.getItem(key),
    set: (key, value) => {
      try {
        localStorage.setItem(key, value);
      } catch {
        sessionStorage.setItem(key, value);  // Fallback
      }
    },
  },
});
```

---

### 10. WebAuthn Works Locally But Not in Production

**Symptom:** Passkeys work on localhost but fail on production domain

**Cause:** Relying Party (RP) ID mismatch

**Fix (2 minutes):**

```bash
# Check RP ID configuration
iam config get rp-id

# Should match your domain (no subdomain)
# ✅ Correct: yourapp.com
# ❌ Wrong: www.yourapp.com
# ❌ Wrong: auth.yourapp.com

# Fix
iam config set rp-id yourapp.com

# Restart API
iam restart
```

**Also check allowed origins:**
```bash
iam config set allowed-origins \
  "https://yourapp.com,https://www.yourapp.com"
```

---

## Advanced Debugging

### Enable Debug Mode

```bash
# Enable verbose logging
iam config set log-level debug

# Check logs
iam logs --tail 100
```

### Test Authentication Flow Manually

```bash
# Test passkey registration
iam test passkey-register user@example.com

# Test passkey authentication
iam test passkey-login user@example.com

# Test token validation
iam test token YOUR_ACCESS_TOKEN
```

### Check Database Connection

```bash
# Test database connectivity
iam db test

# Check database migrations
iam db status

# Force migration (if needed)
iam db migrate --force
```

---

## Production Issues

### High Latency (Slow Logins)

**Symptom:** Login takes >2 seconds

**Diagnosis:**
```bash
# Check API response time
curl -w "@curl-format.txt" https://your-domain.iam.dev/health

# Check database query performance
iam db slow-queries
```

**Fixes:**
```bash
# Add database indexes (if missing)
iam db optimize

# Enable Redis caching
iam config set cache-enabled true
iam config set redis-url redis://localhost:6379

# Scale horizontally (multiple API instances)
iam scale --instances 3
```

**Expected latency:**
- Passkey registration: <500ms
- Passkey authentication: <100ms
- Token refresh: <50ms

---

### Memory Leaks

**Symptom:** API memory usage grows over time

**Diagnosis:**
```bash
# Check memory usage
iam status --metrics

# If >2GB → leak suspected
```

**Fix:**
```bash
# Restart API (immediate)
iam restart

# Enable memory profiling
iam config set memory-profiling true
iam logs --memory

# Report to us with logs
iam report --issue memory-leak --attach-logs
```

---

### Database Locked Errors (SQLite)

**Symptom:**
```
SQLite Error: database is locked
```

**Cause:** SQLite doesn't handle high concurrency well

**Fix (Upgrade to PostgreSQL):**
```bash
# Export data from SQLite
iam db export --format sql > backup.sql

# Install PostgreSQL
sudo apt install postgresql

# Create database
sudo -u postgres createdb iam_system

# Configure IAM to use PostgreSQL
iam config set database postgresql://localhost/iam_system

# Import data
iam db import < backup.sql

# Test
iam db test  # Should succeed
```

**PostgreSQL can handle 10,000+ req/sec (SQLite: ~100 req/sec)**

---

## Browser-Specific Issues

### Safari: "WebAuthn not available"

**Fix:**
```bash
# Check Safari version (need 13+)
# Check Settings → Safari → Advanced → Experimental Features
# Enable "Web Authentication"
```

### Firefox: Passkeys don't sync

**Note:** Firefox doesn't sync passkeys (Chrome/Safari do)
**Workaround:** Use hardware security key (YubiKey) instead

### Chrome: "Registration failed with error -65534"

**Cause:** USB security key issue
**Fix:**
```bash
# Try internal authenticator instead
await navigator.credentials.create({
  publicKey: {
    authenticatorSelection: {
      authenticatorAttachment: "platform",  # ← Force internal
    },
  },
});
```

---

## Still Stuck?

### Get Help (< 2 Hour Response Time)

**Option 1: AI Assistant**
```bash
iam debug --ai
# GPT-4 analyzes your logs and suggests fixes
```

**Option 2: Discord Community**
- [Join Discord](https://discord.gg/iam-system)
- Post in #help channel
- Usually answered in <30 minutes

**Option 3: Emergency Support (Production Down)**
- Email: [emergency@iam.dev](mailto:emergency@iam.dev)
- Response time: <1 hour (24/7)
- We'll jump on a call and fix it live

**Option 4: GitHub Issues**
- [github.com/martiendejong/iam-system/issues](https://github.com/martiendejong/iam-system/issues)
- Attach logs: `iam logs --export issue-logs.txt`
- Attach config (sanitized): `iam config export --sanitize`

---

## Preventive Measures

### Enable Health Checks

```bash
# Set up monitoring
iam monitor enable --endpoint https://your-domain.iam.dev/health

# Get alerts
iam monitor alert --email devops@yourcompany.com
iam monitor alert --slack https://hooks.slack.com/services/YOUR/WEBHOOK/URL
```

### Automated Testing

```bash
# Run test suite
iam test all

# Add to CI/CD
# .github/workflows/test.yml
- name: Test IAM
  run: npx iam test all
```

### Backup & Recovery

```bash
# Automated daily backups
iam backup enable --schedule "0 2 * * *"  # 2am daily

# Test recovery
iam backup test-restore

# Manual backup (before risky changes)
iam backup create --tag "before-migration"
```

---

## Error Codes Reference

| Code | Meaning | Fix |
|------|---------|-----|
| `invalid_token` | Token expired/invalid | Refresh token or re-login |
| `invalid_client` | Wrong client ID | Check VITE_IAM_CLIENT_ID |
| `invalid_grant` | Refresh token expired | Re-login |
| `rate_limit_exceeded` | Too many requests | Wait or increase limit |
| `user_not_found` | User doesn't exist | Check migration or register |
| `credential_not_found` | Passkey not registered | Register passkey first |
| `invalid_challenge` | Challenge expired | Retry registration/login |
| `cors_error` | CORS misconfigured | Add origin to allowed list |
| `database_error` | Database connection failed | Check DB status |
| `internal_error` | Something broke | Check logs, contact support |

---

## Useful Commands

```bash
# Quick health check
iam status

# View recent errors
iam logs --level error --tail 50

# Test everything
iam test all

# Export config (for support)
iam config export --sanitize > config.json

# Reset to defaults (last resort)
iam reset --confirm
```

---

**Remember:** Most issues are fixed in <5 minutes with `iam debug`.

**Still broken?** We're here: [support@iam.dev](mailto:support@iam.dev) 💪
