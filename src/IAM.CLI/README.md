# IAM System CLI

> Enterprise authentication in 60 seconds

A command-line tool for initializing and managing authentication in your web applications using the IAM System. Supports React, Vue.js, Next.js, and Angular.

## Features

- 🚀 **60-second setup** - From zero to authenticated app in one minute
- 🎯 **Framework detection** - Automatically detects your framework
- 📦 **SDK installation** - Installs and configures @iam-system/sdk
- 🔨 **Code generation** - Generates framework-specific authentication code
- ✅ **Status checking** - Verifies your integration is working correctly
- ⚙️ **Configuration** - Stores global settings for reuse

## Installation

```bash
# npm
npm install -g @iam-system/cli

# yarn
yarn global add @iam-system/cli

# pnpm
pnpm add -g @iam-system/cli
```

## Quick Start

### Initialize a New Project

Navigate to your project directory and run:

```bash
iam init
```

The CLI will:
1. Auto-detect your framework (React, Vue, Next.js, or Angular)
2. Ask for your IAM API URL (defaults to http://localhost:5161)
3. Install the @iam-system/sdk package
4. Generate authentication code for your framework
5. Show usage examples

### Add to Existing Project

If you already have a project:

```bash
iam add
```

This works like `init` but skips SDK installation if it's already present.

## Commands

### `iam init`

Initialize a new project with IAM authentication.

**Options:**
- `-f, --framework <framework>` - Framework: react, vue, angular, nextjs
- `-t, --typescript` - Use TypeScript (default: true)
- `-d, --directory <directory>` - Project directory (default: ".")
- `--api-url <url>` - IAM API URL (default: "http://localhost:5161")

**Examples:**

```bash
# Interactive mode (recommended)
iam init

# Specify framework
iam init --framework react

# Custom API URL
iam init --api-url https://api.example.com

# JavaScript instead of TypeScript
iam init --typescript false

# All options at once
iam init -f vue --api-url https://api.example.com -d ./my-app
```

### `iam add`

Add IAM authentication to an existing project.

**Options:**
- `-f, --framework <framework>` - Auto-detect framework if not specified
- `--api-url <url>` - IAM API URL (default: "http://localhost:5161")

**Examples:**

```bash
# Interactive mode
iam add

# Specify framework
iam add --framework nextjs

# Custom API URL
iam add --api-url https://api.example.com
```

### `iam config`

Configure global IAM settings.

**Options:**
- `-k, --key <key>` - Configuration key
- `-v, --value <value>` - Configuration value
- `--list` - List all configuration

**Examples:**

```bash
# List all configuration
iam config --list

# Set API URL
iam config --key apiUrl --value https://api.example.com

# Set default framework
iam config --key defaultFramework --value react

# Get a specific value
iam config --key apiUrl
```

Configuration is stored in `~/.iam/config.json`.

### `iam status`

Check IAM integration status.

**Examples:**

```bash
iam status
```

Checks:
- ✓ Framework detection
- ✓ SDK installation
- ✓ Authentication code generated
- ✓ API URL configured
- ✓ API connection

## Generated Code

### React

Generates:
- `src/auth/AuthContext.tsx` - React context for authentication
- `src/auth/useAuth.tsx` - Custom hook for auth state
- `src/auth/LoginPage.tsx` - Example login component
- `src/auth/index.ts` - Barrel export

**Usage:**

```tsx
import { AuthProvider, useAuth } from './auth';

function App() {
  return (
    <AuthProvider>
      <YourApp />
    </AuthProvider>
  );
}

function YourComponent() {
  const { user, isAuthenticated, login, logout } = useAuth();

  if (isAuthenticated) {
    return <div>Welcome, {user?.firstName}!</div>;
  }

  return <button onClick={() => login('user@example.com', 'password')}>
    Login
  </button>;
}
```

### Vue.js

Generates:
- `src/composables/useAuth.ts` - Vue composable for authentication
- `src/views/LoginView.vue` - Example login component

**Usage:**

```vue
<script setup>
import { useAuth } from '@/composables/useAuth';

const { user, isAuthenticated, login, logout } = useAuth();

async function handleLogin() {
  await login('user@example.com', 'password');
}
</script>

<template>
  <div v-if="isAuthenticated">
    Welcome, {{ user?.firstName }}!
  </div>
  <button v-else @click="handleLogin">Login</button>
</template>
```

### Next.js

Generates:
- `lib/iam.ts` - IAM client instance
- `app/api/auth/login/route.ts` - Login API route
- `app/api/auth/register/route.ts` - Register API route
- `app/api/auth/refresh/route.ts` - Token refresh API route
- `app/api/auth/logout/route.ts` - Logout API route
- `app/login/page.tsx` - Example login page

**Usage:**

```tsx
// Client component
'use client';

async function handleLogin(email: string, password: string) {
  const response = await fetch('/api/auth/login', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ email, password }),
  });

  if (response.ok) {
    const { user, accessToken } = await response.json();
    // Handle successful login
  }
}
```

### Angular

Generates:
- `src/app/services/iam.service.ts` - Authentication service
- `src/app/guards/auth.guard.ts` - Route guard
- `src/app/interceptors/auth.interceptor.ts` - HTTP interceptor
- `src/app/login/login.component.ts` - Login component
- `src/app/login/login.component.html` - Login template
- `src/app/login/login.component.css` - Login styles

**Usage:**

```typescript
// Component
import { IamService } from './services/iam.service';

@Component({
  selector: 'app-dashboard',
  template: '<div *ngIf="isAuthenticated">Welcome, {{ user?.firstName }}!</div>'
})
export class DashboardComponent {
  user$ = this.iamService.user$;
  isAuthenticated$ = this.iamService.isAuthenticated$;

  constructor(private iamService: IamService) {}

  async login() {
    await this.iamService.login('user@example.com', 'password');
  }
}

// Route protection
const routes: Routes = [
  {
    path: 'dashboard',
    component: DashboardComponent,
    canActivate: [AuthGuard]
  }
];

// HTTP interceptor (add to app.module.ts providers)
providers: [
  { provide: HTTP_INTERCEPTORS, useClass: AuthInterceptor, multi: true }
]
```

## Requirements

- Node.js >= 18.0.0
- An existing web project with React, Vue, Next.js, or Angular
- IAM System API running (see [IAM System Documentation](https://github.com/martiendejong/iam-system))

## Comparison with Alternatives

| Feature | IAM CLI | Auth0 CLI | AWS Amplify |
|---------|---------|-----------|-------------|
| Setup Time | 60 seconds | 10+ minutes | 15+ minutes |
| Bundle Size | 5 KB | 50 KB | 150 KB+ |
| Framework Support | 4 frameworks | Limited | React-focused |
| Code Generation | ✅ Full | ❌ None | ✅ Partial |
| Self-hosted | ✅ Yes | ❌ No | ❌ No |
| Cost | Free | Paid tiers | Paid tiers |
| OAuth 2.1 | ✅ Yes | ✅ Yes | ✅ Yes |
| Token Security | ✅ Advanced | ✅ Basic | ✅ Basic |

## Development

```bash
# Install dependencies
npm install

# Build
npm run build

# Test locally
npm link
iam --help

# Unlink
npm unlink -g @iam-system/cli
```

## Troubleshooting

### "Framework not detected"

Make sure you have a `package.json` with dependencies for your framework:
- React: `react` package
- Vue: `vue` package
- Next.js: `next` package
- Angular: `@angular/core` package

### "Cannot connect to API"

Ensure your IAM API is running:

```bash
# Check if API is running
curl http://localhost:5161/health
```

If not, start your IAM API first.

### "SDK installation failed"

Try manually installing:

```bash
npm install @iam-system/sdk
```

Then run `iam add` again.

## License

MIT

## Links

- [IAM System Documentation](https://github.com/martiendejong/iam-system)
- [JavaScript SDK](https://www.npmjs.com/package/@iam-system/sdk)
- [Report Issues](https://github.com/martiendejong/iam-system/issues)
