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

// API response types
export interface ApiError {
  message: string;
  errors?: Record<string, string[]>;
}
