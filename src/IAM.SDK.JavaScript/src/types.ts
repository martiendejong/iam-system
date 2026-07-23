/**
 * IAM System SDK - TypeScript Type Definitions
 */

/**
 * User data transfer object
 */
export interface UserDto {
  id: string;
  email: string;
  firstName: string;
  lastName: string;
  emailConfirmed: boolean;
  isActive: boolean;
  createdAt: string;
  lastLoginAt?: string;
}

/**
 * Login request payload
 */
export interface LoginRequest {
  email: string;
  password: string;
}

/**
 * Login response with tokens and user data
 */
export interface LoginResponse {
  accessToken: string;
  refreshToken?: string;
  user: UserDto;
  expiresIn: number;
}

/**
 * Register request payload
 */
export interface RegisterRequest {
  email: string;
  password: string;
  firstName: string;
  lastName: string;
}

/**
 * Register response
 */
export interface RegisterResponse {
  message: string;
  userId: string;
}

/**
 * Refresh token request payload
 */
export interface RefreshTokenRequest {
  refreshToken: string;
}

/**
 * Error response from API
 */
export interface ErrorResponse {
  error: string;
  validationErrors?: Record<string, string[]>;
}

/**
 * IAM client configuration options
 */
export interface IamClientOptions {
  /**
   * Base URL of the IAM API (e.g., "https://api.example.com")
   */
  apiBaseUrl: string;

  /**
   * Request timeout in seconds (default: 30)
   */
  timeoutSeconds?: number;

  /**
   * Whether to automatically refresh tokens when they expire (default: true)
   */
  autoRefreshTokens?: boolean;

  /**
   * Custom headers to include in all requests
   */
  headers?: Record<string, string>;

  /**
   * Callback function when token is refreshed
   */
  onTokenRefreshed?: (accessToken: string, refreshToken?: string) => void;

  /**
   * Callback function when authentication fails
   */
  onAuthenticationFailed?: (error: Error) => void;
}

/**
 * IAM Authentication Client Interface
 */
export interface IIamAuthClient {
  /**
   * Login with email and password
   * @param email - User email address
   * @param password - User password
   * @returns Login response with tokens and user data
   */
  login(email: string, password: string): Promise<LoginResponse>;

  /**
   * Register a new user
   * @param email - User email address
   * @param password - User password
   * @param firstName - User first name
   * @param lastName - User last name
   * @returns Registration response
   */
  register(
    email: string,
    password: string,
    firstName: string,
    lastName: string
  ): Promise<RegisterResponse>;

  /**
   * Refresh access token using refresh token
   * @param refreshToken - The refresh token
   * @returns New login response with refreshed tokens
   */
  refreshToken(refreshToken: string): Promise<LoginResponse>;

  /**
   * Get current authenticated user
   * @returns Current user data
   */
  getCurrentUser(): Promise<UserDto>;

  /**
   * Logout and revoke tokens
   */
  logout(): Promise<void>;

  /**
   * Get current access token
   * @returns Access token or null if not authenticated
   */
  getAccessToken(): string | null;

  /**
   * Set access token manually
   * @param accessToken - The access token to set
   */
  setAccessToken(accessToken: string): void;

  /**
   * Check if user is authenticated
   * @returns True if user has valid access token
   */
  isAuthenticated(): boolean;
}
