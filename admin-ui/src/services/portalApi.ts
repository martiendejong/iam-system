import { api } from './api';

const client = () => api.getClient();

// ─── Types ─────────────────────────────────────────────────

export interface UserProfile {
  id: string;
  email: string;
  firstName: string;
  lastName: string;
  phoneNumber?: string;
  avatarUrl?: string;
  createdAt: string;
}

export interface UpdateProfileRequest {
  firstName?: string;
  lastName?: string;
  phoneNumber?: string;
  avatarUrl?: string;
}

export interface ChangePasswordRequest {
  currentPassword: string;
  newPassword: string;
}

export interface SecuritySummary {
  mfaEnabled: boolean;
  mfaMethod: string | null;
  passkeyCount: number;
  sessionCount: number;
  lastLoginAt: string | null;
  recoveryCodesRemaining: number;
}

export interface ActivityEntry {
  id: string;
  action: string;
  resource: string;
  details?: string;
  ipAddress?: string;
  userAgent?: string;
  createdAt: string;
}

export interface ActivityResponse {
  items: ActivityEntry[];
  total: number;
  page: number;
  pageSize: number;
}

export interface Session {
  id: string;
  ipAddress?: string;
  userAgent?: string;
  deviceInfo?: string;
  location?: string;
  createdAt: string;
  lastActivityAt?: string;
  expiresAt: string;
  isCurrent: boolean;
}

export interface PasskeyCredential {
  id: string;
  name?: string;
  credType: string;
  createdAt: string;
  lastUsedAt?: string;
  deviceType?: string;
}

export interface ConsentRecord {
  id: string;
  clientId: string;
  scopes: string;
  grantedAt: string;
  ipAddress?: string;
  userAgent?: string;
}

export interface DataRequestEntry {
  id: string;
  type: string;
  status: string;
  requestedAt: string;
  completedAt?: string;
  notes?: string;
  hasDownload?: boolean;
}

export interface LinkedIdentity {
  id: string;
  provider: string;
  providerUserId: string;
  email?: string;
  displayName?: string;
  linkedAt: string;
  lastUsedAt?: string;
  isPrimary: boolean;
}

export interface DuplicateAccountInfo {
  userId: string;
  email: string;
  firstName: string;
  lastName: string;
  hasPassword: boolean;
  linkedProviders: string[];
  createdAt: string;
  lastLoginAt?: string;
}

export interface DuplicateEmailGroup {
  email: string;
  accounts: DuplicateAccountInfo[];
}

// ─── API Functions ─────────────────────────────────────────

export const portalApi = {
  // Profile
  getProfile: async (): Promise<UserProfile> => {
    const response = await client().get('/portal/profile');
    return response.data;
  },

  updateProfile: async (data: UpdateProfileRequest): Promise<UserProfile> => {
    const response = await client().put('/portal/profile', data);
    return response.data;
  },

  // Password
  changePassword: async (data: ChangePasswordRequest): Promise<{ message: string }> => {
    const response = await client().post('/portal/change-password', data);
    return response.data;
  },

  // Security
  getSecuritySummary: async (): Promise<SecuritySummary> => {
    const response = await client().get('/portal/security-summary');
    return response.data;
  },

  // Activity
  getActivity: async (params?: { page?: number; pageSize?: number; eventType?: string }): Promise<ActivityResponse> => {
    const query = new URLSearchParams();
    if (params?.page) query.append('page', String(params.page));
    if (params?.pageSize) query.append('pageSize', String(params.pageSize));
    if (params?.eventType) query.append('eventType', params.eventType);
    const response = await client().get(`/portal/activity?${query}`);
    return response.data;
  },

  // Sessions (uses existing sessions controller)
  getSessions: async (): Promise<Session[]> => {
    const response = await client().get('/sessions');
    return response.data;
  },

  revokeSession: async (id: string): Promise<{ message: string }> => {
    const response = await client().delete(`/sessions/${id}`);
    return response.data;
  },

  revokeOtherSessions: async (): Promise<{ message: string; revokedCount: number }> => {
    const response = await client().delete('/sessions/revoke-others');
    return response.data;
  },

  // Passkeys (uses existing passkey controller)
  getPasskeys: async (): Promise<PasskeyCredential[]> => {
    const response = await client().get('/passkey/credentials');
    return response.data;
  },

  deletePasskey: async (credentialId: string): Promise<{ message: string }> => {
    const response = await client().delete(`/passkey/credentials/${credentialId}`);
    return response.data;
  },

  // MFA (uses existing MFA controller)
  getMfaStatus: async (): Promise<{
    twoFactorEnabled: boolean;
    method: string | null;
    recoveryCodesRemaining: number;
    hasPendingSetup: boolean;
  }> => {
    const response = await client().get('/mfa/status');
    return response.data;
  },

  regenerateRecoveryCodes: async (): Promise<{ recoveryCodes: string[]; warning: string }> => {
    const response = await client().post('/mfa/recovery-codes');
    return response.data;
  },

  // Consent management (uses consent controller)
  getMyConsents: async (): Promise<ConsentRecord[]> => {
    const response = await client().get('/consent/my-consents');
    return response.data;
  },

  revokeConsent: async (clientId: string): Promise<{ message: string }> => {
    const response = await client().post('/consent/revoke', { clientId });
    return response.data;
  },

  // Data requests (uses data-requests controller)
  getMyDataRequests: async (): Promise<DataRequestEntry[]> => {
    const response = await client().get('/data-requests/my-requests');
    return response.data;
  },

  requestDataExport: async (): Promise<DataRequestEntry> => {
    const response = await client().post('/data-requests/export');
    return response.data;
  },

  requestAccountDeletion: async (reason?: string): Promise<DataRequestEntry> => {
    const response = await client().post('/data-requests/deletion', { reason });
    return response.data;
  },

  downloadExport: async (requestId: string): Promise<Blob> => {
    const response = await client().get(`/data-requests/${requestId}/download`, {
      responseType: 'blob',
    });
    return response.data;
  },

  // Linked Accounts / Account Linking
  getLinkedIdentities: async (): Promise<LinkedIdentity[]> => {
    const response = await client().get('/portal/identities');
    return response.data;
  },

  linkProvider: async (provider: string, data: { providerUserId: string; email?: string; displayName?: string }): Promise<LinkedIdentity> => {
    const response = await client().post(`/portal/link/${provider}`, data);
    return response.data;
  },

  unlinkProvider: async (provider: string): Promise<{ message: string }> => {
    const response = await client().delete(`/portal/link/${provider}`);
    return response.data;
  },

  setPrimaryIdentity: async (identityId: string): Promise<{ message: string }> => {
    const response = await client().post(`/portal/identities/${identityId}/primary`);
    return response.data;
  },

  getMergeSuggestions: async (): Promise<DuplicateEmailGroup[]> => {
    const response = await client().get('/portal/merge/suggestions');
    return response.data;
  },

  mergeAccounts: async (secondaryUserId: string): Promise<{ message: string }> => {
    const response = await client().post('/portal/merge', { secondaryUserId });
    return response.data;
  },
};
