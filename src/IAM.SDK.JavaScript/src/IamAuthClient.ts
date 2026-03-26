import fetch from 'cross-fetch';
import type {
  IIamAuthClient,
  IamClientOptions,
  LoginResponse,
  RegisterResponse,
  UserDto,
  ErrorResponse,
} from './types';

/**
 * IAM Authentication Client
 *
 * Official JavaScript/TypeScript SDK for IAM System
 *
 * @example
 * ```typescript
 * import { IamAuthClient } from '@iam-system/sdk';
 *
 * const client = new IamAuthClient({
 *   apiBaseUrl: 'http://localhost:5161'
 * });
 *
 * // Login
 * const response = await client.login('user@example.com', 'password123');
 * console.log('Access Token:', response.accessToken);
 *
 * // Get current user
 * const user = await client.getCurrentUser();
 * console.log('User:', user.email);
 * ```
 */
export class IamAuthClient implements IIamAuthClient {
  private readonly baseUrl: string;
  private readonly timeout: number;
  private readonly customHeaders: Record<string, string>;
  private readonly onTokenRefreshed?: (
    accessToken: string,
    refreshToken?: string
  ) => void;
  private readonly onAuthenticationFailed?: (error: Error) => void;

  private accessToken: string | null = null;
  private refreshTokenValue: string | null = null;

  /**
   * Create a new IAM Authentication Client
   * @param options - Client configuration options
   */
  constructor(options: IamClientOptions) {
    this.baseUrl = options.apiBaseUrl.replace(/\/$/, ''); // Remove trailing slash
    this.timeout = (options.timeoutSeconds ?? 30) * 1000; // Convert to milliseconds
    this.customHeaders = options.headers ?? {};
    this.onTokenRefreshed = options.onTokenRefreshed;
    this.onAuthenticationFailed = options.onAuthenticationFailed;

    // Note: autoRefreshTokens option reserved for future automatic token refresh implementation
    // Currently token refresh must be called manually by the application
  }

  /**
   * Login with email and password
   */
  async login(email: string, password: string): Promise<LoginResponse> {
    const response = await this.fetch<LoginResponse>('/api/auth/login', {
      method: 'POST',
      body: JSON.stringify({ email, password }),
    });

    this.accessToken = response.accessToken;
    this.refreshTokenValue = response.refreshToken ?? null;

    if (this.onTokenRefreshed) {
      this.onTokenRefreshed(this.accessToken, this.refreshTokenValue ?? undefined);
    }

    return response;
  }

  /**
   * Register a new user
   */
  async register(
    email: string,
    password: string,
    firstName: string,
    lastName: string
  ): Promise<RegisterResponse> {
    return await this.fetch<RegisterResponse>('/api/auth/register', {
      method: 'POST',
      body: JSON.stringify({ email, password, firstName, lastName }),
    });
  }

  /**
   * Refresh access token
   */
  async refreshToken(refreshToken: string): Promise<LoginResponse> {
    const response = await this.fetch<LoginResponse>('/api/auth/refresh', {
      method: 'POST',
      body: JSON.stringify({ refreshToken }),
    });

    this.accessToken = response.accessToken;
    this.refreshTokenValue = response.refreshToken ?? refreshToken;

    if (this.onTokenRefreshed) {
      this.onTokenRefreshed(this.accessToken, this.refreshTokenValue ?? undefined);
    }

    return response;
  }

  /**
   * Get current authenticated user
   */
  async getCurrentUser(): Promise<UserDto> {
    return await this.fetch<UserDto>('/api/auth/me', {
      method: 'GET',
      authenticated: true,
    });
  }

  /**
   * Logout and revoke tokens
   */
  async logout(): Promise<void> {
    try {
      await this.fetch('/api/auth/logout', {
        method: 'POST',
        authenticated: true,
      });
    } finally {
      // Always clear tokens, even if logout request fails
      this.accessToken = null;
      this.refreshTokenValue = null;
    }
  }

  /**
   * Get current access token
   */
  getAccessToken(): string | null {
    return this.accessToken;
  }

  /**
   * Set access token manually
   */
  setAccessToken(accessToken: string): void {
    this.accessToken = accessToken;
  }

  /**
   * Check if user is authenticated
   */
  isAuthenticated(): boolean {
    return this.accessToken !== null;
  }

  /**
   * Internal fetch wrapper with automatic error handling
   */
  private async fetch<T>(
    path: string,
    options: {
      method: 'GET' | 'POST' | 'PUT' | 'DELETE';
      body?: string;
      authenticated?: boolean;
    }
  ): Promise<T> {
    const url = `${this.baseUrl}${path}`;

    const headers: Record<string, string> = {
      'Content-Type': 'application/json',
      ...this.customHeaders,
    };

    // Add authorization header if authenticated
    if (options.authenticated && this.accessToken) {
      headers['Authorization'] = `Bearer ${this.accessToken}`;
    }

    const controller = new AbortController();
    const timeoutId = setTimeout(() => controller.abort(), this.timeout);

    try {
      const response = await fetch(url, {
        method: options.method,
        headers,
        body: options.body,
        signal: controller.signal,
      });

      clearTimeout(timeoutId);

      // Handle non-OK responses
      if (!response.ok) {
        const errorData: ErrorResponse = await response.json().catch(() => ({
          error: `HTTP ${response.status}: ${response.statusText}`,
        }));

        const error = new Error(errorData.error || 'Request failed');
        (error as any).status = response.status;
        (error as any).validationErrors = errorData.validationErrors;

        // Call authentication failed callback if 401
        if (response.status === 401 && this.onAuthenticationFailed) {
          this.onAuthenticationFailed(error);
        }

        throw error;
      }

      // Handle 204 No Content
      if (response.status === 204) {
        return undefined as T;
      }

      // Parse JSON response
      return await response.json();
    } catch (error) {
      clearTimeout(timeoutId);

      if (error instanceof Error && error.name === 'AbortError') {
        throw new Error(`Request timeout after ${this.timeout}ms`);
      }

      throw error;
    }
  }
}
