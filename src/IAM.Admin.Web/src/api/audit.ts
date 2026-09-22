import { apiClient } from '../lib/axios';
import type { AuditLog, PaginatedResponse } from '../types';

export const auditApi = {
  getAll: async (
    page: number = 1,
    pageSize: number = 20,
    filters?: {
      userId?: string;
      resourceType?: string;
      action?: string;
      startDate?: string;
      endDate?: string;
    }
  ): Promise<PaginatedResponse<AuditLog>> => {
    const response = await apiClient.get('/audit', {
      params: { page, pageSize, ...filters },
    });
    return response.data;
  },

  getById: async (id: string): Promise<AuditLog> => {
    const response = await apiClient.get(`/audit/${id}`);
    return response.data;
  },

  export: async (format: 'csv' | 'json', filters?: Record<string, unknown>): Promise<Blob> => {
    const response = await apiClient.get('/audit/export', {
      params: { format, ...filters },
      responseType: 'blob',
    });
    return response.data;
  },
};
