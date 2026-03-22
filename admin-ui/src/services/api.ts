import axios from 'axios';
import type { AxiosInstance, AxiosError } from 'axios';
import type { LoginRequest, LoginResponse, RegisterRequest, User } from '../types';

const API_BASE_URL = import.meta.env.VITE_API_URL || 'https://localhost:5001';

class ApiService {
  private client: AxiosInstance;

  constructor() {
    this.client = axios.create({
      baseURL: `${API_BASE_URL}/api`,
      headers: {
        'Content-Type': 'application/json',
      },
      withCredentials: true, // For refresh token cookies
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

    // Add response interceptor for error handling
    this.client.interceptors.response.use(
      (response) => response,
      async (error: AxiosError) => {
        if (error.response?.status === 401) {
          // Try to refresh token
          try {
            await this.refreshToken();
            // Retry the failed request
            return this.client.request(error.config!);
          } catch {
            // Refresh failed, clear auth and redirect to login
            localStorage.removeItem('accessToken');
            window.location.href = '/login';
          }
        }
        return Promise.reject(error);
      }
    );
  }

  // Auth endpoints
  async login(data: LoginRequest): Promise<LoginResponse> {
    const response = await this.client.post<LoginResponse>('/auth/login', data);
    if (response.data.accessToken) {
      localStorage.setItem('accessToken', response.data.accessToken);
    }
    return response.data;
  }

  async register(data: RegisterRequest): Promise<void> {
    await this.client.post('/auth/register', data);
  }

  async logout(): Promise<void> {
    await this.client.post('/auth/logout');
    localStorage.removeItem('accessToken');
  }

  async refreshToken(): Promise<void> {
    const response = await this.client.post<{ accessToken: string }>('/auth/refresh');
    localStorage.setItem('accessToken', response.data.accessToken);
  }

  async getCurrentUser(): Promise<User> {
    const response = await this.client.get<User>('/users/me');
    return response.data;
  }

  // User endpoints
  async getUsers(): Promise<User[]> {
    const response = await this.client.get<User[]>('/users');
    return response.data;
  }

  async getUser(id: string): Promise<User> {
    const response = await this.client.get<User>(`/users/${id}`);
    return response.data;
  }

  async updateUser(id: string, data: Partial<User>): Promise<User> {
    const response = await this.client.put<User>(`/users/${id}`, data);
    return response.data;
  }

  async activateUser(id: string): Promise<void> {
    await this.client.put(`/users/${id}/activate`);
  }

  async deactivateUser(id: string): Promise<void> {
    await this.client.put(`/users/${id}/deactivate`);
  }

  async getUserRoles(id: string): Promise<any[]> {
    const response = await this.client.get(`/users/${id}/roles`);
    return response.data;
  }

  // Role endpoints
  async getRoles(): Promise<any[]> {
    const response = await this.client.get('/roles');
    return response.data;
  }

  async getRole(id: string): Promise<any> {
    const response = await this.client.get(`/roles/${id}`);
    return response.data;
  }

  async createRole(data: { name: string; description?: string }): Promise<any> {
    const response = await this.client.post('/roles', data);
    return response.data;
  }

  async updateRole(id: string, data: { name: string; description?: string }): Promise<any> {
    const response = await this.client.put(`/roles/${id}`, data);
    return response.data;
  }

  async deleteRole(id: string): Promise<void> {
    await this.client.delete(`/roles/${id}`);
  }

  async assignRole(roleId: string, userId: string, tenantId?: string): Promise<void> {
    await this.client.post(`/roles/${roleId}/assign`, { userId, tenantId });
  }

  async revokeRole(roleId: string, userId: string, tenantId?: string): Promise<void> {
    await this.client.post(`/roles/${roleId}/revoke`, { userId, tenantId });
  }

  // Tenant endpoints
  async getTenants(): Promise<any[]> {
    const response = await this.client.get('/tenants');
    return response.data;
  }

  async getTenant(id: string): Promise<any> {
    const response = await this.client.get(`/tenants/${id}`);
    return response.data;
  }

  async createTenant(data: any): Promise<any> {
    const response = await this.client.post('/tenants', data);
    return response.data;
  }

  async updateTenant(id: string, data: any): Promise<any> {
    const response = await this.client.put(`/tenants/${id}`, data);
    return response.data;
  }

  async deleteTenant(id: string): Promise<void> {
    await this.client.delete(`/tenants/${id}`);
  }

  async getTenantHierarchy(id: string): Promise<any> {
    const response = await this.client.get(`/tenants/${id}/hierarchy`);
    return response.data;
  }

  // OAuth2 Client endpoints
  async getOAuth2Clients(): Promise<any[]> {
    const response = await this.client.get('/oauth/clients');
    return response.data;
  }

  async getOAuth2Client(id: string): Promise<any> {
    const response = await this.client.get(`/oauth/clients/${id}`);
    return response.data;
  }

  async createOAuth2Client(data: any): Promise<any> {
    const response = await this.client.post('/oauth/clients', data);
    return response.data;
  }

  async updateOAuth2Client(id: string, data: any): Promise<any> {
    const response = await this.client.put(`/oauth/clients/${id}`, data);
    return response.data;
  }

  async deleteOAuth2Client(id: string): Promise<void> {
    await this.client.delete(`/oauth/clients/${id}`);
  }
}

export const api = new ApiService();
