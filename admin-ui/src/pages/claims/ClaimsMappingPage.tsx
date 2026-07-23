import { useState, useEffect } from 'react';
import DashboardLayout from '../../components/layout/DashboardLayout';
import { api } from '../../services/api';

interface OAuthClient {
  id: string;
  clientId: string;
  displayName: string;
}

interface Tenant {
  id: string;
  name: string;
  slug: string;
}

interface UserSummary {
  id: string;
  email: string;
  firstName: string;
  lastName: string;
}

interface ClaimsMappingRule {
  id: string;
  clientId: string;
  tenantId: string | null;
  sourceType: string;
  sourcePath: string;
  targetClaim: string;
  transform: string;
  transformPattern: string | null;
  priority: number;
  isActive: boolean;
  createdAt: string;
}

interface TokenConfig {
  id?: string;
  clientId: string;
  tenantId: string | null;
  accessTokenLifetimeMinutes: number;
  refreshTokenLifetimeDays: number;
  includeRoles: boolean;
  includePermissions: boolean;
  includeGroups: boolean;
  customNamespace: string | null;
  isDefault: boolean;
}

interface TokenPreviewClaim {
  type: string;
  value: string;
  source: string;
}

interface TokenPreview {
  userId: string;
  clientId: string;
  tenantId: string | null;
  claims: TokenPreviewClaim[];
  configuration: {
    accessTokenLifetimeMinutes: number;
    refreshTokenLifetimeDays: number;
    includeRoles: boolean;
    includePermissions: boolean;
    includeGroups: boolean;
    customNamespace: string | null;
  };
}

const SOURCE_TYPES = ['UserAttribute', 'GroupMembership', 'RolePermission', 'Static', 'External'];
const TRANSFORMS = ['None', 'ToUpper', 'ToLower', 'Join', 'Split', 'Format'];

const claimsApi = {
  client: api.getClient(),

  async getRules(clientId: string, tenantId?: string): Promise<ClaimsMappingRule[]> {
    const params: any = { clientId };
    if (tenantId) params.tenantId = tenantId;
    const res = await this.client.get('/claims-mapping/rules', { params });
    return res.data;
  },

  async createRule(data: any): Promise<ClaimsMappingRule> {
    const res = await this.client.post('/claims-mapping/rules', data);
    return res.data;
  },

  async updateRule(id: string, data: any): Promise<ClaimsMappingRule> {
    const res = await this.client.put(`/claims-mapping/rules/${id}`, data);
    return res.data;
  },

  async deleteRule(id: string): Promise<void> {
    await this.client.delete(`/claims-mapping/rules/${id}`);
  },

  async getTokenConfig(clientId: string, tenantId?: string): Promise<TokenConfig> {
    const params: any = { clientId };
    if (tenantId) params.tenantId = tenantId;
    const res = await this.client.get('/claims-mapping/token-config', { params });
    return res.data;
  },

  async upsertTokenConfig(data: any): Promise<TokenConfig> {
    const res = await this.client.put('/claims-mapping/token-config', data);
    return res.data;
  },

  async previewToken(clientId: string, userId: string, tenantId?: string): Promise<TokenPreview> {
    const params: any = { clientId, userId };
    if (tenantId) params.tenantId = tenantId;
    const res = await this.client.get('/claims-mapping/preview', { params });
    return res.data;
  },
};

export default function ClaimsMappingPage() {
  const [clients, setClients] = useState<OAuthClient[]>([]);
  const [tenants, setTenants] = useState<Tenant[]>([]);
  const [users, setUsers] = useState<UserSummary[]>([]);
  const [selectedClientId, setSelectedClientId] = useState('');
  const [selectedTenantId, setSelectedTenantId] = useState('');
  const [activeTab, setActiveTab] = useState<'rules' | 'config' | 'preview'>('rules');
  const [error, setError] = useState('');
  const [success, setSuccess] = useState('');
  const [loading, setLoading] = useState(false);

  // Rules state
  const [rules, setRules] = useState<ClaimsMappingRule[]>([]);
  const [showRuleForm, setShowRuleForm] = useState(false);
  const [editingRule, setEditingRule] = useState<ClaimsMappingRule | null>(null);
  const [ruleForm, setRuleForm] = useState({
    sourceType: 'UserAttribute',
    sourcePath: '',
    targetClaim: '',
    transform: 'None',
    transformPattern: '',
    priority: 100,
    isActive: true,
  });

  // Token config state
  const [tokenConfig, setTokenConfig] = useState<TokenConfig>({
    clientId: '',
    tenantId: null,
    accessTokenLifetimeMinutes: 15,
    refreshTokenLifetimeDays: 7,
    includeRoles: true,
    includePermissions: false,
    includeGroups: false,
    customNamespace: null,
    isDefault: true,
  });

  // Preview state
  const [selectedUserId, setSelectedUserId] = useState('');
  const [preview, setPreview] = useState<TokenPreview | null>(null);
  const [previewLoading, setPreviewLoading] = useState(false);

  useEffect(() => {
    loadClients();
    loadTenants();
    loadUsers();
  }, []);

  useEffect(() => {
    if (selectedClientId) {
      loadRules();
      loadTokenConfig();
      setPreview(null);
    }
  }, [selectedClientId, selectedTenantId]);

  const loadClients = async () => {
    try {
      const data = await api.getOAuth2Clients();
      setClients(data);
      if (data.length > 0 && !selectedClientId) {
        setSelectedClientId(data[0].clientId || data[0].id);
      }
    } catch (err: any) {
      setError(err.response?.data?.error || 'Failed to load OAuth2 clients');
    }
  };

  const loadTenants = async () => {
    try {
      const data = await api.getTenants();
      setTenants(data);
    } catch {
      // Non-critical
    }
  };

  const loadUsers = async () => {
    try {
      const data = await api.getUsers();
      setUsers(data);
    } catch {
      // Non-critical
    }
  };

  const loadRules = async () => {
    try {
      setLoading(true);
      const data = await claimsApi.getRules(selectedClientId, selectedTenantId || undefined);
      setRules(data);
    } catch (err: any) {
      setError(err.response?.data?.error || 'Failed to load claims mapping rules');
    } finally {
      setLoading(false);
    }
  };

  const loadTokenConfig = async () => {
    try {
      const data = await claimsApi.getTokenConfig(selectedClientId, selectedTenantId || undefined);
      setTokenConfig(data);
    } catch (err: any) {
      setError(err.response?.data?.error || 'Failed to load token configuration');
    }
  };

  const handleCreateOrUpdateRule = async () => {
    if (!ruleForm.sourcePath.trim() || !ruleForm.targetClaim.trim()) {
      setError('Source path and target claim are required');
      return;
    }

    try {
      setError('');
      if (editingRule) {
        await claimsApi.updateRule(editingRule.id, {
          sourceType: SOURCE_TYPES.indexOf(ruleForm.sourceType),
          sourcePath: ruleForm.sourcePath,
          targetClaim: ruleForm.targetClaim,
          transform: TRANSFORMS.indexOf(ruleForm.transform),
          transformPattern: ruleForm.transformPattern || null,
          priority: ruleForm.priority,
          isActive: ruleForm.isActive,
        });
        setSuccess('Rule updated successfully');
      } else {
        await claimsApi.createRule({
          clientId: selectedClientId,
          tenantId: selectedTenantId || null,
          sourceType: SOURCE_TYPES.indexOf(ruleForm.sourceType),
          sourcePath: ruleForm.sourcePath,
          targetClaim: ruleForm.targetClaim,
          transform: TRANSFORMS.indexOf(ruleForm.transform),
          transformPattern: ruleForm.transformPattern || null,
          priority: ruleForm.priority,
          isActive: ruleForm.isActive,
        });
        setSuccess('Rule created successfully');
      }

      resetRuleForm();
      await loadRules();
      setTimeout(() => setSuccess(''), 3000);
    } catch (err: any) {
      setError(err.response?.data?.error || 'Failed to save rule');
    }
  };

  const handleDeleteRule = async (ruleId: string) => {
    if (!confirm('Are you sure you want to delete this rule?')) return;

    try {
      await claimsApi.deleteRule(ruleId);
      setSuccess('Rule deleted successfully');
      await loadRules();
      setTimeout(() => setSuccess(''), 3000);
    } catch (err: any) {
      setError(err.response?.data?.error || 'Failed to delete rule');
    }
  };

  const handleEditRule = (rule: ClaimsMappingRule) => {
    setEditingRule(rule);
    setRuleForm({
      sourceType: rule.sourceType,
      sourcePath: rule.sourcePath,
      targetClaim: rule.targetClaim,
      transform: rule.transform,
      transformPattern: rule.transformPattern || '',
      priority: rule.priority,
      isActive: rule.isActive,
    });
    setShowRuleForm(true);
  };

  const resetRuleForm = () => {
    setShowRuleForm(false);
    setEditingRule(null);
    setRuleForm({
      sourceType: 'UserAttribute',
      sourcePath: '',
      targetClaim: '',
      transform: 'None',
      transformPattern: '',
      priority: 100,
      isActive: true,
    });
  };

  const handleSaveTokenConfig = async () => {
    try {
      setError('');
      await claimsApi.upsertTokenConfig({
        clientId: selectedClientId,
        tenantId: selectedTenantId || null,
        accessTokenLifetimeMinutes: tokenConfig.accessTokenLifetimeMinutes,
        refreshTokenLifetimeDays: tokenConfig.refreshTokenLifetimeDays,
        includeRoles: tokenConfig.includeRoles,
        includePermissions: tokenConfig.includePermissions,
        includeGroups: tokenConfig.includeGroups,
        customNamespace: tokenConfig.customNamespace || null,
      });
      setSuccess('Token configuration saved successfully');
      await loadTokenConfig();
      setTimeout(() => setSuccess(''), 3000);
    } catch (err: any) {
      setError(err.response?.data?.error || 'Failed to save token configuration');
    }
  };

  const handlePreviewToken = async () => {
    if (!selectedUserId) {
      setError('Please select a user for token preview');
      return;
    }

    try {
      setError('');
      setPreviewLoading(true);
      const data = await claimsApi.previewToken(selectedClientId, selectedUserId, selectedTenantId || undefined);
      setPreview(data);
    } catch (err: any) {
      setError(err.response?.data?.error || 'Failed to generate token preview');
    } finally {
      setPreviewLoading(false);
    }
  };


  const getSourceTypeBadgeColor = (sourceType: string) => {
    switch (sourceType) {
      case 'UserAttribute': return 'bg-blue-100 text-blue-800';
      case 'GroupMembership': return 'bg-green-100 text-green-800';
      case 'RolePermission': return 'bg-purple-100 text-purple-800';
      case 'Static': return 'bg-gray-100 text-gray-800';
      case 'External': return 'bg-yellow-100 text-yellow-800';
      default: return 'bg-gray-100 text-gray-800';
    }
  };

  return (
    <DashboardLayout>
      <div className="px-4 sm:px-6 lg:px-8">
        {/* Header */}
        <div className="sm:flex sm:items-center">
          <div className="sm:flex-auto">
            <h1 className="text-2xl font-semibold text-gray-900">Claims Mapping & Token Configuration</h1>
            <p className="mt-2 text-sm text-gray-700">
              Configure custom token claims, transformations, and token lifetime settings per OAuth2 client.
            </p>
          </div>
        </div>

        {/* Client & Tenant Selectors */}
        <div className="mt-6 grid grid-cols-1 sm:grid-cols-2 gap-4 max-w-lg">
          <div>
            <label htmlFor="client" className="block text-sm font-medium text-gray-700">OAuth2 Client</label>
            <select
              id="client"
              value={selectedClientId}
              onChange={(e) => setSelectedClientId(e.target.value)}
              className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
            >
              <option value="">Select a client...</option>
              {clients.map((client) => (
                <option key={client.id} value={client.clientId || client.id}>
                  {client.displayName || client.clientId || client.id}
                </option>
              ))}
            </select>
          </div>
          <div>
            <label htmlFor="tenant" className="block text-sm font-medium text-gray-700">Tenant (optional)</label>
            <select
              id="tenant"
              value={selectedTenantId}
              onChange={(e) => setSelectedTenantId(e.target.value)}
              className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
            >
              <option value="">All tenants (global)</option>
              {tenants.map((tenant) => (
                <option key={tenant.id} value={tenant.id}>
                  {tenant.name}
                </option>
              ))}
            </select>
          </div>
        </div>

        {/* Messages */}
        {error && (
          <div className="mt-4 bg-red-50 border border-red-200 text-red-700 px-4 py-3 rounded">
            {error}
            <button onClick={() => setError('')} className="ml-2 text-red-900 font-medium">Dismiss</button>
          </div>
        )}
        {success && (
          <div className="mt-4 bg-green-50 border border-green-200 text-green-700 px-4 py-3 rounded">
            {success}
          </div>
        )}

        {selectedClientId && (
          <>
            {/* Tab Navigation */}
            <div className="mt-6 border-b border-gray-200">
              <nav className="-mb-px flex space-x-8">
                {(['rules', 'config', 'preview'] as const).map((tab) => (
                  <button
                    key={tab}
                    onClick={() => setActiveTab(tab)}
                    className={`whitespace-nowrap py-4 px-1 border-b-2 font-medium text-sm ${
                      activeTab === tab
                        ? 'border-indigo-500 text-indigo-600'
                        : 'border-transparent text-gray-500 hover:text-gray-700 hover:border-gray-300'
                    }`}
                  >
                    {tab === 'rules' ? 'Mapping Rules' : tab === 'config' ? 'Token Lifetime' : 'Token Preview'}
                  </button>
                ))}
              </nav>
            </div>

            {/* Mapping Rules Tab */}
            {activeTab === 'rules' && (
              <div className="mt-6">
                {/* Add Rule Button */}
                <div className="mb-6">
                  {!showRuleForm ? (
                    <button
                      onClick={() => setShowRuleForm(true)}
                      className="inline-flex items-center px-4 py-2 border border-transparent text-sm font-medium rounded-md shadow-sm text-white bg-indigo-600 hover:bg-indigo-700"
                    >
                      Add Mapping Rule
                    </button>
                  ) : (
                    <div className="bg-white shadow rounded-lg p-6">
                      <h3 className="text-lg font-medium text-gray-900 mb-4">
                        {editingRule ? 'Edit Mapping Rule' : 'New Mapping Rule'}
                      </h3>
                      <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 lg:grid-cols-3">
                        <div>
                          <label className="block text-sm font-medium text-gray-700">Source Type</label>
                          <select
                            value={ruleForm.sourceType}
                            onChange={(e) => setRuleForm({ ...ruleForm, sourceType: e.target.value })}
                            className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                          >
                            {SOURCE_TYPES.map((st) => (
                              <option key={st} value={st}>{st}</option>
                            ))}
                          </select>
                        </div>
                        <div>
                          <label className="block text-sm font-medium text-gray-700">Source Path *</label>
                          <input
                            type="text"
                            value={ruleForm.sourcePath}
                            onChange={(e) => setRuleForm({ ...ruleForm, sourcePath: e.target.value })}
                            placeholder={ruleForm.sourceType === 'UserAttribute' ? 'e.g. Email, FirstName' : ruleForm.sourceType === 'Static' ? 'Static value' : 'e.g. AdminGroup'}
                            className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                          />
                        </div>
                        <div>
                          <label className="block text-sm font-medium text-gray-700">Target Claim *</label>
                          <input
                            type="text"
                            value={ruleForm.targetClaim}
                            onChange={(e) => setRuleForm({ ...ruleForm, targetClaim: e.target.value })}
                            placeholder="e.g. custom:department"
                            className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                          />
                        </div>
                        <div>
                          <label className="block text-sm font-medium text-gray-700">Transform</label>
                          <select
                            value={ruleForm.transform}
                            onChange={(e) => setRuleForm({ ...ruleForm, transform: e.target.value })}
                            className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                          >
                            {TRANSFORMS.map((t) => (
                              <option key={t} value={t}>{t}</option>
                            ))}
                          </select>
                        </div>
                        {ruleForm.transform !== 'None' && ruleForm.transform !== 'ToUpper' && ruleForm.transform !== 'ToLower' && (
                          <div>
                            <label className="block text-sm font-medium text-gray-700">Transform Pattern</label>
                            <input
                              type="text"
                              value={ruleForm.transformPattern}
                              onChange={(e) => setRuleForm({ ...ruleForm, transformPattern: e.target.value })}
                              placeholder={ruleForm.transform === 'Join' || ruleForm.transform === 'Split' ? 'Delimiter (e.g. ,)' : 'Format string (e.g. prefix_{0})'}
                              className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                            />
                          </div>
                        )}
                        <div>
                          <label className="block text-sm font-medium text-gray-700">Priority</label>
                          <input
                            type="number"
                            value={ruleForm.priority}
                            onChange={(e) => setRuleForm({ ...ruleForm, priority: parseInt(e.target.value) || 100 })}
                            className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                          />
                        </div>
                        <div className="flex items-end">
                          <label className="flex items-center">
                            <input
                              type="checkbox"
                              checked={ruleForm.isActive}
                              onChange={(e) => setRuleForm({ ...ruleForm, isActive: e.target.checked })}
                              className="h-4 w-4 text-indigo-600 focus:ring-indigo-500 border-gray-300 rounded"
                            />
                            <span className="ml-2 text-sm text-gray-700">Active</span>
                          </label>
                        </div>
                      </div>
                      <div className="mt-4 flex gap-3">
                        <button
                          onClick={handleCreateOrUpdateRule}
                          className="inline-flex items-center px-4 py-2 border border-transparent text-sm font-medium rounded-md shadow-sm text-white bg-indigo-600 hover:bg-indigo-700"
                        >
                          {editingRule ? 'Update Rule' : 'Create Rule'}
                        </button>
                        <button
                          onClick={resetRuleForm}
                          className="inline-flex items-center px-4 py-2 border border-gray-300 text-sm font-medium rounded-md text-gray-700 bg-white hover:bg-gray-50"
                        >
                          Cancel
                        </button>
                      </div>
                    </div>
                  )}
                </div>

                {/* Rules Table */}
                {loading ? (
                  <div className="text-center py-8">
                    <div className="inline-block animate-spin rounded-full h-8 w-8 border-b-2 border-indigo-600"></div>
                    <p className="mt-2 text-sm text-gray-500">Loading rules...</p>
                  </div>
                ) : (
                  <div className="overflow-hidden shadow ring-1 ring-black ring-opacity-5 md:rounded-lg">
                    <table className="min-w-full divide-y divide-gray-300">
                      <thead className="bg-gray-50">
                        <tr>
                          <th className="px-3 py-3.5 text-left text-sm font-semibold text-gray-900">Priority</th>
                          <th className="px-3 py-3.5 text-left text-sm font-semibold text-gray-900">Source</th>
                          <th className="px-3 py-3.5 text-left text-sm font-semibold text-gray-900">Target Claim</th>
                          <th className="px-3 py-3.5 text-left text-sm font-semibold text-gray-900">Transform</th>
                          <th className="px-3 py-3.5 text-left text-sm font-semibold text-gray-900">Status</th>
                          <th className="relative py-3.5 pl-3 pr-4 sm:pr-6">
                            <span className="sr-only">Actions</span>
                          </th>
                        </tr>
                      </thead>
                      <tbody className="divide-y divide-gray-200 bg-white">
                        {rules.length === 0 ? (
                          <tr>
                            <td colSpan={6} className="px-3 py-8 text-sm text-gray-500 text-center">
                              No mapping rules configured. Add one to customize token claims.
                            </td>
                          </tr>
                        ) : (
                          rules.map((rule) => (
                            <tr key={rule.id}>
                              <td className="whitespace-nowrap px-3 py-4 text-sm text-gray-500 font-mono">
                                {rule.priority}
                              </td>
                              <td className="px-3 py-4 text-sm">
                                <span className={`inline-flex rounded-full px-2 text-xs font-semibold leading-5 ${getSourceTypeBadgeColor(rule.sourceType)}`}>
                                  {rule.sourceType}
                                </span>
                                <div className="mt-1 text-gray-500 text-xs font-mono">{rule.sourcePath}</div>
                              </td>
                              <td className="whitespace-nowrap px-3 py-4 text-sm font-mono text-gray-900">
                                {rule.targetClaim}
                              </td>
                              <td className="whitespace-nowrap px-3 py-4 text-sm text-gray-500">
                                {rule.transform !== 'None' ? (
                                  <span className="inline-flex rounded px-2 py-0.5 text-xs font-bold bg-indigo-100 text-indigo-800">
                                    {rule.transform}
                                  </span>
                                ) : (
                                  <span className="text-gray-400">-</span>
                                )}
                                {rule.transformPattern && (
                                  <div className="text-xs text-gray-400 font-mono mt-1">{rule.transformPattern}</div>
                                )}
                              </td>
                              <td className="whitespace-nowrap px-3 py-4 text-sm">
                                <span className={`inline-flex rounded-full px-2 text-xs font-semibold leading-5 ${
                                  rule.isActive ? 'bg-green-100 text-green-800' : 'bg-red-100 text-red-800'
                                }`}>
                                  {rule.isActive ? 'Active' : 'Inactive'}
                                </span>
                              </td>
                              <td className="relative whitespace-nowrap py-4 pl-3 pr-4 text-right text-sm font-medium sm:pr-6">
                                <button
                                  onClick={() => handleEditRule(rule)}
                                  className="text-indigo-600 hover:text-indigo-900 mr-3"
                                >
                                  Edit
                                </button>
                                <button
                                  onClick={() => handleDeleteRule(rule.id)}
                                  className="text-red-600 hover:text-red-900"
                                >
                                  Delete
                                </button>
                              </td>
                            </tr>
                          ))
                        )}
                      </tbody>
                    </table>
                  </div>
                )}
              </div>
            )}

            {/* Token Configuration Tab */}
            {activeTab === 'config' && (
              <div className="mt-6">
                <div className="bg-white shadow rounded-lg p-6">
                  <div className="flex items-center justify-between mb-4">
                    <h3 className="text-lg font-medium text-gray-900">Token Lifetime & Content</h3>
                    {tokenConfig.isDefault && (
                      <span className="inline-flex rounded-full bg-yellow-100 px-2 text-xs font-semibold leading-5 text-yellow-800">
                        Using Defaults
                      </span>
                    )}
                  </div>

                  <div className="grid grid-cols-1 gap-6 sm:grid-cols-2">
                    <div>
                      <label className="block text-sm font-medium text-gray-700">Access Token Lifetime (minutes)</label>
                      <input
                        type="number"
                        min="1"
                        max="1440"
                        value={tokenConfig.accessTokenLifetimeMinutes}
                        onChange={(e) => setTokenConfig({ ...tokenConfig, accessTokenLifetimeMinutes: parseInt(e.target.value) || 15 })}
                        className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                      />
                      <p className="mt-1 text-xs text-gray-500">1 to 1440 minutes (24 hours)</p>
                    </div>
                    <div>
                      <label className="block text-sm font-medium text-gray-700">Refresh Token Lifetime (days)</label>
                      <input
                        type="number"
                        min="1"
                        max="365"
                        value={tokenConfig.refreshTokenLifetimeDays}
                        onChange={(e) => setTokenConfig({ ...tokenConfig, refreshTokenLifetimeDays: parseInt(e.target.value) || 7 })}
                        className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                      />
                      <p className="mt-1 text-xs text-gray-500">1 to 365 days</p>
                    </div>
                    <div>
                      <label className="block text-sm font-medium text-gray-700">Custom Namespace (optional)</label>
                      <input
                        type="text"
                        value={tokenConfig.customNamespace || ''}
                        onChange={(e) => setTokenConfig({ ...tokenConfig, customNamespace: e.target.value || null })}
                        placeholder="e.g. https://myapp.com/"
                        className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                      />
                      <p className="mt-1 text-xs text-gray-500">Prefixed to custom claim types</p>
                    </div>
                  </div>

                  <div className="mt-6">
                    <h4 className="text-sm font-medium text-gray-700 mb-3">Include in Token</h4>
                    <div className="space-y-3">
                      <label className="flex items-center">
                        <input
                          type="checkbox"
                          checked={tokenConfig.includeRoles}
                          onChange={(e) => setTokenConfig({ ...tokenConfig, includeRoles: e.target.checked })}
                          className="h-4 w-4 text-indigo-600 focus:ring-indigo-500 border-gray-300 rounded"
                        />
                        <span className="ml-2 text-sm text-gray-700">Roles - Include user role names as "role" claims</span>
                      </label>
                      <label className="flex items-center">
                        <input
                          type="checkbox"
                          checked={tokenConfig.includePermissions}
                          onChange={(e) => setTokenConfig({ ...tokenConfig, includePermissions: e.target.checked })}
                          className="h-4 w-4 text-indigo-600 focus:ring-indigo-500 border-gray-300 rounded"
                        />
                        <span className="ml-2 text-sm text-gray-700">Permissions - Include individual permissions from roles as "permission" claims</span>
                      </label>
                      <label className="flex items-center">
                        <input
                          type="checkbox"
                          checked={tokenConfig.includeGroups}
                          onChange={(e) => setTokenConfig({ ...tokenConfig, includeGroups: e.target.checked })}
                          className="h-4 w-4 text-indigo-600 focus:ring-indigo-500 border-gray-300 rounded"
                        />
                        <span className="ml-2 text-sm text-gray-700">Groups - Include group memberships as "group" claims</span>
                      </label>
                    </div>
                  </div>

                  <div className="mt-6">
                    <button
                      onClick={handleSaveTokenConfig}
                      className="inline-flex items-center px-4 py-2 border border-transparent text-sm font-medium rounded-md shadow-sm text-white bg-indigo-600 hover:bg-indigo-700"
                    >
                      Save Configuration
                    </button>
                  </div>
                </div>
              </div>
            )}

            {/* Token Preview Tab */}
            {activeTab === 'preview' && (
              <div className="mt-6">
                <div className="bg-white shadow rounded-lg p-6 mb-6">
                  <h3 className="text-lg font-medium text-gray-900 mb-4">Generate Token Preview</h3>
                  <p className="text-sm text-gray-500 mb-4">
                    Select a user to see what claims their access token would contain with the current configuration.
                  </p>
                  <div className="flex items-end gap-4">
                    <div className="flex-1 max-w-md">
                      <label className="block text-sm font-medium text-gray-700">User</label>
                      <select
                        value={selectedUserId}
                        onChange={(e) => setSelectedUserId(e.target.value)}
                        className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                      >
                        <option value="">Select a user...</option>
                        {users.map((user) => (
                          <option key={user.id} value={user.id}>
                            {user.firstName} {user.lastName} ({user.email})
                          </option>
                        ))}
                      </select>
                    </div>
                    <button
                      onClick={handlePreviewToken}
                      disabled={previewLoading || !selectedUserId}
                      className="inline-flex items-center px-4 py-2 border border-transparent text-sm font-medium rounded-md shadow-sm text-white bg-indigo-600 hover:bg-indigo-700 disabled:opacity-50 disabled:cursor-not-allowed"
                    >
                      {previewLoading ? 'Generating...' : 'Preview Token'}
                    </button>
                  </div>
                </div>

                {preview && (
                  <div className="space-y-6">
                    {/* Config Summary */}
                    <div className="bg-white shadow rounded-lg p-6">
                      <h4 className="text-sm font-medium text-gray-900 mb-3">Token Configuration</h4>
                      <div className="grid grid-cols-2 sm:grid-cols-4 gap-4 text-sm">
                        <div>
                          <span className="text-gray-500">Access Token:</span>
                          <span className="ml-1 font-medium">{preview.configuration.accessTokenLifetimeMinutes}min</span>
                        </div>
                        <div>
                          <span className="text-gray-500">Refresh Token:</span>
                          <span className="ml-1 font-medium">{preview.configuration.refreshTokenLifetimeDays}d</span>
                        </div>
                        <div>
                          <span className="text-gray-500">Roles:</span>
                          <span className={`ml-1 font-medium ${preview.configuration.includeRoles ? 'text-green-600' : 'text-gray-400'}`}>
                            {preview.configuration.includeRoles ? 'Yes' : 'No'}
                          </span>
                        </div>
                        <div>
                          <span className="text-gray-500">Permissions:</span>
                          <span className={`ml-1 font-medium ${preview.configuration.includePermissions ? 'text-green-600' : 'text-gray-400'}`}>
                            {preview.configuration.includePermissions ? 'Yes' : 'No'}
                          </span>
                        </div>
                      </div>
                    </div>

                    {/* Claims Table */}
                    <div className="bg-white shadow rounded-lg overflow-hidden">
                      <div className="px-6 py-4 border-b border-gray-200">
                        <h4 className="text-sm font-medium text-gray-900">
                          Token Claims ({preview.claims.length} total)
                        </h4>
                      </div>
                      <div className="overflow-x-auto">
                        <table className="min-w-full divide-y divide-gray-200">
                          <thead className="bg-gray-50">
                            <tr>
                              <th className="px-4 py-3 text-left text-sm font-semibold text-gray-900">Claim Type</th>
                              <th className="px-4 py-3 text-left text-sm font-semibold text-gray-900">Value</th>
                              <th className="px-4 py-3 text-left text-sm font-semibold text-gray-900">Source</th>
                            </tr>
                          </thead>
                          <tbody className="divide-y divide-gray-200">
                            {preview.claims.map((claim, idx) => (
                              <tr key={idx} className={claim.source === 'Standard' ? 'bg-gray-50' : ''}>
                                <td className="whitespace-nowrap px-4 py-3 text-sm font-mono text-gray-900">
                                  {claim.type}
                                </td>
                                <td className="px-4 py-3 text-sm text-gray-700 max-w-md truncate">
                                  {claim.value}
                                </td>
                                <td className="whitespace-nowrap px-4 py-3 text-sm">
                                  <span className={`inline-flex rounded-full px-2 text-xs font-semibold leading-5 ${
                                    claim.source === 'Standard' ? 'bg-gray-100 text-gray-600' :
                                    claim.source === 'UserRole' ? 'bg-purple-100 text-purple-800' :
                                    claim.source === 'GroupMembership' ? 'bg-green-100 text-green-800' :
                                    claim.source.startsWith('Role:') ? 'bg-purple-100 text-purple-800' :
                                    'bg-indigo-100 text-indigo-800'
                                  }`}>
                                    {claim.source}
                                  </span>
                                </td>
                              </tr>
                            ))}
                          </tbody>
                        </table>
                      </div>
                    </div>
                  </div>
                )}
              </div>
            )}
          </>
        )}
      </div>
    </DashboardLayout>
  );
}
