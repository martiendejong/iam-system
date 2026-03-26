import { api } from './api';

export interface ScimToken {
  id: string;
  name: string;
  tokenPrefix: string;
  token?: string; // Only present on creation
  createdAt: string;
  expiresAt?: string;
  lastUsedAt?: string;
  isActive: boolean;
  description?: string;
}

export interface ScimProvisioningLog {
  id: string;
  operation: string;
  resourceType: string;
  externalId?: string;
  resourceId?: string;
  status: string;
  details?: string;
  createdAt: string;
}

export interface CreateScimTokenRequest {
  tenantId: string;
  name: string;
  description?: string;
  expiresAt?: string;
}

const client = api.getClient();

export const scimApi = {
  // Token management
  async createToken(data: CreateScimTokenRequest): Promise<ScimToken> {
    const response = await client.post<ScimToken>('/scim/tokens', data);
    return response.data;
  },

  async getTokens(tenantId: string): Promise<ScimToken[]> {
    const response = await client.get<ScimToken[]>('/scim/tokens', {
      params: { tenantId },
    });
    return response.data;
  },

  async revokeToken(tokenId: string): Promise<void> {
    await client.delete(`/scim/tokens/${tokenId}`);
  },

  // Provisioning logs
  async getProvisioningLogs(
    tenantId: string,
    skip = 0,
    take = 50
  ): Promise<ScimProvisioningLog[]> {
    const response = await client.get<ScimProvisioningLog[]>('/scim/logs', {
      params: { tenantId, skip, take },
    });
    return response.data;
  },
};
