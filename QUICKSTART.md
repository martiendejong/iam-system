# ⚡ Quick Start: Authentication in 60 Seconds

> **From zero to authenticated in one minute.**

## What You'll Build

By the end of this guide (60 seconds), you'll have:
- ✅ A working login page with Face ID/Touch ID
- ✅ Protected routes in your app
- ✅ User profile access
- ✅ Logout functionality

**No configuration. No complexity. Just works.**

---

## Step 1: Install (15 seconds)

```bash
npx iam init
```

That's it. The CLI auto-detects your framework and sets everything up.

**What just happened:**
- ✅ Installed `@iam-system/sdk` (React/Vue/Angular/Next.js)
- ✅ Generated authentication code for your framework
- ✅ Created config file with your credentials
- ✅ Added environment variables

---

## Step 2: See It Work (30 seconds)

```bash
npm run dev
```

Visit `http://localhost:3000/login`

**Click "Login with Passkey"** → Face ID/Touch ID prompt → You're in. ✅

**That's it.** Authentication works.

---

## Step 3: Understand What You Got (15 seconds)

### React Example

The CLI generated this for you:

```tsx
// src/auth/AuthProvider.tsx (auto-generated)
import { IamProvider } from '@iam-system/react';

export function AuthProvider({ children }) {
  return (
    <IamProvider
      domain={process.env.VITE_IAM_DOMAIN}
      clientId={process.env.VITE_IAM_CLIENT_ID}
    >
      {children}
    </IamProvider>
  );
}
```

```tsx
// src/pages/LoginPage.tsx (auto-generated)
import { useAuth } from '@iam-system/react';

export function LoginPage() {
  const { loginWithPasskey, loginWithEmail } = useAuth();

  return (
    <div>
      <button onClick={loginWithPasskey}>
        Login with Face ID / Touch ID
      </button>
      <button onClick={() => loginWithEmail('user@example.com')}>
        Login with Email (Magic Link)
      </button>
    </div>
  );
}
```

```tsx
// src/pages/Dashboard.tsx (auto-generated)
import { useAuth } from '@iam-system/react';

export function Dashboard() {
  const { user, logout } = useAuth();

  if (!user) {
    return <div>Loading...</div>;
  }

  return (
    <div>
      <h1>Welcome, {user.name}!</h1>
      <button onClick={logout}>Logout</button>
    </div>
  );
}
```

**Protected routes work automatically:**

```tsx
// src/App.tsx (auto-generated)
import { ProtectedRoute } from '@iam-system/react';

<ProtectedRoute path="/dashboard" component={Dashboard} />
// Redirects to /login if not authenticated
```

---

## What's Next?

You now have working authentication. Here's what you can do:

### Option 1: Deploy to Production (2 minutes)
```bash
# Works with any platform
vercel deploy
# Or: netlify deploy
# Or: npm run build && scp dist/* user@server:/var/www
```

### Option 2: Add More Features (5 minutes each)

**Social Login:**
```bash
iam add google
iam add microsoft
iam add apple
```

**Multi-Factor Authentication:**
```bash
iam add mfa --type totp  # Google Authenticator
iam add mfa --type sms   # SMS codes
```

**Custom Branding:**
```bash
iam config set logo https://yourcompany.com/logo.png
iam config set primary-color "#0066cc"
```

**User Roles:**
```bash
iam roles create admin --permissions "users:*, content:*"
iam roles create editor --permissions "content:read, content:write"
iam users assign-role user@example.com admin
```

### Option 3: Migrate from Auth0/Azure AD (5 minutes)
```bash
iam migrate --from auth0
# Imports all users, roles, and permissions
```

---

## Framework-Specific Quick Starts

<details>
<summary><b>React (Create React App, Vite)</b></summary>

```bash
npx iam init  # Auto-detects React

# Adds to your app:
# - src/auth/AuthProvider.tsx
# - src/auth/useAuth.ts
# - src/auth/ProtectedRoute.tsx
# - src/pages/LoginPage.tsx
```

**Usage:**
```tsx
import { useAuth } from './auth/useAuth';

function MyComponent() {
  const { user, loginWithPasskey, logout } = useAuth();

  return user ? (
    <div>
      <p>Hello, {user.name}</p>
      <button onClick={logout}>Logout</button>
    </div>
  ) : (
    <button onClick={loginWithPasskey}>Login</button>
  );
}
```

</details>

<details>
<summary><b>Next.js (App Router & Pages Router)</b></summary>

```bash
npx iam init  # Auto-detects Next.js

# Adds to your app:
# - app/api/auth/[...iam]/route.ts (App Router)
#   OR pages/api/auth/[...iam].ts (Pages Router)
# - lib/auth.ts
# - middleware.ts (auto-protects routes)
```

**App Router (app/):**
```tsx
// app/dashboard/page.tsx
import { auth } from '@/lib/auth';

export default async function Dashboard() {
  const session = await auth();

  return <div>Hello, {session.user.name}</div>;
}
```

**Pages Router (pages/):**
```tsx
// pages/dashboard.tsx
import { withAuth } from '@/lib/auth';

function Dashboard({ user }) {
  return <div>Hello, {user.name}</div>;
}

export const getServerSideProps = withAuth(async (context) => {
  return { props: { user: context.user } };
});
```

**Protected routes (automatic):**
```typescript
// middleware.ts (auto-generated)
export { middleware } from '@iam-system/nextjs';

export const config = {
  matcher: ['/dashboard/:path*', '/admin/:path*'],
};
```

</details>

<details>
<summary><b>Vue.js 3 (Vite)</b></summary>

```bash
npx iam init  # Auto-detects Vue

# Adds to your app:
# - src/plugins/auth.ts
# - src/composables/useAuth.ts
# - src/views/LoginView.vue
```

**Usage:**
```vue
<template>
  <div v-if="user">
    <p>Hello, {{ user.name }}</p>
    <button @click="logout">Logout</button>
  </div>
  <button v-else @click="loginWithPasskey">Login</button>
</template>

<script setup>
import { useAuth } from '@/composables/useAuth';

const { user, loginWithPasskey, logout } = useAuth();
</script>
```

**Protected routes:**
```typescript
// router/index.ts (auto-generated guard)
import { createRouter } from 'vue-router';
import { authGuard } from '@iam-system/vue';

const router = createRouter({
  routes: [
    {
      path: '/dashboard',
      component: Dashboard,
      beforeEnter: authGuard,  // Protects this route
    },
  ],
});
```

</details>

<details>
<summary><b>Angular 17+</b></summary>

```bash
npx iam init  # Auto-detects Angular

# Adds to your app:
# - src/app/auth/auth.service.ts
# - src/app/auth/auth.guard.ts
# - src/app/auth/auth.interceptor.ts
# - src/app/pages/login/login.component.ts
```

**Usage:**
```typescript
// dashboard.component.ts
import { Component } from '@angular/core';
import { AuthService } from '@/auth/auth.service';

@Component({
  selector: 'app-dashboard',
  template: `
    <div *ngIf="user$ | async as user">
      <p>Hello, {{ user.name }}</p>
      <button (click)="logout()">Logout</button>
    </div>
  `,
})
export class DashboardComponent {
  user$ = this.auth.user$;

  constructor(private auth: AuthService) {}

  logout() {
    this.auth.logout();
  }
}
```

**Protected routes:**
```typescript
// app.routes.ts
import { authGuard } from '@/auth/auth.guard';

export const routes = [
  {
    path: 'dashboard',
    component: DashboardComponent,
    canActivate: [authGuard],  // Protects this route
  },
];
```

</details>

---

## Advanced Usage (When You Need It)

### Custom Authentication UI

Don't like our generated UI? Replace it:

```tsx
// React
import { useAuth } from '@iam-system/react';

function CustomLogin() {
  const { loginWithEmail, loginWithPasskey, isLoading } = useAuth();

  return (
    <YourCustomUI
      onEmailLogin={(email) => loginWithEmail(email)}
      onPasskeyLogin={loginWithPasskey}
      loading={isLoading}
    />
  );
}
```

### Backend API Protection

Protect your API endpoints:

```typescript
// Express.js
import { requireAuth } from '@iam-system/node';

app.get('/api/protected', requireAuth, (req, res) => {
  res.json({ message: `Hello, ${req.user.name}` });
});
```

```csharp
// ASP.NET Core
[Authorize]
[HttpGet("protected")]
public IActionResult Protected()
{
    return Ok(new { message = $"Hello, {User.Identity.Name}" });
}
```

### User Metadata

Store custom data on users:

```typescript
await auth.updateUser({
  metadata: {
    subscriptionTier: 'premium',
    onboardingComplete: true,
  },
});
```

### Webhooks

React to authentication events:

```bash
iam webhooks create \
  --event user.login \
  --url https://yourapi.com/webhooks/user-login

iam webhooks create \
  --event user.register \
  --url https://yourapi.com/webhooks/user-register
```

---

## Common Questions

<details>
<summary><b>Q: Does this work offline?</b></summary>

**A:** Yes, after first login. Tokens are cached locally. When internet reconnects, tokens refresh automatically.

</details>

<details>
<summary><b>Q: What about refresh tokens?</b></summary>

**A:** Handled automatically. Access tokens expire in 15 minutes. Refresh tokens last 30 days. All transparent to you.

</details>

<details>
<summary><b>Q: Can I use my own database?</b></summary>

**A:** Yes. Self-hosted mode connects to your PostgreSQL/MySQL:

```bash
iam config set mode self-hosted
iam config set database postgresql://localhost/iam
```

</details>

<details>
<summary><b>Q: How do I test authentication?</b></summary>

**A:** Built-in test mode:

```bash
iam test login user@example.com
iam test passkey user@example.com
iam test protected-route /dashboard
```

</details>

<details>
<summary><b>Q: What if I need SAML/LDAP/AD?</b></summary>

**A:** Enterprise features available:

```bash
iam add saml --idp your-idp
iam add ldap --server ldap://yourcompany.com
iam add azure-ad --tenant your-tenant-id
```

</details>

---

## Pricing

**Free Tier (Forever):**
- 10,000 Monthly Active Users
- Unlimited passkeys
- Social logins
- Email magic links
- Community support

**Pro Tier ($0.02/MAU above 10K):**
- Everything in Free
- Custom branding
- Advanced analytics
- Priority support
- 99.99% SLA

**Enterprise (Custom):**
- Everything in Pro
- SAML/LDAP/AD
- Dedicated support
- Custom deployment
- SOC 2 compliance

**Calculate your costs:** [iam.dev/pricing](https://iam.dev/pricing)

---

## Next Steps

1. **Deploy to production:** `vercel deploy` or `netlify deploy`
2. **Join Discord:** [discord.gg/iam-system](https://discord.gg/iam-system)
3. **Read API docs:** [iam.dev/docs/api](https://iam.dev/docs/api)
4. **Enable passkeys:** Already done! ✅

**Questions?** We're here: [support@iam.dev](mailto:support@iam.dev)

---

**You're authenticated. Now build something amazing.** 🚀
