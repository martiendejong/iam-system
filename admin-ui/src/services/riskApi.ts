import { api } from './api';

const client = () => api.getClient();

export interface RiskHeatmapCell {
  hour: number;
  dayOfWeek: number;
  count: number;
  avgRiskScore: number;
}

export interface RiskDistributionBucket {
  range: string;
  count: number;
  minScore: number;
  maxScore: number;
}

export interface RiskSummary {
  totalAssessments: number;
  allowedCount: number;
  stepUpCount: number;
  blockedCount: number;
  avgRiskScore: number;
  highRiskCount: number;
  trustedDeviceCount: number;
}

export interface RiskDashboardData {
  heatmap: RiskHeatmapCell[];
  distribution: RiskDistributionBucket[];
  summary: RiskSummary;
}

export interface LoginRiskScore {
  id: string;
  userId: string;
  ipAddress: string;
  userAgent?: string;
  geoLocation?: string;
  riskScore: number;
  riskFactors: string;
  action: number; // 0=Allow, 1=StepUp, 2=Block
  sessionId?: string;
  createdAt: string;
}

export interface RiskThreshold {
  id: string;
  tenantId?: string;
  lowThreshold: number;
  mediumThreshold: number;
  highThreshold: number;
  blockThreshold: number;
  requireMfaAbove: number;
  isActive: boolean;
  createdAt: string;
  updatedAt: string;
}

export interface TrustedDevice {
  id: string;
  userId: string;
  deviceFingerprint: string;
  name: string;
  trustScore: number;
  lastUsedAt: string;
  createdAt: string;
  expiresAt: string;
}

export const riskApi = {
  getDashboard: async (days = 30, tenantId?: string): Promise<RiskDashboardData> => {
    const params = new URLSearchParams({ days: String(days) });
    if (tenantId) params.append('tenantId', tenantId);
    const response = await client().get(`/riskassessment/dashboard?${params}`);
    return response.data;
  },

  getScores: async (params?: { userId?: string; startDate?: string; endDate?: string; skip?: number; take?: number }): Promise<LoginRiskScore[]> => {
    const query = new URLSearchParams();
    if (params) {
      Object.entries(params).forEach(([key, value]) => {
        if (value !== undefined && value !== '') query.append(key, String(value));
      });
    }
    const response = await client().get(`/riskassessment/scores?${query}`);
    return response.data;
  },

  // Thresholds
  getThresholds: async (tenantId?: string): Promise<RiskThreshold[]> => {
    const params = tenantId ? `?tenantId=${tenantId}` : '';
    const response = await client().get(`/riskassessment/thresholds${params}`);
    return response.data;
  },

  upsertThreshold: async (data: Partial<RiskThreshold>): Promise<RiskThreshold> => {
    const response = await client().post('/riskassessment/thresholds', data);
    return response.data;
  },

  deleteThreshold: async (id: string): Promise<void> => {
    await client().delete(`/riskassessment/thresholds/${id}`);
  },

  // Trusted Devices
  getTrustedDevices: async (userId: string): Promise<TrustedDevice[]> => {
    const response = await client().get(`/riskassessment/devices/${userId}`);
    return response.data;
  },

  trustDevice: async (data: { userId: string; deviceFingerprint: string; name: string }): Promise<TrustedDevice> => {
    const response = await client().post('/riskassessment/devices', data);
    return response.data;
  },

  removeTrustedDevice: async (id: string, userId: string): Promise<void> => {
    await client().delete(`/riskassessment/devices/${id}?userId=${userId}`);
  },
};
