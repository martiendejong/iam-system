# IAM System - JavaScript/TypeScript SDK

Official JavaScript/TypeScript SDK for **IAM System** - Enterprise authentication made simple.

[![npm version](https://img.shields.io/npm/v/@iam-system/sdk.svg)](https://www.npmjs.com/package/@iam-system/sdk)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](https://opensource.org/licenses/MIT)
[![TypeScript](https://img.shields.io/badge/TypeScript-5.3-blue)](https://www.typescriptlang.org/)

## 🚀 Features

- ✅ **Simple Integration** - 3 lines of code to get started
- 🔒 **Bank-Level Security** - Token binding, rotation, device fingerprinting
- 📦 **TypeScript First** - Full type safety and IntelliSense support
- 🌐 **Universal** - Works in Node.js, browsers, React, Vue, Angular, Next.js
- ⚡ **Zero Dependencies** - Only `cross-fetch` for universal compatibility
- 🎯 **OAuth 2.1 Compliant** - Industry-standard security
- 🔄 **Auto Token Refresh** - Seamless authentication experience

## 📦 Installation

```bash
npm install @iam-system/sdk
```

or with yarn:

```bash
yarn add @iam-system/sdk
```

or with pnpm:

```bash
pnpm add @iam-system/sdk
```

## 🎯 Quick Start

### 3-Line Integration

```typescript
import { IamAuthClient } from '@iam-system/sdk';

// Line 1: Create client
const client = new IamAuthClient({ apiBaseUrl: 'http://localhost:5161' });

// Line 2: Login
const response = await client.login('user@example.com', 'password123');

// Line 3: Use the access token
console.log('Authenticated!', response.user.email);
```

That's it! You now have enterprise-grade authentication.

## 📚 Usage Examples

### React Integration

```tsx
import { IamAuthClient } from '@iam-system/sdk';
import { createContext, useContext, useState } from 'react';

// Create auth context
const AuthContext = createContext<IamAuthClient | null>(null);

export function AuthProvider({ children }: { children: React.ReactNode }) {
  const [client] = useState(
    () =>
      new IamAuthClient({
        apiBaseUrl: process.env.REACT_APP_IAM_API_URL!,
        onTokenRefreshed: (accessToken) => {
          console.log('Token refreshed automatically');
        },
      })
  );

  return <AuthContext.Provider value={client}>{children}</AuthContext.Provider>;
}

export function useAuth() {
  const context = useContext(AuthContext);
  if (!context) throw new Error('useAuth must be used within AuthProvider');
  return context;
}

// Login component
export function LoginForm() {
  const auth = useAuth();
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');

  const handleLogin = async (e: React.FormEvent) => {
    e.preventDefault();
    try {
      const response = await auth.login(email, password);
      console.log('Logged in:', response.user.email);
    } catch (error) {
      console.error('Login failed:', error);
    }
  };

  return (
    <form onSubmit={handleLogin}>
      <input
        type="email"
        value={email}
        onChange={(e) => setEmail(e.target.value)}
        placeholder="Email"
      />
      <input
        type="password"
        value={password}
        onChange={(e) => setPassword(e.target.value)}
        placeholder="Password"
      />
      <button type="submit">Login</button>
    </form>
  );
}
```

### Vue.js Integration

```typescript
// auth.ts
import { IamAuthClient } from '@iam-system/sdk';
import { ref, readonly } from 'vue';

const client = new IamAuthClient({
  apiBaseUrl: import.meta.env.VITE_IAM_API_URL,
});

const user = ref<UserDto | null>(null);
const isAuthenticated = ref(false);

export function useAuth() {
  const login = async (email: string, password: string) => {
    const response = await client.login(email, password);
    user.value = response.user;
    isAuthenticated.value = true;
  };

  const logout = async () => {
    await client.logout();
    user.value = null;
    isAuthenticated.value = false;
  };

  const getCurrentUser = async () => {
    if (client.isAuthenticated()) {
      user.value = await client.getCurrentUser();
      isAuthenticated.value = true;
    }
  };

  return {
    user: readonly(user),
    isAuthenticated: readonly(isAuthenticated),
    login,
    logout,
    getCurrentUser,
  };
}
```

### Next.js API Route

```typescript
// app/api/auth/login/route.ts
import { IamAuthClient } from '@iam-system/sdk';
import { NextResponse } from 'next/server';

const client = new IamAuthClient({
  apiBaseUrl: process.env.IAM_API_URL!,
});

export async function POST(request: Request) {
  try {
    const { email, password } = await request.json();
    const response = await client.login(email, password);

    return NextResponse.json({
      user: response.user,
      accessToken: response.accessToken,
    });
  } catch (error) {
    return NextResponse.json(
      { error: error.message },
      { status: 401 }
    );
  }
}
```

### Node.js Server

```typescript
import { IamAuthClient } from '@iam-system/sdk';
import express from 'express';

const app = express();
const client = new IamAuthClient({
  apiBaseUrl: process.env.IAM_API_URL!,
});

app.post('/api/login', async (req, res) => {
  try {
    const { email, password } = req.body;
    const response = await client.login(email, password);

    res.json({
      user: response.user,
      accessToken: response.accessToken,
    });
  } catch (error) {
    res.status(401).json({ error: error.message });
  }
});

app.get('/api/me', async (req, res) => {
  try {
    // Get token from Authorization header
    const token = req.headers.authorization?.replace('Bearer ', '');
    if (!token) throw new Error('No token provided');

    client.setAccessToken(token);
    const user = await client.getCurrentUser();

    res.json({ user });
  } catch (error) {
    res.status(401).json({ error: error.message });
  }
});

app.listen(3000, () => console.log('Server running on port 3000'));
```

## 🔒 Security Features

### Token Binding
Access tokens are cryptographically bound to refresh tokens, preventing token theft.

### Token Rotation
Refresh tokens are single-use and automatically rotated on every refresh (OAuth 2.1 compliant).

### Device Fingerprinting
IP address and User-Agent are tracked for anomaly detection.

### Short-Lived Tokens
Access tokens expire in 5 minutes (configurable), minimizing attack windows.

### Automatic Refresh
Tokens are automatically refreshed when they expire (if enabled).

## 📖 API Reference

### `IamAuthClient`

#### Constructor

```typescript
new IamAuthClient(options: IamClientOptions)
```

**Options:**
- `apiBaseUrl` (required): Base URL of the IAM API
- `timeoutSeconds` (optional): Request timeout in seconds (default: 30)
- `autoRefreshTokens` (optional): Auto-refresh tokens (default: true)
- `headers` (optional): Custom headers for all requests
- `onTokenRefreshed` (optional): Callback when token is refreshed
- `onAuthenticationFailed` (optional): Callback when auth fails

#### Methods

**`login(email: string, password: string): Promise<LoginResponse>`**

Login with email and password.

```typescript
const response = await client.login('user@example.com', 'password123');
console.log(response.accessToken); // JWT access token
console.log(response.user.email); // user@example.com
```

**`register(email: string, password: string, firstName: string, lastName: string): Promise<RegisterResponse>`**

Register a new user.

```typescript
const response = await client.register(
  'new@example.com',
  'securePassword123',
  'John',
  'Doe'
);
console.log(response.userId); // New user ID
```

**`refreshToken(refreshToken: string): Promise<LoginResponse>`**

Refresh access token using refresh token.

```typescript
const response = await client.refreshToken('your-refresh-token');
console.log(response.accessToken); // New access token
```

**`getCurrentUser(): Promise<UserDto>`**

Get current authenticated user.

```typescript
const user = await client.getCurrentUser();
console.log(user.email, user.firstName, user.lastName);
```

**`logout(): Promise<void>`**

Logout and revoke tokens.

```typescript
await client.logout();
console.log('Logged out successfully');
```

**`getAccessToken(): string | null`**

Get current access token.

```typescript
const token = client.getAccessToken();
if (token) {
  // Use token
}
```

**`setAccessToken(accessToken: string): void`**

Set access token manually (e.g., from localStorage).

```typescript
client.setAccessToken('your-stored-token');
```

**`isAuthenticated(): boolean`**

Check if user is authenticated.

```typescript
if (client.isAuthenticated()) {
  console.log('User is logged in');
}
```

## 🔧 Configuration

### Custom Headers

```typescript
const client = new IamAuthClient({
  apiBaseUrl: 'http://localhost:5161',
  headers: {
    'X-Custom-Header': 'value',
  },
});
```

### Token Refresh Callback

```typescript
const client = new IamAuthClient({
  apiBaseUrl: 'http://localhost:5161',
  onTokenRefreshed: (accessToken, refreshToken) => {
    // Store tokens in localStorage, cookies, or state management
    localStorage.setItem('accessToken', accessToken);
    if (refreshToken) {
      localStorage.setItem('refreshToken', refreshToken);
    }
  },
});
```

### Authentication Failed Callback

```typescript
const client = new IamAuthClient({
  apiBaseUrl: 'http://localhost:5161',
  onAuthenticationFailed: (error) => {
    // Redirect to login page, show error message, etc.
    console.error('Authentication failed:', error.message);
    window.location.href = '/login';
  },
});
```

## 📊 TypeScript Support

This SDK is written in TypeScript and provides full type definitions.

```typescript
import type { UserDto, LoginResponse, IamClientOptions } from '@iam-system/sdk';

const options: IamClientOptions = {
  apiBaseUrl: 'http://localhost:5161',
  timeoutSeconds: 60,
};

const handleLogin = async (): Promise<UserDto> => {
  const response: LoginResponse = await client.login('user@example.com', 'password');
  return response.user;
};
```

## 🌐 Browser Support

This SDK works in all modern browsers:

- ✅ Chrome / Edge (Chromium)
- ✅ Firefox
- ✅ Safari
- ✅ Opera
- ✅ Mobile browsers (iOS Safari, Chrome Android)

For older browsers, you may need to include polyfills for `fetch` and `Promise`.

## 📦 Bundle Size

- **ESM:** ~5 KB (minified + gzipped)
- **CJS:** ~6 KB (minified + gzipped)
- **Zero dependencies** (except `cross-fetch` for universal compatibility)

## 🛠️ Development

```bash
# Install dependencies
npm install

# Build the SDK
npm run build

# Run tests
npm test

# Run linter
npm run lint

# Format code
npm run format
```

## 📄 License

MIT License - see [LICENSE](../../LICENSE) file for details.

## 🤝 Contributing

Contributions are welcome! Please feel free to submit a Pull Request.

## 🔗 Links

- [GitHub Repository](https://github.com/martiendejong/iam-system)
- [Documentation](https://github.com/martiendejong/iam-system#readme)
- [Issue Tracker](https://github.com/martiendejong/iam-system/issues)
- [NPM Package](https://www.npmjs.com/package/@iam-system/sdk)

## 💬 Support

For questions, issues, or feature requests:
- Open an issue on [GitHub](https://github.com/martiendejong/iam-system/issues)
- Contact: [your-email@example.com]

---

**Built with ❤️ by the IAM System Team**

**More secure than Auth0. Easier than Okta. 100% Free and Open Source.**
