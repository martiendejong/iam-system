import { api } from './api';

const client = () => api.getClient();

// --- Types ---

export interface Delegation {
  id: string;
  delegatorUserId: string;
  delegatorUserName?: string;
  delegateUserId: string;
  delegateUserName?: string;
  tenantId: string;
  tenantName?: string;
  permissions: string;
  validFrom: string;
  validUntil: string;
  reason: string;
  isActive: boolean;
  requiresApproval: boolean;
  status: DelegationStatus;
  approvedByUserId?: string;
  approvedAt?: string;
  revokedAt?: string;
  revokedByUserId?: string;
  createdAt: string;
  updatedAt: string;
}

export type DelegationStatus = 'PendingApproval' | 'Active' | 'Expired' | 'Revoked' | 'Denied';

export interface CreateDelegationRequest {
  delegatorUserId?: string;
  delegateUserId: string;
  tenantId: string;
  permissions: string[];
  validFrom: string;
  validUntil: string;
  reason: string;
  requiresApproval?: boolean;
}

export interface SodConstraint {
  id: string;
  name: string;
  tenantId: string;
  conflictingRoleA: string;
  roleAName?: string;
  conflictingRoleB: string;
  roleBName?: string;
  description: string;
  severity: SodSeverity;
  isActive: boolean;
  createdAt: string;
  updatedAt: string;
}

export type SodSeverity = 'Warning' | 'Block';

export interface CreateSodConstraintRequest {
  name: string;
  tenantId: string;
  conflictingRoleA: string;
  conflictingRoleB: string;
  description: string;
  severity?: string;
}

export interface UpdateSodConstraintRequest {
  name: string;
  description: string;
  severity?: string;
  isActive?: boolean;
}

export interface SodViolation {
  id: string;
  constraintId: string;
  constraintName?: string;
  userId: string;
  userName?: string;
  roleA: string;
  roleB: string;
  detectedAt: string;
  resolution?: string;
  resolvedAt?: string;
  resolvedByUserId?: string;
}

export interface SodCheckResult {
  userId: string;
  violationsFound: number;
  violations: SodViolation[];
}

export interface EffectivePermissions {
  userId: string;
  tenantId: string;
  permissions: string[];
}

// --- API Functions ---

export const delegationApi = {
  // Delegations
  createDelegation: async (data: CreateDelegationRequest): Promise<Delegation> => {
    const response = await client().post('/delegations', data);
    return response.data;
  },

  getDelegations: async (tenantId: string): Promise<Delegation[]> => {
    const response = await client().get('/delegations', { params: { tenantId } });
    return response.data;
  },

  getDelegation: async (id: string): Promise<Delegation> => {
    const response = await client().get(`/delegations/${id}`);
    return response.data;
  },

  getActiveDelegations: async (): Promise<Delegation[]> => {
    const response = await client().get('/delegations/active');
    return response.data;
  },

  approveDelegation: async (id: string): Promise<Delegation> => {
    const response = await client().post(`/delegations/${id}/approve`);
    return response.data;
  },

  revokeDelegation: async (id: string): Promise<Delegation> => {
    const response = await client().post(`/delegations/${id}/revoke`);
    return response.data;
  },

  getEffectivePermissions: async (userId: string, tenantId: string): Promise<EffectivePermissions> => {
    const response = await client().get('/delegations/effective-permissions', {
      params: { userId, tenantId },
    });
    return response.data;
  },

  // SoD Constraints
  createConstraint: async (data: CreateSodConstraintRequest): Promise<SodConstraint> => {
    const response = await client().post('/sod/constraints', data);
    return response.data;
  },

  getConstraints: async (tenantId: string): Promise<SodConstraint[]> => {
    const response = await client().get('/sod/constraints', { params: { tenantId } });
    return response.data;
  },

  getConstraint: async (id: string): Promise<SodConstraint> => {
    const response = await client().get(`/sod/constraints/${id}`);
    return response.data;
  },

  updateConstraint: async (id: string, data: UpdateSodConstraintRequest): Promise<SodConstraint> => {
    const response = await client().put(`/sod/constraints/${id}`, data);
    return response.data;
  },

  deleteConstraint: async (id: string): Promise<void> => {
    await client().delete(`/sod/constraints/${id}`);
  },

  // SoD Violations
  checkViolations: async (userId: string): Promise<SodCheckResult> => {
    const response = await client().post(`/sod/check/${userId}`);
    return response.data;
  },

  getViolations: async (tenantId?: string): Promise<SodViolation[]> => {
    const params = tenantId ? { tenantId } : {};
    const response = await client().get('/sod/violations', { params });
    return response.data;
  },

  resolveViolation: async (id: string, resolution: string): Promise<SodViolation> => {
    const response = await client().post(`/sod/violations/${id}/resolve`, { resolution });
    return response.data;
  },
};
