import { api } from './api';

const client = api.getClient();

// ── Types ────────────────────────────────────────────────────────

export type RegionStatus = 'Active' | 'Standby' | 'Degraded' | 'Offline';

export type SyncStatus = 'Pending' | 'InProgress' | 'Completed' | 'Failed' | 'Conflict';

export type ConflictResolution = 'None' | 'LastWriterWins' | 'SourceWins' | 'TargetWins' | 'Merged' | 'Manual';

export interface RegionConfig {
  id: string;
  name: string;
  endpoint: string;
  isPrimary: boolean;
  status: number;
  lastHealthCheck?: string;
  latencyMs: number;
  description?: string;
  priority: number;
  createdAt: string;
  updatedAt: string;
  isHealthy: boolean;
}

export interface RegionHealthResult {
  regionId: string;
  regionName: string;
  isHealthy: boolean;
  latencyMs: number;
  status: number;
  checkedAt: string;
  error?: string;
}

export interface RegionSyncEvent {
  id: string;
  sourceRegion: string;
  targetRegion: string;
  entityType: string;
  entityId: string;
  syncStatus: number;
  conflictResolution: number;
  details?: string;
  errorMessage?: string;
  retryCount: number;
  createdAt: string;
  completedAt?: string;
}

export interface RegionSyncSummary {
  totalEvents: number;
  pendingEvents: number;
  completedEvents: number;
  failedEvents: number;
  conflictEvents: number;
  lastSyncAt?: string;
  averageSyncLatencyMs: number;
}

export interface RegisterRegionRequest {
  name: string;
  endpoint: string;
  isPrimary?: boolean;
  status?: number;
  description?: string;
  priority?: number;
}

export interface FailoverRequest {
  targetRegionId?: string;
}

export interface ResolveSyncConflictRequest {
  resolution: number;
}

// ── Status Helpers ───────────────────────────────────────────────

export const REGION_STATUS_LABELS: Record<number, RegionStatus> = {
  0: 'Active',
  1: 'Standby',
  2: 'Degraded',
  3: 'Offline',
};

export const REGION_STATUS_COLORS: Record<number, string> = {
  0: 'bg-green-100 text-green-800',
  1: 'bg-blue-100 text-blue-800',
  2: 'bg-yellow-100 text-yellow-800',
  3: 'bg-red-100 text-red-800',
};

export const SYNC_STATUS_LABELS: Record<number, SyncStatus> = {
  0: 'Pending',
  1: 'InProgress',
  2: 'Completed',
  3: 'Failed',
  4: 'Conflict',
};

export const SYNC_STATUS_COLORS: Record<number, string> = {
  0: 'bg-gray-100 text-gray-800',
  1: 'bg-blue-100 text-blue-800',
  2: 'bg-green-100 text-green-800',
  3: 'bg-red-100 text-red-800',
  4: 'bg-yellow-100 text-yellow-800',
};

export const CONFLICT_RESOLUTION_LABELS: Record<number, ConflictResolution> = {
  0: 'None',
  1: 'LastWriterWins',
  2: 'SourceWins',
  3: 'TargetWins',
  4: 'Merged',
  5: 'Manual',
};

// ── API ──────────────────────────────────────────────────────────

export const regionApi = {
  // Regions
  async getRegions(): Promise<RegionConfig[]> {
    const response = await client.get('/regions');
    return response.data;
  },

  async getRegion(id: string): Promise<RegionConfig> {
    const response = await client.get(`/regions/${id}`);
    return response.data;
  },

  async registerRegion(data: RegisterRegionRequest): Promise<RegionConfig> {
    const response = await client.post('/regions', data);
    return response.data;
  },

  async updateRegion(id: string, data: RegisterRegionRequest): Promise<RegionConfig> {
    const response = await client.put(`/regions/${id}`, data);
    return response.data;
  },

  async deleteRegion(id: string): Promise<void> {
    await client.delete(`/regions/${id}`);
  },

  // Health
  async checkRegionHealth(id: string): Promise<RegionHealthResult> {
    const response = await client.get(`/regions/${id}/health`);
    return response.data;
  },

  // Failover
  async triggerFailover(data?: FailoverRequest): Promise<RegionConfig> {
    const response = await client.post('/regions/failover', data || {});
    return response.data;
  },

  // Sync Status
  async getSyncStatus(): Promise<RegionSyncSummary> {
    const response = await client.get('/regions/sync-status');
    return response.data;
  },

  async getSyncEvents(skip = 0, take = 100): Promise<RegionSyncEvent[]> {
    const response = await client.get('/regions/sync-events', { params: { skip, take } });
    return response.data;
  },

  async resolveSyncConflict(id: string, data: ResolveSyncConflictRequest): Promise<RegionSyncEvent> {
    const response = await client.post(`/regions/sync-events/${id}/resolve`, data);
    return response.data;
  },
};
