export interface Device {
  id: string;
  deviceId: string;
  name: string;
  deviceType: string;
  authenticationMethod: string;
  tenantId: string;
  resourcePath: string;
  permissions: string[];
  isActive: boolean;
  isOnline: boolean;
  lastSeenAt?: string;
  lastAuthenticatedAt?: string;
  lastIpAddress?: string;
  isProvisioned: boolean;
  provisionedAt?: string;
  metadata?: Record<string, any>;
  tags?: string[];
  createdAt: string;
  updatedAt: string;
  certificates?: DeviceCertificate[];
}

export interface DeviceCertificate {
  id: string;
  serialNumber: string;
  thumbprint: string;
  subjectName: string;
  issuerName: string;
  notBefore: string;
  notAfter: string;
  status: string;
  createdAt: string;
}

export interface DeviceStatistics {
  totalDevices: number;
  activeDevices: number;
  onlineDevices: number;
  provisionedDevices: number;
  devicesByType: Record<string, number>;
}

export interface RegisterDeviceRequest {
  deviceId: string;
  name: string;
  deviceType: string;
  authenticationMethod: string;
  tenantId: string;
  resourcePath: string;
  permissions: string[];
  metadata?: Record<string, any>;
}
