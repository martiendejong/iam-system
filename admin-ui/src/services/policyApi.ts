import { api } from './api';
import type { Policy, PolicyEvaluationRequest, PolicyEvaluationResult } from '../types/policies';

// Extend the ApiService class by adding policy methods via a separate object
// that uses the same underlying axios instance pattern
class PolicyApiService {
  async getPolicies(tenantId?: string): Promise<Policy[]> {
    const params = tenantId ? `?tenantId=${tenantId}` : '';
    const response = await (api as any).client.get(`/policies${params}`);
    return response.data as Policy[];
  }

  async getPolicy(id: string): Promise<Policy> {
    const response = await (api as any).client.get(`/policies/${id}`);
    return response.data as Policy;
  }

  async createPolicy(data: Partial<Policy>): Promise<Policy> {
    const response = await (api as any).client.post('/policies', data);
    return response.data as Policy;
  }

  async updatePolicy(id: string, data: Partial<Policy>): Promise<Policy> {
    const response = await (api as any).client.put(`/policies/${id}`, data);
    return response.data as Policy;
  }

  async deletePolicy(id: string): Promise<void> {
    await (api as any).client.delete(`/policies/${id}`);
  }

  async evaluatePolicy(data: PolicyEvaluationRequest): Promise<PolicyEvaluationResult> {
    const response = await (api as any).client.post('/policies/evaluate', data);
    return response.data as PolicyEvaluationResult;
  }

  async getEffectivePolicies(tenantId: string): Promise<Policy[]> {
    const response = await (api as any).client.get(`/policies/effective/${tenantId}`);
    return response.data as Policy[];
  }

  async testPolicy(id: string): Promise<any> {
    const response = await (api as any).client.post(`/policies/${id}/test`);
    return response.data;
  }
}

export const policyApi = new PolicyApiService();
