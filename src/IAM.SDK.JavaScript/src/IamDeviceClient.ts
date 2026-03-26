import fetch from 'cross-fetch';
import type { ErrorResponse } from './types';

/**
 * Configuration options for the IAM device client
 */
export interface IamDeviceClientOptions {
  /**
   * Base URL of the IAM API (e.g., "https://iam.example.com")
   */
  apiBaseUrl: string;

  /**
   * Unique device identifier registered in the IAM system
   */
  deviceId: string;

  /**
   * Request timeout in seconds (default: 30)
   */
  timeoutSeconds?: number;

  /**
   * Custom headers to include in all requests
   */
  headers?: Record<string, string>;

  /**
   * Callback when authentication fails (e.g., token expired)
   */
  onAuthenticationFailed?: (error: Error) => void;

  /**
   * Callback when token is refreshed after re-authentication
   */
  onTokenRefreshed?: (accessToken: string) => void;
}

/**
 * Response from device authentication endpoints
 */
export interface DeviceAuthResponse {
  success: boolean;
  accessToken?: string;
  expiresIn: number;
  deviceId?: string;
  permissions: string[];
  error?: string;
}

/**
 * Response from device authorization endpoints
 */
export interface DeviceAuthorizeResponse {
  allowed: boolean;
  matchedPermission?: string;
  reason?: string;
}

/**
 * IAM Device Authentication Client
 *
 * Official JavaScript/TypeScript SDK for IoT device authentication
 * with the IAM System. Supports X.509 certificate and HMAC-SHA256
 * authentication, resource/MQTT authorization, and automatic heartbeat.
 *
 * @example
 * ```typescript
 * import { IamDeviceClient } from '@iam-system/sdk';
 *
 * const device = new IamDeviceClient({
 *   apiBaseUrl: 'https://iam.example.com',
 *   deviceId: 'sensor-001',
 * });
 *
 * // Authenticate with HMAC
 * await device.authenticateWithHmac('my-shared-secret');
 *
 * // Check authorization
 * const auth = await device.authorize('sensors/temperature', 'write');
 * if (auth.allowed) {
 *   console.log('Device is authorized');
 * }
 *
 * // Start automatic heartbeat
 * device.startHeartbeat(30000);
 *
 * // Cleanup
 * device.dispose();
 * ```
 */
export class IamDeviceClient {
  private readonly baseUrl: string;
  private readonly deviceId: string;
  private readonly timeout: number;
  private readonly customHeaders: Record<string, string>;
  private readonly onAuthenticationFailed?: (error: Error) => void;
  private readonly onTokenRefreshed?: (accessToken: string) => void;

  private accessToken: string | null = null;
  private tokenExpiry: number = 0; // Unix timestamp in ms
  private heartbeatIntervalId: ReturnType<typeof setInterval> | null = null;

  /**
   * Create a new IAM Device Authentication Client
   * @param options - Device client configuration options
   */
  constructor(options: IamDeviceClientOptions) {
    if (!options.deviceId) {
      throw new Error('deviceId is required');
    }

    this.baseUrl = options.apiBaseUrl.replace(/\/$/, '');
    this.deviceId = options.deviceId;
    this.timeout = (options.timeoutSeconds ?? 30) * 1000;
    this.customHeaders = options.headers ?? {};
    this.onAuthenticationFailed = options.onAuthenticationFailed;
    this.onTokenRefreshed = options.onTokenRefreshed;
  }

  /**
   * Whether the device is currently authenticated with a valid token
   */
  get isAuthenticated(): boolean {
    return this.accessToken !== null && this.tokenExpiry > Date.now();
  }

  /**
   * The current access token, or null if not authenticated or expired
   */
  getAccessToken(): string | null {
    return this.isAuthenticated ? this.accessToken : null;
  }

  /**
   * Authenticate using an X.509 certificate in PEM format
   * @param certificatePem - The X.509 certificate PEM string
   * @returns Device authentication response with token and permissions
   */
  async authenticateWithCertificate(
    certificatePem: string
  ): Promise<DeviceAuthResponse> {
    if (!certificatePem) {
      throw new Error('certificatePem is required');
    }

    const result = await this.fetchApi<DeviceAuthResponse>(
      '/api/device-auth/certificate',
      {
        method: 'POST',
        body: JSON.stringify({
          certificatePem,
          deviceId: this.deviceId,
        }),
      }
    );

    this.storeToken(result);
    return result;
  }

  /**
   * Authenticate using HMAC-SHA256 with a shared secret.
   * Generates a timestamped nonce-based signature to prevent replay attacks.
   *
   * Uses the Web Crypto API for HMAC-SHA256 computation, compatible
   * with browsers, Node.js 15+, and edge runtimes.
   *
   * @param sharedSecret - The shared secret key
   * @returns Device authentication response with token and permissions
   */
  async authenticateWithHmac(
    sharedSecret: string
  ): Promise<DeviceAuthResponse> {
    if (!sharedSecret) {
      throw new Error('sharedSecret is required');
    }

    const timestamp = Math.floor(Date.now() / 1000).toString();
    const nonce = this.generateNonce(16);
    const message = `${timestamp}:${nonce}:${this.deviceId}`;

    const hash = await this.computeHmacSha256(sharedSecret, message);
    const hmacPassword = `${timestamp}:${nonce}:${hash}`;

    const result = await this.fetchApi<DeviceAuthResponse>(
      '/api/device-auth/hmac',
      {
        method: 'POST',
        body: JSON.stringify({
          deviceId: this.deviceId,
          hmacPassword,
        }),
      }
    );

    this.storeToken(result);
    return result;
  }

  /**
   * Check if the device is authorized to perform an action on a resource
   * @param resource - The resource identifier (e.g., "sensors/temperature")
   * @param action - The action to check (e.g., "read", "write")
   * @returns Authorization response indicating whether the action is allowed
   */
  async authorize(
    resource: string,
    action: string
  ): Promise<DeviceAuthorizeResponse> {
    this.ensureAuthenticated();

    return await this.fetchApi<DeviceAuthorizeResponse>(
      '/api/device-auth/authorize',
      {
        method: 'POST',
        body: JSON.stringify({
          deviceId: this.deviceId,
          resource,
          action,
        }),
        authenticated: true,
      }
    );
  }

  /**
   * Check if the device is authorized to publish/subscribe to an MQTT topic
   * @param topic - The MQTT topic (e.g., "devices/sensor-01/telemetry")
   * @param action - The MQTT action ("publish" or "subscribe")
   * @returns Authorization response indicating whether the MQTT action is allowed
   */
  async authorizeMqtt(
    topic: string,
    action: string
  ): Promise<DeviceAuthorizeResponse> {
    this.ensureAuthenticated();

    return await this.fetchApi<DeviceAuthorizeResponse>(
      '/api/device-auth/authorize-mqtt',
      {
        method: 'POST',
        body: JSON.stringify({
          deviceId: this.deviceId,
          topic,
          action,
        }),
        authenticated: true,
      }
    );
  }

  /**
   * Send a heartbeat to indicate the device is still online
   */
  async heartbeat(): Promise<void> {
    this.ensureAuthenticated();

    await this.fetchApi('/api/device-auth/heartbeat', {
      method: 'POST',
      body: JSON.stringify({
        deviceId: this.deviceId,
      }),
      authenticated: true,
    });
  }

  /**
   * Start sending automatic heartbeats at the specified interval.
   * Previous heartbeat timer is stopped before starting a new one.
   * @param intervalMs - Interval between heartbeats in milliseconds (default: 60000)
   */
  startHeartbeat(intervalMs: number = 60000): void {
    if (intervalMs <= 0) {
      throw new Error('Interval must be positive');
    }

    this.stopHeartbeat();

    // Send first heartbeat immediately
    this.heartbeat().catch(() => {
      // Swallow errors on background heartbeat
    });

    this.heartbeatIntervalId = setInterval(() => {
      this.heartbeat().catch(() => {
        // Swallow errors on background heartbeat.
        // The server will mark the device as offline after missing heartbeats.
      });
    }, intervalMs);
  }

  /**
   * Stop the automatic heartbeat timer
   */
  stopHeartbeat(): void {
    if (this.heartbeatIntervalId !== null) {
      clearInterval(this.heartbeatIntervalId);
      this.heartbeatIntervalId = null;
    }
  }

  /**
   * Dispose the client: stop heartbeat and clear tokens
   */
  dispose(): void {
    this.stopHeartbeat();
    this.accessToken = null;
    this.tokenExpiry = 0;
  }

  // ========== Private Methods ==========

  private storeToken(result: DeviceAuthResponse): void {
    if (result.success && result.accessToken) {
      this.accessToken = result.accessToken;
      // Buffer 60 seconds before actual expiry to avoid edge-case failures
      this.tokenExpiry = Date.now() + (result.expiresIn - 60) * 1000;

      if (this.onTokenRefreshed) {
        this.onTokenRefreshed(this.accessToken);
      }
    }
  }

  private ensureAuthenticated(): void {
    if (!this.isAuthenticated) {
      throw new Error(
        'Device is not authenticated. Call authenticateWithCertificate() or authenticateWithHmac() first.'
      );
    }
  }

  /**
   * Compute HMAC-SHA256 using the Web Crypto API (cross-platform)
   */
  private async computeHmacSha256(
    secret: string,
    message: string
  ): Promise<string> {
    const encoder = new TextEncoder();

    const key = await crypto.subtle.importKey(
      'raw',
      encoder.encode(secret),
      { name: 'HMAC', hash: 'SHA-256' },
      false,
      ['sign']
    );

    const signature = await crypto.subtle.sign(
      'HMAC',
      key,
      encoder.encode(message)
    );

    // Convert ArrayBuffer to base64
    const bytes = new Uint8Array(signature);
    let binary = '';
    for (let i = 0; i < bytes.byteLength; i++) {
      binary += String.fromCharCode(bytes[i]);
    }
    return btoa(binary);
  }

  /**
   * Generate a random hex nonce of the specified length
   */
  private generateNonce(length: number): string {
    const array = new Uint8Array(Math.ceil(length / 2));
    crypto.getRandomValues(array);
    return Array.from(array, (b) => b.toString(16).padStart(2, '0'))
      .join('')
      .slice(0, length);
  }

  /**
   * Internal fetch wrapper with timeout, auth header, and error handling
   */
  private async fetchApi<T>(
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

      if (!response.ok) {
        const errorData: ErrorResponse = await response
          .json()
          .catch(() => ({
            error: `HTTP ${response.status}: ${response.statusText}`,
          }));

        const error = new Error(errorData.error || 'Request failed');
        (error as any).status = response.status;

        if (response.status === 401 && this.onAuthenticationFailed) {
          this.onAuthenticationFailed(error);
        }

        throw error;
      }

      if (response.status === 204) {
        return undefined as T;
      }

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
