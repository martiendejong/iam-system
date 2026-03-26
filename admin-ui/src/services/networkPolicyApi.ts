import { api } from './api';

export interface IpAllowlistEntry {
  id: string;
  tenantId: string;
  cidr: string;
  description?: string;
  isActive: boolean;
  createdAt: string;
  updatedAt: string;
}

export interface GeoRestriction {
  id: string;
  tenantId: string;
  allowedCountries?: string;
  blockedCountries?: string;
  isActive: boolean;
  createdAt: string;
  updatedAt: string;
}

export interface GeoFence {
  id: string;
  tenantId: string;
  name: string;
  latitude: number;
  longitude: number;
  radiusMeters: number;
  description?: string;
  isActive: boolean;
  createdAt: string;
}

export interface BlockedIpLog {
  id: string;
  tenantId?: string;
  ipAddress: string;
  reason: string;
  country?: string;
  city?: string;
  blockedAt: string;
}

export interface IpCheckResult {
  isAllowed: boolean;
  ipAddress: string;
  country?: string;
  city?: string;
  reasons: string[];
  passedIpAllowlist: boolean;
  passedGeoRestriction: boolean;
  passedGeoFence: boolean;
}

const client = api.getClient();

export const networkPolicyApi = {
  // IP Allowlist
  async getIpAllowlist(tenantId: string): Promise<IpAllowlistEntry[]> {
    const response = await client.get<IpAllowlistEntry[]>('/networkpolicy/ip-allowlist', {
      params: { tenantId },
    });
    return response.data;
  },

  async createIpAllowlistEntry(data: { tenantId: string; cidr: string; description?: string }): Promise<IpAllowlistEntry> {
    const response = await client.post<IpAllowlistEntry>('/networkpolicy/ip-allowlist', data);
    return response.data;
  },

  async updateIpAllowlistEntry(id: string, data: { cidr: string; description?: string; isActive: boolean }): Promise<IpAllowlistEntry> {
    const response = await client.put<IpAllowlistEntry>(`/networkpolicy/ip-allowlist/${id}`, data);
    return response.data;
  },

  async deleteIpAllowlistEntry(id: string): Promise<void> {
    await client.delete(`/networkpolicy/ip-allowlist/${id}`);
  },

  // Geo Restriction
  async getGeoRestriction(tenantId: string): Promise<GeoRestriction | null> {
    const response = await client.get<GeoRestriction | null>('/networkpolicy/geo-restriction', {
      params: { tenantId },
    });
    return response.data;
  },

  async upsertGeoRestriction(data: { tenantId: string; allowedCountries?: string; blockedCountries?: string; isActive: boolean }): Promise<GeoRestriction> {
    const response = await client.put<GeoRestriction>('/networkpolicy/geo-restriction', data);
    return response.data;
  },

  async deleteGeoRestriction(tenantId: string): Promise<void> {
    await client.delete('/networkpolicy/geo-restriction', { params: { tenantId } });
  },

  // GeoFences
  async getGeoFences(tenantId: string): Promise<GeoFence[]> {
    const response = await client.get<GeoFence[]>('/networkpolicy/geofences', {
      params: { tenantId },
    });
    return response.data;
  },

  async createGeoFence(data: { tenantId: string; name: string; latitude: number; longitude: number; radiusMeters: number; description?: string }): Promise<GeoFence> {
    const response = await client.post<GeoFence>('/networkpolicy/geofences', data);
    return response.data;
  },

  async updateGeoFence(id: string, data: { name: string; latitude: number; longitude: number; radiusMeters: number; description?: string; isActive: boolean }): Promise<GeoFence> {
    const response = await client.put<GeoFence>(`/networkpolicy/geofences/${id}`, data);
    return response.data;
  },

  async deleteGeoFence(id: string): Promise<void> {
    await client.delete(`/networkpolicy/geofences/${id}`);
  },

  // Blocked IP Log
  async getBlockedLogs(tenantId: string, skip = 0, take = 100): Promise<BlockedIpLog[]> {
    const response = await client.get<BlockedIpLog[]>('/networkpolicy/blocked-log', {
      params: { tenantId, skip, take },
    });
    return response.data;
  },

  // IP Check
  async checkIp(data: { tenantId: string; ipAddress: string; latitude?: number; longitude?: number }): Promise<IpCheckResult> {
    const response = await client.post<IpCheckResult>('/networkpolicy/check-ip', data);
    return response.data;
  },
};
