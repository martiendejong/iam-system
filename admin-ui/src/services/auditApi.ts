import { api } from './api';
import type { AuditEvent, AuditStatistics, ComplianceReport, AuditFilter } from '../types/audit';

const client = () => api.getClient();

export const auditApi = {
  getEvents: async (params?: AuditFilter & { page?: number; pageSize?: number }): Promise<{ items: AuditEvent[]; total: number }> => {
    const query = new URLSearchParams();
    if (params) {
      Object.entries(params).forEach(([key, value]) => {
        if (value !== undefined && value !== '') query.append(key, String(value));
      });
    }
    const response = await client().get(`/audit/events?${query}`);
    return response.data;
  },

  getStatistics: async (startDate?: string, endDate?: string): Promise<AuditStatistics> => {
    const query = new URLSearchParams();
    if (startDate) query.append('startDate', startDate);
    if (endDate) query.append('endDate', endDate);
    const response = await client().get(`/audit/statistics?${query}`);
    return response.data;
  },

  generateReport: async (framework: string): Promise<ComplianceReport> => {
    const response = await client().post('/audit/compliance-report', { framework });
    return response.data;
  },

  getAnomalies: async (): Promise<AuditEvent[]> => {
    const response = await client().get('/audit/anomalies');
    return response.data;
  },
};
