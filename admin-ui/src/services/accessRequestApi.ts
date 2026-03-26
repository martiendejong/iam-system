import { api } from './api';

const client = () => api.getClient();

// ─── Types ─────────────────────────────────────────────────

export interface AccessRequest {
  id: string;
  requesterId: string;
  requesterName?: string;
  resourceType: string;
  resourceId?: string;
  roleId?: string;
  roleName?: string;
  tenantId?: string;
  tenantName?: string;
  justification: string;
  status: string;
  priority: string;
  createdAt: string;
  updatedAt: string;
  expiresAt?: string;
  stepsCount: number;
  currentStep?: number;
}

export interface AccessRequestDetail extends AccessRequest {
  workflowTemplateId?: string;
  workflowTemplateName?: string;
  approvalSteps?: ApprovalStep[];
}

export interface ApprovalStep {
  id: string;
  stepOrder: number;
  approverId?: string;
  approverName?: string;
  approverRoleId?: string;
  approverRoleName?: string;
  decidedByUserId?: string;
  decidedByUserName?: string;
  status: string;
  comment?: string;
  quorumCount: number;
  approvalsReceived: number;
  decidedAt?: string;
  createdAt: string;
}

export interface WorkflowTemplate {
  id: string;
  tenantId?: string;
  tenantName?: string;
  name: string;
  description?: string;
  resourceType: string;
  steps: string;
  autoExpireHours?: number;
  autoApproveRules?: string;
  isActive: boolean;
  createdAt: string;
  updatedAt: string;
}

export interface CreateAccessRequestDto {
  resourceType: string;
  resourceId?: string;
  roleId?: string;
  tenantId?: string;
  justification: string;
  priority?: string;
}

export interface WorkflowTemplateDto {
  name: string;
  description?: string;
  resourceType: string;
  tenantId?: string;
  steps?: string;
  autoExpireHours?: number;
  autoApproveRules?: string;
  isActive?: boolean;
}

// ─── Access Request API Functions ──────────────────────────

export const accessRequestApi = {
  // Submit a new access request
  createRequest: async (data: CreateAccessRequestDto): Promise<AccessRequest> => {
    const response = await client().post('/access-requests', data);
    return response.data;
  },

  // Get my submitted requests
  getMyRequests: async (): Promise<AccessRequest[]> => {
    const response = await client().get('/access-requests');
    return response.data;
  },

  // Get pending approvals (inbox)
  getPendingApprovals: async (): Promise<AccessRequest[]> => {
    const response = await client().get('/access-requests/pending-approvals');
    return response.data;
  },

  // Get request details
  getRequest: async (id: string): Promise<AccessRequestDetail> => {
    const response = await client().get(`/access-requests/${id}`);
    return response.data;
  },

  // Approve a request
  approve: async (id: string, comment?: string): Promise<AccessRequest> => {
    const response = await client().post(`/access-requests/${id}/approve`, { comment });
    return response.data;
  },

  // Deny a request
  deny: async (id: string, comment?: string): Promise<AccessRequest> => {
    const response = await client().post(`/access-requests/${id}/deny`, { comment });
    return response.data;
  },

  // Cancel a request
  cancel: async (id: string): Promise<AccessRequest> => {
    const response = await client().post(`/access-requests/${id}/cancel`);
    return response.data;
  },
};

// ─── Workflow Template API Functions ───────────────────────

export const workflowTemplateApi = {
  getTemplates: async (tenantId?: string): Promise<WorkflowTemplate[]> => {
    const params = tenantId ? { tenantId } : {};
    const response = await client().get('/workflow-templates', { params });
    return response.data;
  },

  getTemplate: async (id: string): Promise<WorkflowTemplate> => {
    const response = await client().get(`/workflow-templates/${id}`);
    return response.data;
  },

  createTemplate: async (data: WorkflowTemplateDto): Promise<WorkflowTemplate> => {
    const response = await client().post('/workflow-templates', data);
    return response.data;
  },

  updateTemplate: async (id: string, data: WorkflowTemplateDto): Promise<WorkflowTemplate> => {
    const response = await client().put(`/workflow-templates/${id}`, data);
    return response.data;
  },

  deleteTemplate: async (id: string): Promise<void> => {
    await client().delete(`/workflow-templates/${id}`);
  },
};
