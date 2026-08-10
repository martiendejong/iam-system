import { apiClient } from '../lib/axios';
import type { Tenant, PaginatedResponse } from '../types';

export const tenantsApi = {
  getAll: async (page: number = 1, pageSize: number = 10): Promise<PaginatedResponse<Tenant>> => {
    const response = await apiClient.get('/tenants', {
      params: { page, pageSize },
    });
    return response.data;
  },

  getById: async (id: string): Promise<Tenant> => {
    const response = await apiClient.get(`/tenants/${id}`);
    return response.data;
  },

  create: async (data: Partial<Tenant>): Promise<Tenant> => {
    const response = await apiClient.post('/tenants', data);
    return response.data;
  },

  update: async (id: string, data: Partial<Tenant>): Promise<Tenant> => {
    const response = await apiClient.put(`/tenants/${id}`, data);
    return response.data;
  },

  delete: async (id: string): Promise<void> => {
    await apiClient.delete(`/tenants/${id}`);
  },

  deactivate: async (id: string): Promise<void> => {
    await apiClient.patch(`/tenants/${id}/deactivate`);
  },

  activate: async (id: string): Promise<void> => {
    await apiClient.patch(`/tenants/${id}/activate`);
  },
};
