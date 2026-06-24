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
    const d = response.data;
    // Backend returns a plain array; normalize to the { items, total } shape the UI expects.
    if (Array.isArray(d)) return { items: d, total: d.length };
    return { items: d?.items ?? [], total: d?.total ?? 0 };
  },

  getStatistics: async (startDate?: string, endDate?: string): Promise<AuditStatistics> => {
    // Backend requires both dates; default to the last 30 days when not supplied.
    const end = endDate ?? new Date().toISOString();
    const start = startDate ?? new Date(Date.now() - 30 * 24 * 60 * 60 * 1000).toISOString();
    const query = new URLSearchParams();
    query.append('startDate', start);
    query.append('endDate', end);
    const response = await client().get(`/audit/statistics?${query}`);
    const d = response.data ?? {};
    // Backend returns ComplianceStatistics (different field names + EventsByDay as a
    // dictionary). Normalize to the AuditStatistics shape the dashboard renders.
    const eventsByDayObj = d.eventsByDay ?? {};
    const eventsByDay = Array.isArray(eventsByDayObj)
      ? eventsByDayObj
      : Object.entries(eventsByDayObj).map(([date, count]) => ({ date, count: Number(count) }));
    return {
      totalEvents: d.totalEvents ?? d.totalEvaluations ?? 0,
      eventsByType: d.eventsByType ?? {},
      eventsByDay,
      failedEvents: d.failedEvents ?? d.failedAccessAttempts ?? 0,
      highRiskEvents: d.highRiskEvents ?? d.complianceViolations ?? 0,
    };
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
