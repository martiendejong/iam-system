export interface Policy {
  id: string;
  name: string;
  description?: string;
  tenantId: string;
  effect: 'Allow' | 'Deny';
  resource: string;
  action: string;
  conditions?: string;
  priority: number;
  inheritanceScope: 'Self' | 'Children' | 'Descendants';
  isActive: boolean;
  createdAt: string;
  updatedAt: string;
  createdByUserId?: string;
  timeConstraints?: string;
  roleId?: string;
  userId?: string;
  inheritedFromPolicyId?: string;
  expiresAt?: string;
}

export interface PolicyEvaluationRequest {
  userId: string;
  tenantId: string;
  resource: string;
  action: string;
}

export interface PolicyEvaluationResult {
  isAllowed: boolean;
  matchedPolicy?: Policy;
  policyId?: string;
  evaluatedPoliciesCount: number;
  reason: string;
  evaluatedPolicies: Policy[];
  evaluationTimeMs: number;
}

export interface PolicyTestScenarioResult {
  testName: string;
  passed: boolean;
  expected: boolean;
  actual: boolean;
  matchedPolicy?: string;
}

export interface TenantNode {
  id: string;
  name: string;
  type: string;
  parentId?: string;
  children: TenantNode[];
  policies: Policy[];
  inheritedPolicies: Policy[];
}
