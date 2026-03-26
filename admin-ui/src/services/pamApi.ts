import { api } from './api';

const client = () => api.getClient();

// --- Types ---

export interface PrivilegedSession {
  id: string;
  userId: string;
  roleId: string;
  tenantId: string;
  grantedAt: string;
  expiresAt: string;
  checkedOutAt?: string;
  checkedInAt?: string;
  justification: string;
  status: PrivilegedSessionStatus;
  approvedByUserId?: string;
  isBreakGlass: boolean;
  breakGlassApprovers?: string;
  ipAddress?: string;
  auditCorrelationId?: string;
  pamPolicyId?: string;
  createdAt: string;
  updatedAt: string;
  user?: { id: string; firstName: string; lastName: string; email: string };
  role?: { id: string; name: string };
}

export type PrivilegedSessionStatus = 'Pending' | 'Active' | 'Expired' | 'CheckedIn' | 'Denied' | 'Revoked';

export interface PamPolicy {
  id: string;
  tenantId: string;
  roleId: string;
  maxDurationMinutes: number;
  requireJustification: boolean;
  requireApproval: boolean;
  approverRoleId?: string;
  breakGlassEnabled: boolean;
  breakGlassApproversRequired: number;
  isActive: boolean;
  createdAt: string;
  updatedAt: string;
  role?: { id: string; name: string };
}

export interface PamCheckoutRequest {
  roleId: string;
  tenantId: string;
  justification: string;
  durationMinutes?: number;
}

export interface PamBreakGlassRequest {
  roleId: string;
  tenantId: string;
  justification: string;
  approverUserIds: string[];
  durationMinutes?: number;
}

export interface PamPolicyRequest {
  tenantId: string;
  roleId: string;
  maxDurationMinutes: number;
  requireJustification: boolean;
  requireApproval: boolean;
  approverRoleId?: string;
  breakGlassEnabled: boolean;
  breakGlassApproversRequired: number;
  isActive: boolean;
}

// --- API Functions ---

export const pamApi = {
  // Session management
  checkout: async (data: PamCheckoutRequest): Promise<PrivilegedSession> => {
    const response = await client().post('/pam/checkout', data);
    return response.data;
  },

  checkin: async (sessionId: string): Promise<PrivilegedSession> => {
    const response = await client().post(`/pam/checkin/${sessionId}`);
    return response.data;
  },

  approveSession: async (sessionId: string): Promise<PrivilegedSession> => {
    const response = await client().post(`/pam/sessions/${sessionId}/approve`);
    return response.data;
  },

  denySession: async (sessionId: string): Promise<PrivilegedSession> => {
    const response = await client().post(`/pam/sessions/${sessionId}/deny`);
    return response.data;
  },

  // Break-glass
  breakGlass: async (data: PamBreakGlassRequest): Promise<PrivilegedSession> => {
    const response = await client().post('/pam/break-glass', data);
    return response.data;
  },

  // Session queries
  getActiveSessions: async (tenantId?: string, userId?: string): Promise<PrivilegedSession[]> => {
    const params: Record<string, string> = {};
    if (tenantId) params.tenantId = tenantId;
    if (userId) params.userId = userId;
    const response = await client().get('/pam/sessions', { params });
    return response.data;
  },

  getSessionHistory: async (params?: {
    userId?: string;
    tenantId?: string;
    startDate?: string;
    endDate?: string;
    skip?: number;
    take?: number;
  }): Promise<PrivilegedSession[]> => {
    const response = await client().get('/pam/sessions/history', { params });
    return response.data;
  },

  // Policy management
  getPolicies: async (tenantId?: string): Promise<PamPolicy[]> => {
    const params = tenantId ? { tenantId } : {};
    const response = await client().get('/pam/policies', { params });
    return response.data;
  },

  getPolicy: async (id: string): Promise<PamPolicy> => {
    const response = await client().get(`/pam/policies/${id}`);
    return response.data;
  },

  createPolicy: async (data: PamPolicyRequest): Promise<PamPolicy> => {
    const response = await client().post('/pam/policies', data);
    return response.data;
  },

  updatePolicy: async (id: string, data: PamPolicyRequest): Promise<PamPolicy> => {
    const response = await client().put(`/pam/policies/${id}`, data);
    return response.data;
  },

  deletePolicy: async (id: string): Promise<void> => {
    await client().delete(`/pam/policies/${id}`);
  },
};
