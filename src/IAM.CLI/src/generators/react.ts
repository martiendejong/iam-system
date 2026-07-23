import fs from 'fs-extra';
import path from 'path';

interface GeneratorConfig {
  apiUrl: string;
  typescript: boolean;
}

export async function generateReactAuth(cwd: string, config: GeneratorConfig): Promise<void> {
  const { apiUrl, typescript } = config;
  const ext = typescript ? 'tsx' : 'jsx';
  const authDir = path.join(cwd, 'src', 'auth');

  // Ensure auth directory exists
  await fs.ensureDir(authDir);

  // Generate AuthContext
  const authContextContent = generateAuthContext(apiUrl, typescript);
  await fs.writeFile(path.join(authDir, `AuthContext.${ext}`), authContextContent);

  // Generate useAuth hook
  const useAuthContent = generateUseAuthHook(typescript);
  await fs.writeFile(path.join(authDir, `useAuth.${ext}`), useAuthContent);

  // Generate index file for easy imports
  const indexContent = generateIndexFile(typescript);
  await fs.writeFile(path.join(authDir, `index.${typescript ? 'ts' : 'js'}`), indexContent);

  // Generate example LoginPage component
  const loginPageContent = generateLoginPage(typescript);
  await fs.writeFile(path.join(authDir, `LoginPage.${ext}`), loginPageContent);
}

function generateAuthContext(apiUrl: string, typescript: boolean): string {
  if (typescript) {
    return `import React, { createContext, useState, useEffect, ReactNode } from 'react';
import { IamAuthClient, UserDto, LoginResponse } from '@iam-system/sdk';

interface AuthContextType {
  user: UserDto | null;
  accessToken: string | null;
  isAuthenticated: boolean;
  isLoading: boolean;
  login: (email: string, password: string) => Promise<void>;
  register: (email: string, password: string, firstName: string, lastName: string) => Promise<void>;
  logout: () => Promise<void>;
  refreshAuth: () => Promise<void>;
}

export const AuthContext = createContext<AuthContextType | undefined>(undefined);

interface AuthProviderProps {
  children: ReactNode;
}

export const AuthProvider: React.FC<AuthProviderProps> = ({ children }) => {
  const [user, setUser] = useState<UserDto | null>(null);
  const [accessToken, setAccessToken] = useState<string | null>(null);
  const [isLoading, setIsLoading] = useState(true);

  const client = new IamAuthClient({
    apiBaseUrl: '${apiUrl}',
    autoRefreshTokens: false,
    onTokenRefreshed: (newAccessToken, newRefreshToken) => {
      setAccessToken(newAccessToken);
      if (newRefreshToken) {
        localStorage.setItem('refreshToken', newRefreshToken);
      }
    },
    onAuthenticationFailed: () => {
      setUser(null);
      setAccessToken(null);
      localStorage.removeItem('refreshToken');
    },
  });

  useEffect(() => {
    // Try to restore session on mount
    const storedRefreshToken = localStorage.getItem('refreshToken');
    if (storedRefreshToken) {
      client
        .refreshToken(storedRefreshToken)
        .then((response: LoginResponse) => {
          setUser(response.user);
          setAccessToken(response.accessToken);
          if (response.refreshToken) {
            localStorage.setItem('refreshToken', response.refreshToken);
          }
        })
        .catch(() => {
          localStorage.removeItem('refreshToken');
        })
        .finally(() => {
          setIsLoading(false);
        });
    } else {
      setIsLoading(false);
    }
  }, []);

  const login = async (email: string, password: string): Promise<void> => {
    const response = await client.login(email, password);
    setUser(response.user);
    setAccessToken(response.accessToken);
    if (response.refreshToken) {
      localStorage.setItem('refreshToken', response.refreshToken);
    }
  };

  const register = async (
    email: string,
    password: string,
    firstName: string,
    lastName: string
  ): Promise<void> => {
    const response = await client.register(email, password, firstName, lastName);
    setUser(response.user);
    setAccessToken(response.accessToken);
    if (response.refreshToken) {
      localStorage.setItem('refreshToken', response.refreshToken);
    }
  };

  const logout = async (): Promise<void> => {
    const refreshToken = localStorage.getItem('refreshToken');
    if (refreshToken) {
      try {
        await client.logout(refreshToken);
      } catch {
        // Ignore logout errors
      }
    }
    setUser(null);
    setAccessToken(null);
    localStorage.removeItem('refreshToken');
  };

  const refreshAuth = async (): Promise<void> => {
    const refreshToken = localStorage.getItem('refreshToken');
    if (!refreshToken) {
      throw new Error('No refresh token available');
    }
    const response = await client.refreshToken(refreshToken);
    setUser(response.user);
    setAccessToken(response.accessToken);
    if (response.refreshToken) {
      localStorage.setItem('refreshToken', response.refreshToken);
    }
  };

  const value: AuthContextType = {
    user,
    accessToken,
    isAuthenticated: !!user,
    isLoading,
    login,
    register,
    logout,
    refreshAuth,
  };

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
};
`;
  } else {
    return `import React, { createContext, useState, useEffect } from 'react';
import { IamAuthClient } from '@iam-system/sdk';

export const AuthContext = createContext(undefined);

export const AuthProvider = ({ children }) => {
  const [user, setUser] = useState(null);
  const [accessToken, setAccessToken] = useState(null);
  const [isLoading, setIsLoading] = useState(true);

  const client = new IamAuthClient({
    apiBaseUrl: '${apiUrl}',
    autoRefreshTokens: false,
    onTokenRefreshed: (newAccessToken, newRefreshToken) => {
      setAccessToken(newAccessToken);
      if (newRefreshToken) {
        localStorage.setItem('refreshToken', newRefreshToken);
      }
    },
    onAuthenticationFailed: () => {
      setUser(null);
      setAccessToken(null);
      localStorage.removeItem('refreshToken');
    },
  });

  useEffect(() => {
    // Try to restore session on mount
    const storedRefreshToken = localStorage.getItem('refreshToken');
    if (storedRefreshToken) {
      client
        .refreshToken(storedRefreshToken)
        .then((response) => {
          setUser(response.user);
          setAccessToken(response.accessToken);
          if (response.refreshToken) {
            localStorage.setItem('refreshToken', response.refreshToken);
          }
        })
        .catch(() => {
          localStorage.removeItem('refreshToken');
        })
        .finally(() => {
          setIsLoading(false);
        });
    } else {
      setIsLoading(false);
    }
  }, []);

  const login = async (email, password) => {
    const response = await client.login(email, password);
    setUser(response.user);
    setAccessToken(response.accessToken);
    if (response.refreshToken) {
      localStorage.setItem('refreshToken', response.refreshToken);
    }
  };

  const register = async (email, password, firstName, lastName) => {
    const response = await client.register(email, password, firstName, lastName);
    setUser(response.user);
    setAccessToken(response.accessToken);
    if (response.refreshToken) {
      localStorage.setItem('refreshToken', response.refreshToken);
    }
  };

  const logout = async () => {
    const refreshToken = localStorage.getItem('refreshToken');
    if (refreshToken) {
      try {
        await client.logout(refreshToken);
      } catch {
        // Ignore logout errors
      }
    }
    setUser(null);
    setAccessToken(null);
    localStorage.removeItem('refreshToken');
  };

  const refreshAuth = async () => {
    const refreshToken = localStorage.getItem('refreshToken');
    if (!refreshToken) {
      throw new Error('No refresh token available');
    }
    const response = await client.refreshToken(refreshToken);
    setUser(response.user);
    setAccessToken(response.accessToken);
    if (response.refreshToken) {
      localStorage.setItem('refreshToken', response.refreshToken);
    }
  };

  const value = {
    user,
    accessToken,
    isAuthenticated: !!user,
    isLoading,
    login,
    register,
    logout,
    refreshAuth,
  };

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
};
`;
  }
}

function generateUseAuthHook(typescript: boolean): string {
  if (typescript) {
    return `import { useContext } from 'react';
import { AuthContext } from './AuthContext';

export const useAuth = () => {
  const context = useContext(AuthContext);
  if (context === undefined) {
    throw new Error('useAuth must be used within an AuthProvider');
  }
  return context;
};
`;
  } else {
    return `import { useContext } from 'react';
import { AuthContext } from './AuthContext';

export const useAuth = () => {
  const context = useContext(AuthContext);
  if (context === undefined) {
    throw new Error('useAuth must be used within an AuthProvider');
  }
  return context;
};
`;
  }
}

function generateIndexFile(typescript: boolean): string {
  return `export { AuthProvider, AuthContext } from './AuthContext';
export { useAuth } from './useAuth';
export { LoginPage } from './LoginPage';
`;
}

function generateLoginPage(typescript: boolean): string {
  if (typescript) {
    return `import React, { useState } from 'react';
import { useAuth } from './useAuth';

export const LoginPage: React.FC = () => {
  const { login, register, isAuthenticated, user, logout, isLoading } = useAuth();
  const [isLoginMode, setIsLoginMode] = useState(true);
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [firstName, setFirstName] = useState('');
  const [lastName, setLastName] = useState('');
  const [error, setError] = useState<string | null>(null);

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    setError(null);

    try {
      if (isLoginMode) {
        await login(email, password);
      } else {
        await register(email, password, firstName, lastName);
      }
    } catch (err: any) {
      setError(err.message || 'Authentication failed');
    }
  };

  if (isLoading) {
    return <div>Loading...</div>;
  }

  if (isAuthenticated && user) {
    return (
      <div>
        <h1>Welcome, {user.firstName} {user.lastName}!</h1>
        <p>Email: {user.email}</p>
        <button onClick={logout}>Logout</button>
      </div>
    );
  }

  return (
    <div>
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
            />
            <input
              type="text"
              placeholder="Last Name"
              value={lastName}
              onChange={(e) => setLastName(e.target.value)}
              required
            />
          </>
        )}
        <input
          type="email"
          placeholder="Email"
          value={email}
          onChange={(e) => setEmail(e.target.value)}
          required
        />
        <input
          type="password"
          placeholder="Password"
          value={password}
          onChange={(e) => setPassword(e.target.value)}
          required
        />
        {error && <div style={{ color: 'red' }}>{error}</div>}
        <button type="submit">{isLoginMode ? 'Login' : 'Register'}</button>
      </form>
      <button onClick={() => setIsLoginMode(!isLoginMode)}>
        {isLoginMode ? 'Need an account? Register' : 'Have an account? Login'}
      </button>
    </div>
  );
};
`;
  } else {
    return `import React, { useState } from 'react';
import { useAuth } from './useAuth';

export const LoginPage = () => {
  const { login, register, isAuthenticated, user, logout, isLoading } = useAuth();
  const [isLoginMode, setIsLoginMode] = useState(true);
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [firstName, setFirstName] = useState('');
  const [lastName, setLastName] = useState('');
  const [error, setError] = useState(null);

  const handleSubmit = async (e) => {
    e.preventDefault();
    setError(null);

    try {
      if (isLoginMode) {
        await login(email, password);
      } else {
        await register(email, password, firstName, lastName);
      }
    } catch (err) {
      setError(err.message || 'Authentication failed');
    }
  };

  if (isLoading) {
    return <div>Loading...</div>;
  }

  if (isAuthenticated && user) {
    return (
      <div>
        <h1>Welcome, {user.firstName} {user.lastName}!</h1>
        <p>Email: {user.email}</p>
        <button onClick={logout}>Logout</button>
      </div>
    );
  }

  return (
    <div>
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
            />
            <input
              type="text"
              placeholder="Last Name"
              value={lastName}
              onChange={(e) => setLastName(e.target.value)}
              required
            />
          </>
        )}
        <input
          type="email"
          placeholder="Email"
          value={email}
          onChange={(e) => setEmail(e.target.value)}
          required
        />
        <input
          type="password"
          placeholder="Password"
          value={password}
          onChange={(e) => setPassword(e.target.value)}
          required
        />
        {error && <div style={{ color: 'red' }}>{error}</div>}
        <button type="submit">{isLoginMode ? 'Login' : 'Register'}</button>
      </form>
      <button onClick={() => setIsLoginMode(!isLoginMode)}>
        {isLoginMode ? 'Need an account? Register' : 'Have an account? Login'}
      </button>
    </div>
  );
};
`;
  }
}
