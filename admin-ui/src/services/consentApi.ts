import { api } from './api';

const client = () => api.getClient();

// ─── Types ─────────────────────────────────────────────────

export interface AdminConsentRecord {
  id: string;
  userId: string;
  clientId: string;
  scopes: string;
  grantedAt: string;
  revokedAt?: string;
  ipAddress?: string;
  userAgent?: string;
}

export interface AdminDataRequest {
  id: string;
  userId: string;
  type: string;
  status: string;
  requestedAt: string;
  completedAt?: string;
  processedBy?: string;
  notes?: string;
}

// ─── API Functions ─────────────────────────────────────────

export const consentApi = {
  // Admin: get pending data requests
  getPendingRequests: async (): Promise<AdminDataRequest[]> => {
    const response = await client().get('/data-requests/pending');
    return response.data;
  },

  // Admin: process a data request
  processRequest: async (id: string): Promise<AdminDataRequest> => {
    const response = await client().post(`/data-requests/${id}/process`);
    return response.data;
  },
};
