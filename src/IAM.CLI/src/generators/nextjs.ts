import fs from 'fs-extra';
import path from 'path';

interface GeneratorConfig {
  apiUrl: string;
  typescript: boolean;
}

export async function generateNextjsAuth(cwd: string, config: GeneratorConfig): Promise<void> {
  const { apiUrl, typescript } = config;
  const ext = typescript ? 'ts' : 'js';

  // Detect if using app directory (Next.js 13+) or pages directory
  const hasAppDir = await fs.pathExists(path.join(cwd, 'app'));
  const hasPagesDir = await fs.pathExists(path.join(cwd, 'pages'));

  if (hasAppDir) {
    await generateAppDirectory(cwd, apiUrl, typescript);
  } else if (hasPagesDir) {
    await generatePagesDirectory(cwd, apiUrl, typescript);
  } else {
    // Default to app directory (Next.js 13+)
    await generateAppDirectory(cwd, apiUrl, typescript);
  }

  // Generate shared lib directory
  const libDir = path.join(cwd, 'lib');
  await fs.ensureDir(libDir);

  const iamClientContent = generateIamClient(apiUrl, typescript);
  await fs.writeFile(path.join(libDir, `iam.${ext}`), iamClientContent);
}

async function generateAppDirectory(cwd: string, apiUrl: string, typescript: boolean): Promise<void> {
  const ext = typescript ? 'ts' : 'js';

  // Create API routes for authentication
  const authApiDir = path.join(cwd, 'app', 'api', 'auth');

  // Login route
  const loginDir = path.join(authApiDir, 'login');
  await fs.ensureDir(loginDir);
  const loginRouteContent = generateAppLoginRoute(typescript);
  await fs.writeFile(path.join(loginDir, `route.${ext}`), loginRouteContent);

  // Register route
  const registerDir = path.join(authApiDir, 'register');
  await fs.ensureDir(registerDir);
  const registerRouteContent = generateAppRegisterRoute(typescript);
  await fs.writeFile(path.join(registerDir, `route.${ext}`), registerRouteContent);

  // Refresh route
  const refreshDir = path.join(authApiDir, 'refresh');
  await fs.ensureDir(refreshDir);
  const refreshRouteContent = generateAppRefreshRoute(typescript);
  await fs.writeFile(path.join(refreshDir, `route.${ext}`), refreshRouteContent);

  // Logout route
  const logoutDir = path.join(authApiDir, 'logout');
  await fs.ensureDir(logoutDir);
  const logoutRouteContent = generateAppLogoutRoute(typescript);
  await fs.writeFile(path.join(logoutDir, `route.${ext}`), logoutRouteContent);

  // Example login page
  const loginPageDir = path.join(cwd, 'app', 'login');
  await fs.ensureDir(loginPageDir);
  const loginPageContent = generateAppLoginPage(typescript);
  await fs.writeFile(path.join(loginPageDir, `page.${typescript ? 'tsx' : 'jsx'}`), loginPageContent);
}

async function generatePagesDirectory(cwd: string, apiUrl: string, typescript: boolean): Promise<void> {
  const ext = typescript ? 'ts' : 'js';

  // Create API routes for authentication
  const authApiDir = path.join(cwd, 'pages', 'api', 'auth');
  await fs.ensureDir(authApiDir);

  // Login route
  const loginRouteContent = generatePagesLoginRoute(typescript);
  await fs.writeFile(path.join(authApiDir, `login.${ext}`), loginRouteContent);

  // Register route
  const registerRouteContent = generatePagesRegisterRoute(typescript);
  await fs.writeFile(path.join(authApiDir, `register.${ext}`), registerRouteContent);

  // Refresh route
  const refreshRouteContent = generatePagesRefreshRoute(typescript);
  await fs.writeFile(path.join(authApiDir, `refresh.${ext}`), refreshRouteContent);

  // Logout route
  const logoutRouteContent = generatePagesLogoutRoute(typescript);
  await fs.writeFile(path.join(authApiDir, `logout.${ext}`), logoutRouteContent);

  // Example login page
  const loginPageContent = generatePagesLoginPage(typescript);
  await fs.writeFile(path.join(cwd, 'pages', `login.${typescript ? 'tsx' : 'jsx'}`), loginPageContent);
}

function generateIamClient(apiUrl: string, typescript: boolean): string {
  if (typescript) {
    return `import { IamAuthClient } from '@iam-system/sdk';

export const iamClient = new IamAuthClient({
  apiBaseUrl: '${apiUrl}',
  autoRefreshTokens: false,
});

export default iamClient;
`;
  } else {
    return `import { IamAuthClient } from '@iam-system/sdk';

export const iamClient = new IamAuthClient({
  apiBaseUrl: '${apiUrl}',
  autoRefreshTokens: false,
});

export default iamClient;
`;
  }
}

// App Directory Routes (Next.js 13+)

function generateAppLoginRoute(typescript: boolean): string {
  if (typescript) {
    return `import { NextRequest, NextResponse } from 'next/server';
import { iamClient } from '@/lib/iam';

export async function POST(request: NextRequest) {
  try {
    const { email, password } = await request.json();

    const response = await iamClient.login(email, password);

    // Set refresh token in HTTP-only cookie
    const res = NextResponse.json({ user: response.user, accessToken: response.accessToken });

    if (response.refreshToken) {
      res.cookies.set('refreshToken', response.refreshToken, {
        httpOnly: true,
        secure: process.env.NODE_ENV === 'production',
        sameSite: 'strict',
        maxAge: 60 * 60 * 24 * 7, // 7 days
        path: '/',
      });
    }

    return res;
  } catch (error: any) {
    return NextResponse.json({ error: error.message || 'Login failed' }, { status: 401 });
  }
}
`;
  } else {
    return `import { NextResponse } from 'next/server';
import { iamClient } from '@/lib/iam';

export async function POST(request) {
  try {
    const { email, password } = await request.json();

    const response = await iamClient.login(email, password);

    // Set refresh token in HTTP-only cookie
    const res = NextResponse.json({ user: response.user, accessToken: response.accessToken });

    if (response.refreshToken) {
      res.cookies.set('refreshToken', response.refreshToken, {
        httpOnly: true,
        secure: process.env.NODE_ENV === 'production',
        sameSite: 'strict',
        maxAge: 60 * 60 * 24 * 7, // 7 days
        path: '/',
      });
    }

    return res;
  } catch (error) {
    return NextResponse.json({ error: error.message || 'Login failed' }, { status: 401 });
  }
}
`;
  }
}

function generateAppRegisterRoute(typescript: boolean): string {
  if (typescript) {
    return `import { NextRequest, NextResponse } from 'next/server';
import { iamClient } from '@/lib/iam';

export async function POST(request: NextRequest) {
  try {
    const { email, password, firstName, lastName } = await request.json();

    const response = await iamClient.register(email, password, firstName, lastName);

    // Set refresh token in HTTP-only cookie
    const res = NextResponse.json({ user: response.user, accessToken: response.accessToken });

    if (response.refreshToken) {
      res.cookies.set('refreshToken', response.refreshToken, {
        httpOnly: true,
        secure: process.env.NODE_ENV === 'production',
        sameSite: 'strict',
        maxAge: 60 * 60 * 24 * 7, // 7 days
        path: '/',
      });
    }

    return res;
  } catch (error: any) {
    return NextResponse.json({ error: error.message || 'Registration failed' }, { status: 400 });
  }
}
`;
  } else {
    return `import { NextResponse } from 'next/server';
import { iamClient } from '@/lib/iam';

export async function POST(request) {
  try {
    const { email, password, firstName, lastName } = await request.json();

    const response = await iamClient.register(email, password, firstName, lastName);

    // Set refresh token in HTTP-only cookie
    const res = NextResponse.json({ user: response.user, accessToken: response.accessToken });

    if (response.refreshToken) {
      res.cookies.set('refreshToken', response.refreshToken, {
        httpOnly: true,
        secure: process.env.NODE_ENV === 'production',
        sameSite: 'strict',
        maxAge: 60 * 60 * 24 * 7, // 7 days
        path: '/',
      });
    }

    return res;
  } catch (error) {
    return NextResponse.json({ error: error.message || 'Registration failed' }, { status: 400 });
  }
}
`;
  }
}

function generateAppRefreshRoute(typescript: boolean): string {
  if (typescript) {
    return `import { NextRequest, NextResponse } from 'next/server';
import { iamClient } from '@/lib/iam';

export async function POST(request: NextRequest) {
  try {
    const refreshToken = request.cookies.get('refreshToken')?.value;

    if (!refreshToken) {
      return NextResponse.json({ error: 'No refresh token' }, { status: 401 });
    }

    const response = await iamClient.refreshToken(refreshToken);

    // Update refresh token cookie
    const res = NextResponse.json({ user: response.user, accessToken: response.accessToken });

    if (response.refreshToken) {
      res.cookies.set('refreshToken', response.refreshToken, {
        httpOnly: true,
        secure: process.env.NODE_ENV === 'production',
        sameSite: 'strict',
        maxAge: 60 * 60 * 24 * 7, // 7 days
        path: '/',
      });
    }

    return res;
  } catch (error: any) {
    return NextResponse.json({ error: error.message || 'Token refresh failed' }, { status: 401 });
  }
}
`;
  } else {
    return `import { NextResponse } from 'next/server';
import { iamClient } from '@/lib/iam';

export async function POST(request) {
  try {
    const refreshToken = request.cookies.get('refreshToken')?.value;

    if (!refreshToken) {
      return NextResponse.json({ error: 'No refresh token' }, { status: 401 });
    }

    const response = await iamClient.refreshToken(refreshToken);

    // Update refresh token cookie
    const res = NextResponse.json({ user: response.user, accessToken: response.accessToken });

    if (response.refreshToken) {
      res.cookies.set('refreshToken', response.refreshToken, {
        httpOnly: true,
        secure: process.env.NODE_ENV === 'production',
        sameSite: 'strict',
        maxAge: 60 * 60 * 24 * 7, // 7 days
        path: '/',
      });
    }

    return res;
  } catch (error) {
    return NextResponse.json({ error: error.message || 'Token refresh failed' }, { status: 401 });
  }
}
`;
  }
}

function generateAppLogoutRoute(typescript: boolean): string {
  if (typescript) {
    return `import { NextRequest, NextResponse } from 'next/server';
import { iamClient } from '@/lib/iam';

export async function POST(request: NextRequest) {
  try {
    const refreshToken = request.cookies.get('refreshToken')?.value;

    if (refreshToken) {
      await iamClient.logout(refreshToken);
    }

    const res = NextResponse.json({ success: true });
    res.cookies.delete('refreshToken');

    return res;
  } catch (error: any) {
    return NextResponse.json({ error: error.message || 'Logout failed' }, { status: 500 });
  }
}
`;
  } else {
    return `import { NextResponse } from 'next/server';
import { iamClient } from '@/lib/iam';

export async function POST(request) {
  try {
    const refreshToken = request.cookies.get('refreshToken')?.value;

    if (refreshToken) {
      await iamClient.logout(refreshToken);
    }

    const res = NextResponse.json({ success: true });
    res.cookies.delete('refreshToken');

    return res;
  } catch (error) {
    return NextResponse.json({ error: error.message || 'Logout failed' }, { status: 500 });
  }
}
`;
  }
}

function generateAppLoginPage(typescript: boolean): string {
  const quote = typescript ? "'" : "'";
  return `${typescript ? "'use client';\n\n" : ""}import { useState } from 'react';
import { useRouter } from 'next/navigation';

export default function LoginPage() {
  const router = useRouter();
  const [isLoginMode, setIsLoginMode] = useState(true);
  const [email, setEmail] = useState(${quote}${quote});
  const [password, setPassword] = useState(${quote}${quote});
  const [firstName, setFirstName] = useState(${quote}${quote});
  const [lastName, setLastName] = useState(${quote}${quote});
  const [error, setError] = useState${typescript ? '<string | null>' : ''}(null);

  const handleSubmit = async (e${typescript ? ': React.FormEvent' : ''}) => {
    e.preventDefault();
    setError(null);

    try {
      const endpoint = isLoginMode ? '/api/auth/login' : '/api/auth/register';
      const body = isLoginMode
        ? { email, password }
        : { email, password, firstName, lastName };

      const response = await fetch(endpoint, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(body),
      });

      if (!response.ok) {
        const data = await response.json();
        throw new Error(data.error || 'Authentication failed');
      }

      router.push('/');
    } catch (err${typescript ? ': any' : ''}) {
      setError(err.message || 'Authentication failed');
    }
  };

  return (
    <div style={{ maxWidth: '400px', margin: '50px auto', padding: '20px' }}>
      <h1>{isLoginMode ? 'Login' : 'Register'}</h1>
      <form onSubmit={handleSubmit}>
        {!isLoginMode && (
          <>
            <input
              type="text"
              placeholder="First Name"
              value={firstName}
              onChange={(e) => setFirstName(e.target.value)}
              required
              style={{ display: 'block', width: '100%', margin: '10px 0', padding: '10px' }}
            />
            <input
              type="text"
              placeholder="Last Name"
              value={lastName}
              onChange={(e) => setLastName(e.target.value)}
              required
              style={{ display: 'block', width: '100%', margin: '10px 0', padding: '10px' }}
            />
          </>
        )}
        <input
          type="email"
          placeholder="Email"
          value={email}
          onChange={(e) => setEmail(e.target.value)}
          required
          style={{ display: 'block', width: '100%', margin: '10px 0', padding: '10px' }}
        />
        <input
          type="password"
          placeholder="Password"
          value={password}
          onChange={(e) => setPassword(e.target.value)}
          required
          style={{ display: 'block', width: '100%', margin: '10px 0', padding: '10px' }}
        />
        {error && <div style={{ color: 'red', margin: '10px 0' }}>{error}</div>}
        <button type="submit" style={{ width: '100%', padding: '10px', margin: '10px 0' }}>
          {isLoginMode ? 'Login' : 'Register'}
        </button>
      </form>
      <button
        onClick={() => setIsLoginMode(!isLoginMode)}
        style={{ width: '100%', padding: '10px' }}
      >
        {isLoginMode ? 'Need an account? Register' : 'Have an account? Login'}
      </button>
    </div>
  );
}
`;
}

// Pages Directory Routes (Next.js 12 and below)

function generatePagesLoginRoute(typescript: boolean): string {
  if (typescript) {
    return `import type { NextApiRequest, NextApiResponse } from 'next';
import { iamClient } from '@/lib/iam';

export default async function handler(req: NextApiRequest, res: NextApiResponse) {
  if (req.method !== 'POST') {
    return res.status(405).json({ error: 'Method not allowed' });
  }

  try {
    const { email, password } = req.body;
    const response = await iamClient.login(email, password);

    // Set refresh token in HTTP-only cookie
    if (response.refreshToken) {
      res.setHeader('Set-Cookie', \`refreshToken=\${response.refreshToken}; HttpOnly; Secure; SameSite=Strict; Max-Age=604800; Path=/\`);
    }

    return res.status(200).json({ user: response.user, accessToken: response.accessToken });
  } catch (error: any) {
    return res.status(401).json({ error: error.message || 'Login failed' });
  }
}
`;
  } else {
    return `import { iamClient } from '@/lib/iam';

export default async function handler(req, res) {
  if (req.method !== 'POST') {
    return res.status(405).json({ error: 'Method not allowed' });
  }

  try {
    const { email, password } = req.body;
    const response = await iamClient.login(email, password);

    // Set refresh token in HTTP-only cookie
    if (response.refreshToken) {
      res.setHeader('Set-Cookie', \`refreshToken=\${response.refreshToken}; HttpOnly; Secure; SameSite=Strict; Max-Age=604800; Path=/\`);
    }

    return res.status(200).json({ user: response.user, accessToken: response.accessToken });
  } catch (error) {
    return res.status(401).json({ error: error.message || 'Login failed' });
  }
}
`;
  }
}

function generatePagesRegisterRoute(typescript: boolean): string {
  if (typescript) {
    return `import type { NextApiRequest, NextApiResponse } from 'next';
import { iamClient } from '@/lib/iam';

export default async function handler(req: NextApiRequest, res: NextApiResponse) {
  if (req.method !== 'POST') {
    return res.status(405).json({ error: 'Method not allowed' });
  }

  try {
    const { email, password, firstName, lastName } = req.body;
    const response = await iamClient.register(email, password, firstName, lastName);

    // Set refresh token in HTTP-only cookie
    if (response.refreshToken) {
      res.setHeader('Set-Cookie', \`refreshToken=\${response.refreshToken}; HttpOnly; Secure; SameSite=Strict; Max-Age=604800; Path=/\`);
    }

    return res.status(200).json({ user: response.user, accessToken: response.accessToken });
  } catch (error: any) {
    return res.status(400).json({ error: error.message || 'Registration failed' });
  }
}
`;
  } else {
    return `import { iamClient } from '@/lib/iam';

export default async function handler(req, res) {
  if (req.method !== 'POST') {
    return res.status(405).json({ error: 'Method not allowed' });
  }

  try {
    const { email, password, firstName, lastName } = req.body;
    const response = await iamClient.register(email, password, firstName, lastName);

    // Set refresh token in HTTP-only cookie
    if (response.refreshToken) {
      res.setHeader('Set-Cookie', \`refreshToken=\${response.refreshToken}; HttpOnly; Secure; SameSite=Strict; Max-Age=604800; Path=/\`);
    }

    return res.status(200).json({ user: response.user, accessToken: response.accessToken });
  } catch (error) {
    return res.status(400).json({ error: error.message || 'Registration failed' });
  }
}
`;
  }
}

function generatePagesRefreshRoute(typescript: boolean): string {
  if (typescript) {
    return `import type { NextApiRequest, NextApiResponse } from 'next';
import { iamClient } from '@/lib/iam';

export default async function handler(req: NextApiRequest, res: NextApiResponse) {
  if (req.method !== 'POST') {
    return res.status(405).json({ error: 'Method not allowed' });
  }

  try {
    const refreshToken = req.cookies.refreshToken;

    if (!refreshToken) {
      return res.status(401).json({ error: 'No refresh token' });
    }

    const response = await iamClient.refreshToken(refreshToken);

    // Update refresh token cookie
    if (response.refreshToken) {
      res.setHeader('Set-Cookie', \`refreshToken=\${response.refreshToken}; HttpOnly; Secure; SameSite=Strict; Max-Age=604800; Path=/\`);
    }

    return res.status(200).json({ user: response.user, accessToken: response.accessToken });
  } catch (error: any) {
    return res.status(401).json({ error: error.message || 'Token refresh failed' });
  }
}
`;
  } else {
    return `import { iamClient } from '@/lib/iam';

export default async function handler(req, res) {
  if (req.method !== 'POST') {
    return res.status(405).json({ error: 'Method not allowed' });
  }

  try {
    const refreshToken = req.cookies.refreshToken;

    if (!refreshToken) {
      return res.status(401).json({ error: 'No refresh token' });
    }

    const response = await iamClient.refreshToken(refreshToken);

    // Update refresh token cookie
    if (response.refreshToken) {
      res.setHeader('Set-Cookie', \`refreshToken=\${response.refreshToken}; HttpOnly; Secure; SameSite=Strict; Max-Age=604800; Path=/\`);
    }

    return res.status(200).json({ user: response.user, accessToken: response.accessToken });
  } catch (error) {
    return res.status(401).json({ error: error.message || 'Token refresh failed' });
  }
}
`;
  }
}

function generatePagesLogoutRoute(typescript: boolean): string {
  if (typescript) {
    return `import type { NextApiRequest, NextApiResponse } from 'next';
import { iamClient } from '@/lib/iam';

export default async function handler(req: NextApiRequest, res: NextApiResponse) {
  if (req.method !== 'POST') {
    return res.status(405).json({ error: 'Method not allowed' });
  }

  try {
    const refreshToken = req.cookies.refreshToken;

    if (refreshToken) {
      await iamClient.logout(refreshToken);
    }

    res.setHeader('Set-Cookie', 'refreshToken=; HttpOnly; Secure; SameSite=Strict; Max-Age=0; Path=/');
    return res.status(200).json({ success: true });
  } catch (error: any) {
    return res.status(500).json({ error: error.message || 'Logout failed' });
  }
}
`;
  } else {
    return `import { iamClient } from '@/lib/iam';

export default async function handler(req, res) {
  if (req.method !== 'POST') {
    return res.status(405).json({ error: 'Method not allowed' });
  }

  try {
    const refreshToken = req.cookies.refreshToken;

    if (refreshToken) {
      await iamClient.logout(refreshToken);
    }

    res.setHeader('Set-Cookie', 'refreshToken=; HttpOnly; Secure; SameSite=Strict; Max-Age=0; Path=/');
    return res.status(200).json({ success: true });
  } catch (error) {
    return res.status(500).json({ error: error.message || 'Logout failed' });
  }
}
`;
  }
}

function generatePagesLoginPage(typescript: boolean): string {
  const quote = typescript ? "'" : "'";
  return `import { useState } from 'react';
import { useRouter } from 'next/router';

export default function LoginPage() {
  const router = useRouter();
  const [isLoginMode, setIsLoginMode] = useState(true);
  const [email, setEmail] = useState(${quote}${quote});
  const [password, setPassword] = useState(${quote}${quote});
  const [firstName, setFirstName] = useState(${quote}${quote});
  const [lastName, setLastName] = useState(${quote}${quote});
  const [error, setError] = useState${typescript ? '<string | null>' : ''}(null);

  const handleSubmit = async (e${typescript ? ': React.FormEvent' : ''}) => {
    e.preventDefault();
    setError(null);

    try {
      const endpoint = isLoginMode ? '/api/auth/login' : '/api/auth/register';
      const body = isLoginMode
        ? { email, password }
        : { email, password, firstName, lastName };

      const response = await fetch(endpoint, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(body),
      });

      if (!response.ok) {
        const data = await response.json();
        throw new Error(data.error || 'Authentication failed');
      }

      router.push('/');
    } catch (err${typescript ? ': any' : ''}) {
      setError(err.message || 'Authentication failed');
    }
  };

  return (
    <div style={{ maxWidth: '400px', margin: '50px auto', padding: '20px' }}>
      <h1>{isLoginMode ? 'Login' : 'Register'}</h1>
      <form onSubmit={handleSubmit}>
        {!isLoginMode && (
          <>
            <input
              type="text"
              placeholder="First Name"
              value={firstName}
              onChange={(e) => setFirstName(e.target.value)}
              required
              style={{ display: 'block', width: '100%', margin: '10px 0', padding: '10px' }}
            />
            <input
              type="text"
              placeholder="Last Name"
              value={lastName}
              onChange={(e) => setLastName(e.target.value)}
              required
              style={{ display: 'block', width: '100%', margin: '10px 0', padding: '10px' }}
            />
          </>
        )}
        <input
          type="email"
          placeholder="Email"
          value={email}
          onChange={(e) => setEmail(e.target.value)}
          required
          style={{ display: 'block', width: '100%', margin: '10px 0', padding: '10px' }}
        />
        <input
          type="password"
          placeholder="Password"
          value={password}
          onChange={(e) => setPassword(e.target.value)}
          required
          style={{ display: 'block', width: '100%', margin: '10px 0', padding: '10px' }}
        />
        {error && <div style={{ color: 'red', margin: '10px 0' }}>{error}</div>}
        <button type="submit" style={{ width: '100%', padding: '10px', margin: '10px 0' }}>
          {isLoginMode ? 'Login' : 'Register'}
        </button>
      </form>
      <button
        onClick={() => setIsLoginMode(!isLoginMode)}
        style={{ width: '100%', padding: '10px' }}
      >
        {isLoginMode ? 'Need an account? Register' : 'Have an account? Login'}
      </button>
    </div>
  );
}
`;
}
