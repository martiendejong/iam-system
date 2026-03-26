import axios from 'axios';
import type { AxiosInstance, AxiosError } from 'axios';

const API_BASE_URL = import.meta.env.VITE_API_URL || 'https://localhost:5001';

export interface ServiceAccount {
  id: string;
  name: string;
  clientId: string;
  clientSecret?: string; // Only present on create/rotate
  tenantId: string | null;
  tenantName: string | null;
  type: string;
  permissions: string[];
  certificateThumbprint: string | null;
  description: string | null;
  isActive: boolean;
  createdAt: string;
  updatedAt: string;
  lastAuthenticatedAt: string | null;
}

export interface CreateServiceAccountRequest {
  name: string;
  tenantId?: string | null;
  type: number; // 0=Api, 1=Service, 2=Worker
  permissions?: string[];
  certificateThumbprint?: string | null;
  description?: string | null;
}

export interface UpdateServiceAccountRequest {
  name?: string | null;
  permissions?: string[] | null;
  type?: number | null;
  certificateThumbprint?: string | null;
  description?: string | null;
  isActive?: boolean | null;
}

export interface TokenResponse {
  access_token: string;
  token_type: string;
  expires_at: string;
  expires_in: number;
}

export interface RotateSecretResponse {
  id: string;
  clientSecret: string;
  message: string;
  warning: string;
}

class ServiceAccountApiService {
  private client: AxiosInstance;

  constructor() {
    this.client = axios.create({
      baseURL: `${API_BASE_URL}/api`,
      headers: {
        'Content-Type': 'application/json',
      },
      withCredentials: true,
    });

    // Add request interceptor to add access token
    this.client.interceptors.request.use(
      (config) => {
        const token = localStorage.getItem('accessToken');
        if (token) {
          config.headers.Authorization = `Bearer ${token}`;
        }
        return config;
      },
      (error) => Promise.reject(error)
    );

    // Add response interceptor for 401 handling
    this.client.interceptors.response.use(
      (response) => response,
      async (error: AxiosError) => {
        if (error.response?.status === 401) {
          localStorage.removeItem('accessToken');
          window.location.href = '/login';
        }
        return Promise.reject(error);
      }
    );
  }

  async list(tenantId?: string, type?: string, isActive?: boolean): Promise<ServiceAccount[]> {
    const params: Record<string, string | boolean> = {};
    if (tenantId) params.tenantId = tenantId;
    if (type !== undefined && type !== '') params.type = type;
    if (isActive !== undefined) params.isActive = isActive;
    const response = await this.client.get<ServiceAccount[]>('/service-accounts', { params });
    return response.data;
  }

  async getById(id: string): Promise<ServiceAccount> {
    const response = await this.client.get<ServiceAccount>(`/service-accounts/${id}`);
    return response.data;
  }

  async create(data: CreateServiceAccountRequest): Promise<ServiceAccount> {
    const response = await this.client.post<ServiceAccount>('/service-accounts', data);
    return response.data;
  }

  async update(id: string, data: UpdateServiceAccountRequest): Promise<ServiceAccount> {
    const response = await this.client.put<ServiceAccount>(`/service-accounts/${id}`, data);
    return response.data;
  }

  async delete(id: string): Promise<void> {
    await this.client.delete(`/service-accounts/${id}`);
  }

  async rotateSecret(id: string): Promise<RotateSecretResponse> {
    const response = await this.client.post<RotateSecretResponse>(`/service-accounts/${id}/rotate-secret`);
    return response.data;
  }

  async testToken(clientId: string, clientSecret: string): Promise<TokenResponse> {
    const response = await this.client.post<TokenResponse>('/service-accounts/token', {
      grantType: 'client_credentials',
      clientId,
      clientSecret,
    });
    return response.data;
  }

  // Reuse for dropdowns
  async getTenants(): Promise<{ id: string; name: string; slug: string }[]> {
    const response = await this.client.get('/tenants');
    return response.data;
  }
}

export const serviceAccountApi = new ServiceAccountApiService();
