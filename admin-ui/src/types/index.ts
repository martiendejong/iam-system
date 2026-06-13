// User types
export interface User {
  id: string;
  email: string;
  firstName: string;
  lastName: string;
  isActive: boolean;
  emailConfirmed: boolean;
  phoneNumber?: string;
  createdAt: string;
  updatedAt: string;
}

export interface UserRole {
  userId: string;
  roleId: string;
  tenantId?: string;
  role: Role;
  tenant?: Tenant;
}

// Role types
export interface Role {
  id: string;
  name: string;
  description?: string;
  category?: string;
  isSystem: boolean;
}

// Tenant types
export interface Tenant {
  id: string;
  name: string;
  type: TenantTypeValue;
  parentId?: string;
  settings?: Record<string, any>;
  metadata?: Record<string, any>;
}

export const TenantType = {
  Organization: 'Organization',
  Building: 'Building',
  Floor: 'Floor',
  Room: 'Room',
  Device: 'Device'
} as const;

export type TenantTypeValue = typeof TenantType[keyof typeof TenantType];

// Auth types
export interface LoginRequest {
  email: string;
  password: string;
}

export interface RegisterRequest {
  email: string;
  password: string;
  firstName: string;
  lastName: string;
  phoneNumber?: string;
}

export interface LoginResponse {
  accessToken: string;
  expiresIn: number;
  tokenType: string;
  user: User;
}

// Identity Provider types
export const IdentityProviderType = {
  Google: 'Google',
  Microsoft: 'Microsoft',
  GitHub: 'GitHub',
  Apple: 'Apple',
  SAML: 'SAML',
  OIDC: 'OIDC',
} as const;

export type IdentityProviderTypeValue = typeof IdentityProviderType[keyof typeof IdentityProviderType];

export interface IdentityProvider {
  id: string;
  name: string;
  displayName: string;
  type: IdentityProviderTypeValue;
  tenantId?: string;
  tenantName?: string;
  clientId: string;
  metadataUrl?: string;
  attributeMapping?: string;
  isActive: boolean;
  autoCreateUsers: boolean;
  defaultRoleId?: string;
  defaultRoleName?: string;
  createdAt: string;
  updatedAt: string;
}

export interface ExternalLoginAccount {
  id: string;
  provider: string;
  providerUserId: string;
  email?: string;
  displayName?: string;
  linkedAt: string;
  lastUsedAt?: string;
}

// API response types
export interface ApiError {
  message: string;
  errors?: Record<string, string[]>;
}
