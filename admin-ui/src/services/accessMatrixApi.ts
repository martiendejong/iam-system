import { api } from './api';

const client = () => api.getClient();

export interface MatrixPermission {
  role: string;
  label: string;
  description?: string | null;
}

export interface MatrixApplication {
  clientId: string;
  displayName: string;
  baseRole: string;
  permissions: MatrixPermission[];
  source: 'manifest' | 'registered';
}

export interface MatrixUser {
  id: string;
  email: string;
  firstName: string;
  lastName: string;
  isActive: boolean;
  /** Matrix-managed role names the user currently holds */
  roles: string[];
}

export interface MatrixResponse {
  total: number;
  skip: number;
  take: number;
  applications: MatrixApplication[];
  users: MatrixUser[];
}

export interface MatrixChangeResponse {
  message: string;
  changed: boolean;
  userId: string;
  role: string;
  note: string;
}

export const accessMatrixApi = {
  getMatrix: async (search?: string, skip = 0, take = 50): Promise<MatrixResponse> => {
    const query = new URLSearchParams();
    if (search) query.append('search', search);
    query.append('skip', String(skip));
    query.append('take', String(take));
    const response = await client().get<MatrixResponse>(`/access-matrix?${query}`);
    return response.data;
  },

  getApplications: async (): Promise<MatrixApplication[]> => {
    const response = await client().get<MatrixApplication[]>('/access-matrix/applications');
    return response.data;
  },

  grant: async (userId: string, role: string): Promise<MatrixChangeResponse> => {
    const response = await client().post<MatrixChangeResponse>('/access-matrix/grant', { userId, role });
    return response.data;
  },

  revoke: async (userId: string, role: string): Promise<MatrixChangeResponse> => {
    const response = await client().post<MatrixChangeResponse>('/access-matrix/revoke', { userId, role });
    return response.data;
  },
};
