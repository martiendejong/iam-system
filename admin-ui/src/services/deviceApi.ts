import axios from 'axios';
import type { AxiosInstance, AxiosError } from 'axios';
import type { Device, DeviceStatistics, RegisterDeviceRequest } from '../types/devices';

const API_BASE_URL = import.meta.env.VITE_API_URL || 'https://localhost:5001';

class DeviceApiService {
  private client: AxiosInstance;

  constructor() {
    this.client = axios.create({
      baseURL: `${API_BASE_URL}/api`,
      headers: {
        'Content-Type': 'application/json',
      },
      withCredentials: true,
    });

    // Add request interceptor to add access token
    this.client.interceptors.request.use(
      (config) => {
        const token = localStorage.getItem('accessToken');
        if (token) {
          config.headers.Authorization = `Bearer ${token}`;
        }
        return config;
      },
      (error) => Promise.reject(error)
    );

    // Add response interceptor for 401 handling
    this.client.interceptors.response.use(
      (response) => response,
      async (error: AxiosError) => {
        if (error.response?.status === 401) {
          localStorage.removeItem('accessToken');
          window.location.href = '/login';
        }
        return Promise.reject(error);
      }
    );
  }

  // Device list endpoints
  async getDevices(tenantId?: string): Promise<Device[]> {
    const url = tenantId ? `/devices/by-tenant/${tenantId}` : '/devices';
    const response = await this.client.get<Device[]>(url);
    return response.data;
  }

  async getDevice(id: string): Promise<Device> {
    const response = await this.client.get<Device>(`/devices/${id}`);
    return response.data;
  }

  async getDeviceByDeviceId(deviceId: string): Promise<Device> {
    const response = await this.client.get<Device>(`/devices/by-device-id/${deviceId}`);
    return response.data;
  }

  async getDevicesByType(type: string): Promise<Device[]> {
    const response = await this.client.get<Device[]>(`/devices/by-type/${type}`);
    return response.data;
  }

  // Device management endpoints
  async registerDevice(data: RegisterDeviceRequest): Promise<Device> {
    const response = await this.client.post<Device>('/devices', data);
    return response.data;
  }

  async updateDevice(id: string, data: Partial<Device>): Promise<Device> {
    const response = await this.client.put<Device>(`/devices/${id}`, data);
    return response.data;
  }

  async deactivateDevice(id: string): Promise<void> {
    await this.client.post(`/devices/${id}/deactivate`);
  }

  // Statistics endpoint
  async getStatistics(): Promise<DeviceStatistics> {
    const response = await this.client.get<DeviceStatistics>('/devices/statistics');
    return response.data;
  }

  // Tenant endpoint (reuse for dropdowns)
  async getTenants(): Promise<any[]> {
    const response = await this.client.get('/tenants');
    return response.data;
  }
}

export const deviceApi = new DeviceApiService();
