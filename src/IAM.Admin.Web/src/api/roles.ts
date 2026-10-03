import { apiClient } from '../lib/axios';
import type { Role, PaginatedResponse } from '../types';

export const rolesApi = {
  getAll: async (page: number = 1, pageSize: number = 10): Promise<PaginatedResponse<Role>> => {
    const response = await apiClient.get('/roles', {
      params: { page, pageSize },
    });
    return response.data;
  },

  getById: async (id: string): Promise<Role> => {
    const response = await apiClient.get(`/roles/${id}`);
    return response.data;
  },

  create: async (data: Partial<Role>): Promise<Role> => {
    const response = await apiClient.post('/roles', data);
    return response.data;
  },

  update: async (id: string, data: Partial<Role>): Promise<Role> => {
    const response = await apiClient.put(`/roles/${id}`, data);
    return response.data;
  },

  delete: async (id: string): Promise<void> => {
    await apiClient.delete(`/roles/${id}`);
  },
};
