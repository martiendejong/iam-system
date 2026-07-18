import { api } from './api';

const client = () => api.getClient();

export interface SecretEntry {
  id: string;
  name: string;
  tenantId?: string;
  secretType: string;
  description?: string;
  version: number;
  isActive: boolean;
  lastRotatedAt?: string;
  nextRotationAt?: string;
  rotationSchedule?: unknown;
  tags?: string[];
  createdAt: string;
  updatedAt?: string;
}

export interface SecretHistoryEntry {
  id: string;
  version: number;
  rotationReason: string;
  rotatedByUserId?: string;
  gracePeriodEndsAt?: string;
  isRevoked: boolean;
  createdAt: string;
}

export interface SecretHistory {
  secretId: string;
  secretName: string;
  currentVersion: number;
  history: SecretHistoryEntry[];
}

export interface CreateSecretInput {
  name: string;
  value: string;
  tenantId?: string;
  secretType?: string;
  description?: string;
  tags?: string[];
}

export interface RotateSecretInput {
  newValue: string;
  reason?: string;
  gracePeriodHours?: number;
}

export const secretsApi = {
  getSecrets: async (params?: { tenantId?: string; secretType?: string; isActive?: boolean }): Promise<SecretEntry[]> => {
    const query = new URLSearchParams();
    if (params) {
      Object.entries(params).forEach(([key, value]) => {
        if (value !== undefined && value !== '') query.append(key, String(value));
      });
    }
    const response = await client().get(`/secrets?${query}`);
    return response.data;
  },

  getSecret: async (id: string): Promise<SecretEntry> => {
    const response = await client().get(`/secrets/${id}`);
    return response.data;
  },

  createSecret: async (data: CreateSecretInput): Promise<SecretEntry> => {
    const response = await client().post('/secrets', data);
    return response.data;
  },

  updateSecret: async (id: string, data: Partial<CreateSecretInput> & { isActive?: boolean }): Promise<SecretEntry> => {
    const response = await client().put(`/secrets/${id}`, data);
    return response.data;
  },

  rotateSecret: async (id: string, data: RotateSecretInput): Promise<SecretEntry> => {
    const response = await client().post(`/secrets/rotate/${id}`, data);
    return response.data;
  },

  getSecretHistory: async (id: string): Promise<SecretHistory> => {
    const response = await client().get(`/secrets/${id}/history`);
    return response.data;
  },

  deleteSecret: async (id: string): Promise<void> => {
    await client().delete(`/secrets/${id}`);
  },
};
