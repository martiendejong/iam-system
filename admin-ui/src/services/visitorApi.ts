import { api } from './api';

const client = () => api.getClient();

// ─── Types ─────────────────────────────────────────────────

export interface Visitor {
  id: string;
  name: string;
  email: string;
  company: string | null;
  hostUserId: string;
  hostUserName: string | null;
  tenantId: string;
  visitDate: string;
  checkInAt: string | null;
  checkOutAt: string | null;
  status: string;
  qrToken: string;
  purpose: string | null;
  createdAt: string;
  updatedAt: string;
  accessGrants: VisitorAccessGrant[] | null;
}

export interface VisitorAccessGrant {
  id: string;
  resources: string;
  validFrom: string;
  validUntil: string;
  isCurrentlyValid: boolean;
}

export interface PreRegisterVisitorDto {
  tenantId: string;
  name: string;
  email: string;
  company?: string;
  visitDate: string;
  purpose?: string;
  hostUserId?: string;
  accessGrants?: {
    resources: string[];
    validFrom: string;
    validUntil: string;
  }[];
}

export interface QrTokenResponse {
  visitorId: string;
  qrToken: string;
}

// ─── Visitor API Functions ─────────────────────────────────

export const visitorApi = {
  // Pre-register a new visitor
  preRegister: async (data: PreRegisterVisitorDto): Promise<Visitor> => {
    const response = await client().post('/visitors', data);
    return response.data;
  },

  // Get list of visitors for a tenant
  getVisitors: async (
    tenantId: string,
    skip = 0,
    take = 20,
    status?: string
  ): Promise<Visitor[]> => {
    const params: Record<string, any> = { tenantId, skip, take };
    if (status) params.status = status;
    const response = await client().get('/visitors', { params });
    return response.data;
  },

  // Get a single visitor by ID
  getVisitor: async (id: string): Promise<Visitor> => {
    const response = await client().get(`/visitors/${id}`);
    return response.data;
  },

  // Check in a visitor
  checkIn: async (id: string): Promise<Visitor> => {
    const response = await client().post(`/visitors/${id}/checkin`);
    return response.data;
  },

  // Check out a visitor
  checkOut: async (id: string): Promise<Visitor> => {
    const response = await client().post(`/visitors/${id}/checkout`);
    return response.data;
  },

  // Get QR code token for a visitor
  getQrToken: async (id: string): Promise<QrTokenResponse> => {
    const response = await client().get(`/visitors/${id}/qr`);
    return response.data;
  },
};
