import { apiClient } from '../lib/axios';
import type { User, PaginatedResponse } from '../types';

export const usersApi = {
  getAll: async (page: number = 1, pageSize: number = 10): Promise<PaginatedResponse<User>> => {
    const response = await apiClient.get('/users', {
      params: { page, pageSize },
    });
    return response.data;
  },

  getById: async (id: string): Promise<User> => {
    const response = await apiClient.get(`/users/${id}`);
    return response.data;
  },

  create: async (data: Partial<User>): Promise<User> => {
    const response = await apiClient.post('/users', data);
    return response.data;
  },

  update: async (id: string, data: Partial<User>): Promise<User> => {
    const response = await apiClient.put(`/users/${id}`, data);
    return response.data;
  },

  delete: async (id: string): Promise<void> => {
    await apiClient.delete(`/users/${id}`);
  },

  deactivate: async (id: string): Promise<void> => {
    await apiClient.patch(`/users/${id}/deactivate`);
  },

  activate: async (id: string): Promise<void> => {
    await apiClient.patch(`/users/${id}/activate`);
  },
};
