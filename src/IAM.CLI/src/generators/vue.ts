import fs from 'fs-extra';
import path from 'path';

interface GeneratorConfig {
  apiUrl: string;
  typescript: boolean;
}

export async function generateVueAuth(cwd: string, config: GeneratorConfig): Promise<void> {
  const { apiUrl, typescript } = config;
  const ext = typescript ? 'ts' : 'js';
  const authDir = path.join(cwd, 'src', 'composables');

  // Ensure composables directory exists
  await fs.ensureDir(authDir);

  // Generate useAuth composable
  const useAuthContent = generateUseAuth(apiUrl, typescript);
  await fs.writeFile(path.join(authDir, `useAuth.${ext}`), useAuthContent);

  // Generate example LoginView component
  const loginViewDir = path.join(cwd, 'src', 'views');
  await fs.ensureDir(loginViewDir);
  const loginViewContent = generateLoginView(typescript);
  await fs.writeFile(path.join(loginViewDir, 'LoginView.vue'), loginViewContent);
}

function generateUseAuth(apiUrl: string, typescript: boolean): string {
  if (typescript) {
    return `import { ref, computed, Ref } from 'vue';
import { IamAuthClient, UserDto, LoginResponse } from '@iam-system/sdk';

interface UseAuthReturn {
  user: Ref<UserDto | null>;
  accessToken: Ref<string | null>;
  isAuthenticated: Ref<boolean>;
  isLoading: Ref<boolean>;
  login: (email: string, password: string) => Promise<void>;
  register: (email: string, password: string, firstName: string, lastName: string) => Promise<void>;
  logout: () => Promise<void>;
  refreshAuth: () => Promise<void>;
}

const user = ref<UserDto | null>(null);
const accessToken = ref<string | null>(null);
const isLoading = ref(true);

const client = new IamAuthClient({
  apiBaseUrl: '${apiUrl}',
  autoRefreshTokens: false,
  onTokenRefreshed: (newAccessToken: string, newRefreshToken?: string) => {
    accessToken.value = newAccessToken;
    if (newRefreshToken) {
      localStorage.setItem('refreshToken', newRefreshToken);
    }
  },
  onAuthenticationFailed: () => {
    user.value = null;
    accessToken.value = null;
    localStorage.removeItem('refreshToken');
  },
});

// Try to restore session on module initialization
const storedRefreshToken = localStorage.getItem('refreshToken');
if (storedRefreshToken) {
  client
    .refreshToken(storedRefreshToken)
    .then((response: LoginResponse) => {
      user.value = response.user;
      accessToken.value = response.accessToken;
      if (response.refreshToken) {
        localStorage.setItem('refreshToken', response.refreshToken);
      }
    })
    .catch(() => {
      localStorage.removeItem('refreshToken');
    })
    .finally(() => {
      isLoading.value = false;
    });
} else {
  isLoading.value = false;
}

export function useAuth(): UseAuthReturn {
  const isAuthenticated = computed(() => !!user.value);

  const login = async (email: string, password: string): Promise<void> => {
    const response = await client.login(email, password);
    user.value = response.user;
    accessToken.value = response.accessToken;
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
    user.value = response.user;
    accessToken.value = response.accessToken;
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
    user.value = null;
    accessToken.value = null;
    localStorage.removeItem('refreshToken');
  };

  const refreshAuth = async (): Promise<void> => {
    const refreshToken = localStorage.getItem('refreshToken');
    if (!refreshToken) {
      throw new Error('No refresh token available');
    }
    const response = await client.refreshToken(refreshToken);
    user.value = response.user;
    accessToken.value = response.accessToken;
    if (response.refreshToken) {
      localStorage.setItem('refreshToken', response.refreshToken);
    }
  };

  return {
    user,
    accessToken,
    isAuthenticated,
    isLoading,
    login,
    register,
    logout,
    refreshAuth,
  };
}
`;
  } else {
    return `import { ref, computed } from 'vue';
import { IamAuthClient } from '@iam-system/sdk';

const user = ref(null);
const accessToken = ref(null);
const isLoading = ref(true);

const client = new IamAuthClient({
  apiBaseUrl: '${apiUrl}',
  autoRefreshTokens: false,
  onTokenRefreshed: (newAccessToken, newRefreshToken) => {
    accessToken.value = newAccessToken;
    if (newRefreshToken) {
      localStorage.setItem('refreshToken', newRefreshToken);
    }
  },
  onAuthenticationFailed: () => {
    user.value = null;
    accessToken.value = null;
    localStorage.removeItem('refreshToken');
  },
});

// Try to restore session on module initialization
const storedRefreshToken = localStorage.getItem('refreshToken');
if (storedRefreshToken) {
  client
    .refreshToken(storedRefreshToken)
    .then((response) => {
      user.value = response.user;
      accessToken.value = response.accessToken;
      if (response.refreshToken) {
        localStorage.setItem('refreshToken', response.refreshToken);
      }
    })
    .catch(() => {
      localStorage.removeItem('refreshToken');
    })
    .finally(() => {
      isLoading.value = false;
    });
} else {
  isLoading.value = false;
}

export function useAuth() {
  const isAuthenticated = computed(() => !!user.value);

  const login = async (email, password) => {
    const response = await client.login(email, password);
    user.value = response.user;
    accessToken.value = response.accessToken;
    if (response.refreshToken) {
      localStorage.setItem('refreshToken', response.refreshToken);
    }
  };

  const register = async (email, password, firstName, lastName) => {
    const response = await client.register(email, password, firstName, lastName);
    user.value = response.user;
    accessToken.value = response.accessToken;
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
    user.value = null;
    accessToken.value = null;
    localStorage.removeItem('refreshToken');
  };

  const refreshAuth = async () => {
    const refreshToken = localStorage.getItem('refreshToken');
    if (!refreshToken) {
      throw new Error('No refresh token available');
    }
    const response = await client.refreshToken(refreshToken);
    user.value = response.user;
    accessToken.value = response.accessToken;
    if (response.refreshToken) {
      localStorage.setItem('refreshToken', response.refreshToken);
    }
  };

  return {
    user,
    accessToken,
    isAuthenticated,
    isLoading,
    login,
    register,
    logout,
    refreshAuth,
  };
}
`;
  }
}

function generateLoginView(typescript: boolean): string {
  const scriptLang = typescript ? ' lang="ts"' : '';
  return `<template>
  <div class="login-container">
    <div v-if="isLoading">Loading...</div>

    <div v-else-if="isAuthenticated && user" class="authenticated">
      <h1>Welcome, {{ user.firstName }} {{ user.lastName }}!</h1>
      <p>Email: {{ user.email }}</p>
      <button @click="handleLogout">Logout</button>
    </div>

    <div v-else class="auth-form">
      <h1>{{ isLoginMode ? 'Login' : 'Register' }}</h1>
      <form @submit.prevent="handleSubmit">
        <div v-if="!isLoginMode">
          <input
            v-model="firstName"
            type="text"
            placeholder="First Name"
            required
          />
          <input
            v-model="lastName"
            type="text"
            placeholder="Last Name"
            required
          />
        </div>
        <input
          v-model="email"
          type="email"
          placeholder="Email"
          required
        />
        <input
          v-model="password"
          type="password"
          placeholder="Password"
          required
        />
        <div v-if="error" class="error">{{ error }}</div>
        <button type="submit">{{ isLoginMode ? 'Login' : 'Register' }}</button>
      </form>
      <button @click="isLoginMode = !isLoginMode">
        {{ isLoginMode ? 'Need an account? Register' : 'Have an account? Login' }}
      </button>
    </div>
  </div>
</template>

<script setup${scriptLang}>
import { ref } from 'vue';
import { useAuth } from '@/composables/useAuth';

const { user, isAuthenticated, isLoading, login, register, logout } = useAuth();

const isLoginMode = ref(true);
const email = ref('');
const password = ref('');
const firstName = ref('');
const lastName = ref('');
const error = ref${typescript ? '<string | null>' : ''}(null);

const handleSubmit = async () => {
  error.value = null;

  try {
    if (isLoginMode.value) {
      await login(email.value, password.value);
    } else {
      await register(email.value, password.value, firstName.value, lastName.value);
    }
  } catch (err${typescript ? ': any' : ''}) {
    error.value = err.message || 'Authentication failed';
  }
};

const handleLogout = async () => {
  try {
    await logout();
  } catch (err${typescript ? ': any' : ''}) {
    error.value = err.message || 'Logout failed';
  }
};
</script>

<style scoped>
.login-container {
  max-width: 400px;
  margin: 50px auto;
  padding: 20px;
}

.auth-form input {
  display: block;
  width: 100%;
  margin: 10px 0;
  padding: 10px;
  border: 1px solid #ddd;
  border-radius: 4px;
}

.auth-form button {
  width: 100%;
  padding: 10px;
  margin: 10px 0;
  background-color: #007bff;
  color: white;
  border: none;
  border-radius: 4px;
  cursor: pointer;
}

.auth-form button:hover {
  background-color: #0056b3;
}

.error {
  color: red;
  margin: 10px 0;
}

.authenticated {
  text-align: center;
}
</style>
`;
}
