import axios from 'axios';
import type { AxiosInstance, AxiosError } from 'axios';
import type { LoginRequest, LoginResponse, RegisterRequest, User } from '../types';

const API_BASE_URL = import.meta.env.VITE_API_URL || 'https://localhost:5001';

class ApiService {
  private client: AxiosInstance;

  /** Expose the axios client for use by sub-API modules */
  getClient(): AxiosInstance {
    return this.client;
  }

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
        const originalRequest = error.config as any;
        if (error.response?.status === 401 && !originalRequest?._retry) {
          originalRequest._retry = true;
          const hasToken = !!localStorage.getItem('accessToken');
          // Only attempt refresh if we had a token and this isn't the refresh endpoint itself
          if (hasToken && !originalRequest.url?.includes('/auth/refresh')) {
            try {
              await this.refreshToken();
              return this.client.request(originalRequest);
            } catch {
              localStorage.removeItem('accessToken');
              window.location.href = '/auth/login';
            }
          } else if (hasToken) {
            // Refresh endpoint itself returned 401 — session is gone
            localStorage.removeItem('accessToken');
            window.location.href = '/auth/login';
          }
          // No token: just reject — caller handles the error (e.g. login page ignores provider 401s)
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
    const response = await this.client.get<{ items: User[]; totalCount: number }>('/users?pageSize=100');
    return response.data.items ?? [];
  }

  async getUserCount(): Promise<number> {
    const response = await this.client.get<{ totalCount: number }>('/users?pageSize=1');
    return response.data.totalCount ?? 0;
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

  async changeUserPassword(id: string, newPassword: string): Promise<void> {
    await this.client.post(`/users/${id}/change-password`, { newPassword });
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

  // Identity Provider endpoints
  async getIdentityProviders(tenantId?: string): Promise<any[]> {
    const params = tenantId ? { tenantId } : {};
    const response = await this.client.get('/identity-providers', { params });
    return response.data;
  }

  async getIdentityProvider(id: string): Promise<any> {
    const response = await this.client.get(`/identity-providers/${id}`);
    return response.data;
  }

  async createIdentityProvider(data: any): Promise<any> {
    const response = await this.client.post('/identity-providers', data);
    return response.data;
  }

  async updateIdentityProvider(id: string, data: any): Promise<any> {
    const response = await this.client.put(`/identity-providers/${id}`, data);
    return response.data;
  }

  async deleteIdentityProvider(id: string): Promise<void> {
    await this.client.delete(`/identity-providers/${id}`);
  }

  // Social Auth endpoints
  async getSocialAuthUrl(providerId: string, redirectUri: string): Promise<{ authorizationUrl: string; state: string }> {
    const response = await this.client.get(`/auth/social/${providerId}/authorize`, {
      params: { redirectUri }
    });
    return response.data;
  }

  async socialAuthCallback(providerId: string, code: string, state: string): Promise<LoginResponse> {
    const response = await this.client.post<LoginResponse>(`/auth/social/${providerId}/callback`, { code, state });
    if (response.data.accessToken) {
      localStorage.setItem('accessToken', response.data.accessToken);
    }
    return response.data;
  }
  // Invitation endpoints
  async getInvitations(tenantId: string): Promise<any[]> {
    const response = await this.client.get('/invitations', { params: { tenantId } });
    return response.data;
  }

  async getPendingInvitations(tenantId: string): Promise<any[]> {
    const response = await this.client.get('/invitations/pending', { params: { tenantId } });
    return response.data;
  }

  async sendInvitation(data: { email: string; tenantId: string; roleId: string; expiryDays?: number }): Promise<any> {
    const response = await this.client.post('/invitations', data);
    return response.data;
  }

  async sendBulkInvitations(tenantId: string, file: File): Promise<any> {
    const formData = new FormData();
    formData.append('file', file);
    formData.append('tenantId', tenantId);
    const response = await this.client.post('/invitations/bulk', formData, {
      headers: { 'Content-Type': 'multipart/form-data' },
    });
    return response.data;
  }

  async revokeInvitation(id: string): Promise<void> {
    await this.client.delete(`/invitations/${id}`);
  }

  async getInvitationByToken(token: string): Promise<any> {
    const response = await this.client.get(`/invitations/by-token/${token}`);
    return response.data;
  }

  async acceptInvitation(token: string, data: { password?: string; firstName?: string; lastName?: string }): Promise<any> {
    const response = await this.client.post(`/invitations/${token}/accept`, data);
    return response.data;
  }

  // Organization settings endpoints
  async getOrganizationSettings(tenantId: string): Promise<any> {
    const response = await this.client.get(`/organization-settings/${tenantId}`);
    return response.data;
  }

  async updateOrganizationSettings(tenantId: string, data: any): Promise<any> {
    const response = await this.client.put(`/organization-settings/${tenantId}`, data);
    return response.data;
  }
}

export const api = new ApiService();
