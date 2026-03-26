import { api } from './api';

const client = api.getClient();

// ── Types ────────────────────────────────────────────────────────

export type AlertSeverity = 'Info' | 'Warning' | 'Critical';
export type SiemType = 'Syslog' | 'Webhook' | 'Splunk' | 'Elastic' | 'AzureSentinel';

export interface AlertRule {
  id: string;
  tenantId?: string;
  name: string;
  condition: string;
  severity: number;
  channels: string;
  cooldownMinutes: number;
  autoResponseAction?: string;
  isActive: boolean;
  createdAt: string;
  updatedAt: string;
}

export interface SecurityAlert {
  id: string;
  tenantId?: string;
  ruleId: string;
  severity: number;
  title: string;
  details?: string;
  acknowledgedAt?: string;
  acknowledgedByUserId?: string;
  acknowledgedByUser?: { firstName: string; lastName: string };
  autoResponseAction?: string;
  createdAt: string;
  rule?: AlertRule;
}

export interface SiemIntegration {
  id: string;
  tenantId?: string;
  name: string;
  type: number;
  endpointUrl: string;
  authConfig?: string;
  format: string;
  eventFilter?: string;
  isActive: boolean;
  createdAt: string;
  updatedAt: string;
}

export interface CreateAlertRuleRequest {
  tenantId?: string;
  name: string;
  condition?: string;
  severity?: number;
  channels?: string;
  cooldownMinutes?: number;
  autoResponseAction?: string;
  isActive?: boolean;
}

export interface CreateSiemIntegrationRequest {
  tenantId?: string;
  name: string;
  type: number;
  endpointUrl: string;
  authConfig?: string;
  format?: string;
  eventFilter?: string;
  isActive?: boolean;
}

// ── Alert Rules ──────────────────────────────────────────────────

export const alertsApi = {
  // Rules
  async getRules(tenantId?: string): Promise<AlertRule[]> {
    const params = tenantId ? { tenantId } : {};
    const response = await client.get('/securityalerts/rules', { params });
    return response.data;
  },

  async getRule(id: string): Promise<AlertRule> {
    const response = await client.get(`/securityalerts/rules/${id}`);
    return response.data;
  },

  async createRule(data: CreateAlertRuleRequest): Promise<AlertRule> {
    const response = await client.post('/securityalerts/rules', data);
    return response.data;
  },

  async updateRule(id: string, data: CreateAlertRuleRequest): Promise<AlertRule> {
    const response = await client.put(`/securityalerts/rules/${id}`, data);
    return response.data;
  },

  async deleteRule(id: string): Promise<void> {
    await client.delete(`/securityalerts/rules/${id}`);
  },

  // Active Alerts
  async getActiveAlerts(tenantId?: string): Promise<SecurityAlert[]> {
    const params = tenantId ? { tenantId } : {};
    const response = await client.get('/securityalerts', { params });
    return response.data;
  },

  // Alert History
  async getAlertHistory(tenantId?: string, skip = 0, take = 100): Promise<SecurityAlert[]> {
    const params: Record<string, string | number> = { skip, take };
    if (tenantId) params.tenantId = tenantId;
    const response = await client.get('/securityalerts/history', { params });
    return response.data;
  },

  // Acknowledge
  async acknowledgeAlert(id: string): Promise<SecurityAlert> {
    const response = await client.post(`/securityalerts/${id}/acknowledge`);
    return response.data;
  },

  // SIEM Integrations
  async getSiemIntegrations(tenantId?: string): Promise<SiemIntegration[]> {
    const params = tenantId ? { tenantId } : {};
    const response = await client.get('/securityalerts/siem', { params });
    return response.data;
  },

  async getSiemIntegration(id: string): Promise<SiemIntegration> {
    const response = await client.get(`/securityalerts/siem/${id}`);
    return response.data;
  },

  async createSiemIntegration(data: CreateSiemIntegrationRequest): Promise<SiemIntegration> {
    const response = await client.post('/securityalerts/siem', data);
    return response.data;
  },

  async updateSiemIntegration(id: string, data: CreateSiemIntegrationRequest): Promise<SiemIntegration> {
    const response = await client.put(`/securityalerts/siem/${id}`, data);
    return response.data;
  },

  async deleteSiemIntegration(id: string): Promise<void> {
    await client.delete(`/securityalerts/siem/${id}`);
  },
};
