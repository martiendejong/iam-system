import { useState, useEffect } from 'react';
import DashboardLayout from '../../components/layout/DashboardLayout';
import { api } from '../../services/api';
import { scimApi } from '../../services/scimApi';
import type { ScimToken, ScimProvisioningLog } from '../../services/scimApi';

interface Tenant {
  id: string;
  name: string;
  slug: string;
}

export default function ScimConfigPage() {
  const [tenants, setTenants] = useState<Tenant[]>([]);
  const [selectedTenantId, setSelectedTenantId] = useState<string>('');
  const [tokens, setTokens] = useState<ScimToken[]>([]);
  const [logs, setLogs] = useState<ScimProvisioningLog[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');
  const [activeTab, setActiveTab] = useState<'tokens' | 'logs' | 'endpoints'>('tokens');

  // Token creation form
  const [showCreateToken, setShowCreateToken] = useState(false);
  const [newTokenName, setNewTokenName] = useState('');
  const [newTokenDescription, setNewTokenDescription] = useState('');
  const [newTokenExpiry, setNewTokenExpiry] = useState('');
  const [createdToken, setCreatedToken] = useState<string | null>(null);
  const [tokenCopied, setTokenCopied] = useState(false);

  useEffect(() => {
    loadTenants();
  }, []);

  useEffect(() => {
    if (selectedTenantId) {
      loadTokens();
      loadLogs();
    }
  }, [selectedTenantId]);

  const loadTenants = async () => {
    try {
      const data = await api.getTenants();
      setTenants(data);
      if (data.length > 0 && !selectedTenantId) {
        setSelectedTenantId(data[0].id);
      }
    } catch (err: any) {
      setError(err.response?.data?.error || 'Failed to load tenants');
    }
  };

  const loadTokens = async () => {
    try {
      setLoading(true);
      const data = await scimApi.getTokens(selectedTenantId);
      setTokens(data);
    } catch (err: any) {
      setError(err.response?.data?.error || 'Failed to load SCIM tokens');
    } finally {
      setLoading(false);
    }
  };

  const loadLogs = async () => {
    try {
      const data = await scimApi.getProvisioningLogs(selectedTenantId);
      setLogs(data);
    } catch {
      // Non-critical, don't show error
    }
  };

  const handleCreateToken = async () => {
    if (!newTokenName.trim()) {
      setError('Token name is required');
      return;
    }

    try {
      setError('');
      const result = await scimApi.createToken({
        tenantId: selectedTenantId,
        name: newTokenName.trim(),
        description: newTokenDescription.trim() || undefined,
        expiresAt: newTokenExpiry || undefined,
      });

      setCreatedToken(result.token || null);
      setNewTokenName('');
      setNewTokenDescription('');
      setNewTokenExpiry('');
      await loadTokens();
    } catch (err: any) {
      setError(err.response?.data?.error || 'Failed to create token');
    }
  };

  const handleRevokeToken = async (tokenId: string) => {
    if (!confirm('Are you sure you want to revoke this token? This cannot be undone.')) return;

    try {
      await scimApi.revokeToken(tokenId);
      await loadTokens();
    } catch (err: any) {
      setError(err.response?.data?.error || 'Failed to revoke token');
    }
  };

  const copyToClipboard = async (text: string) => {
    try {
      await navigator.clipboard.writeText(text);
      setTokenCopied(true);
      setTimeout(() => setTokenCopied(false), 2000);
    } catch {
      // Fallback
      const textarea = document.createElement('textarea');
      textarea.value = text;
      document.body.appendChild(textarea);
      textarea.select();
      document.execCommand('copy');
      document.body.removeChild(textarea);
      setTokenCopied(true);
      setTimeout(() => setTokenCopied(false), 2000);
    }
  };

  const formatDate = (dateStr?: string) => {
    if (!dateStr) return 'Never';
    return new Date(dateStr).toLocaleString();
  };

  const getBaseUrl = () => {
    return window.location.origin.replace(/:\d+$/, ':5001');
  };

  return (
    <DashboardLayout>
      <div className="px-4 sm:px-6 lg:px-8">
        {/* Header */}
        <div className="sm:flex sm:items-center">
          <div className="sm:flex-auto">
            <h1 className="text-2xl font-semibold text-gray-900">SCIM 2.0 Provisioning</h1>
            <p className="mt-2 text-sm text-gray-700">
              Configure SCIM 2.0 (RFC 7644) user provisioning for identity providers like Okta, Azure AD, and OneLogin.
            </p>
          </div>
        </div>

        {/* Tenant Selector */}
        <div className="mt-6">
          <label htmlFor="tenant" className="block text-sm font-medium text-gray-700">
            Tenant
          </label>
          <select
            id="tenant"
            value={selectedTenantId}
            onChange={(e) => setSelectedTenantId(e.target.value)}
            className="mt-1 block w-full max-w-xs rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
          >
            <option value="">Select a tenant...</option>
            {tenants.map((tenant) => (
              <option key={tenant.id} value={tenant.id}>
                {tenant.name}
              </option>
            ))}
          </select>
        </div>

        {/* Error Message */}
        {error && (
          <div className="mt-4 bg-red-50 border border-red-200 text-red-700 px-4 py-3 rounded">
            {error}
            <button onClick={() => setError('')} className="ml-2 text-red-900 font-medium">
              Dismiss
            </button>
          </div>
        )}

        {selectedTenantId && (
          <>
            {/* Tab Navigation */}
            <div className="mt-6 border-b border-gray-200">
              <nav className="-mb-px flex space-x-8">
                <button
                  onClick={() => setActiveTab('tokens')}
                  className={`whitespace-nowrap py-4 px-1 border-b-2 font-medium text-sm ${
                    activeTab === 'tokens'
                      ? 'border-indigo-500 text-indigo-600'
                      : 'border-transparent text-gray-500 hover:text-gray-700 hover:border-gray-300'
                  }`}
                >
                  Bearer Tokens
                </button>
                <button
                  onClick={() => setActiveTab('logs')}
                  className={`whitespace-nowrap py-4 px-1 border-b-2 font-medium text-sm ${
                    activeTab === 'logs'
                      ? 'border-indigo-500 text-indigo-600'
                      : 'border-transparent text-gray-500 hover:text-gray-700 hover:border-gray-300'
                  }`}
                >
                  Provisioning Logs
                </button>
                <button
                  onClick={() => setActiveTab('endpoints')}
                  className={`whitespace-nowrap py-4 px-1 border-b-2 font-medium text-sm ${
                    activeTab === 'endpoints'
                      ? 'border-indigo-500 text-indigo-600'
                      : 'border-transparent text-gray-500 hover:text-gray-700 hover:border-gray-300'
                  }`}
                >
                  Endpoint Reference
                </button>
              </nav>
            </div>

            {/* Token Management Tab */}
            {activeTab === 'tokens' && (
              <div className="mt-6">
                {/* Created Token Banner */}
                {createdToken && (
                  <div className="mb-6 bg-green-50 border border-green-200 rounded-lg p-4">
                    <h3 className="text-sm font-medium text-green-800">Token Created Successfully</h3>
                    <p className="mt-1 text-sm text-green-700">
                      Copy this token now. It will not be shown again.
                    </p>
                    <div className="mt-2 flex items-center gap-2">
                      <code className="flex-1 bg-white border border-green-300 rounded px-3 py-2 text-sm font-mono break-all">
                        {createdToken}
                      </code>
                      <button
                        onClick={() => copyToClipboard(createdToken)}
                        className="inline-flex items-center px-3 py-2 border border-green-300 text-sm font-medium rounded-md text-green-700 bg-white hover:bg-green-50"
                      >
                        {tokenCopied ? 'Copied!' : 'Copy'}
                      </button>
                    </div>
                    <button
                      onClick={() => setCreatedToken(null)}
                      className="mt-2 text-sm text-green-600 hover:text-green-800"
                    >
                      Dismiss
                    </button>
                  </div>
                )}

                {/* Create Token Form */}
                <div className="mb-6">
                  {!showCreateToken ? (
                    <button
                      onClick={() => setShowCreateToken(true)}
                      className="inline-flex items-center px-4 py-2 border border-transparent text-sm font-medium rounded-md shadow-sm text-white bg-indigo-600 hover:bg-indigo-700"
                    >
                      Generate New Token
                    </button>
                  ) : (
                    <div className="bg-white shadow rounded-lg p-6">
                      <h3 className="text-lg font-medium text-gray-900 mb-4">Generate SCIM Bearer Token</h3>
                      <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
                        <div>
                          <label className="block text-sm font-medium text-gray-700">Name *</label>
                          <input
                            type="text"
                            value={newTokenName}
                            onChange={(e) => setNewTokenName(e.target.value)}
                            placeholder="e.g., Okta Production"
                            className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                          />
                        </div>
                        <div>
                          <label className="block text-sm font-medium text-gray-700">Expires At (optional)</label>
                          <input
                            type="datetime-local"
                            value={newTokenExpiry}
                            onChange={(e) => setNewTokenExpiry(e.target.value)}
                            className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                          />
                        </div>
                        <div className="sm:col-span-2">
                          <label className="block text-sm font-medium text-gray-700">Description (optional)</label>
                          <input
                            type="text"
                            value={newTokenDescription}
                            onChange={(e) => setNewTokenDescription(e.target.value)}
                            placeholder="e.g., Used by Okta for user provisioning"
                            className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                          />
                        </div>
                      </div>
                      <div className="mt-4 flex gap-3">
                        <button
                          onClick={handleCreateToken}
                          className="inline-flex items-center px-4 py-2 border border-transparent text-sm font-medium rounded-md shadow-sm text-white bg-indigo-600 hover:bg-indigo-700"
                        >
                          Generate Token
                        </button>
                        <button
                          onClick={() => {
                            setShowCreateToken(false);
                            setNewTokenName('');
                            setNewTokenDescription('');
                            setNewTokenExpiry('');
                          }}
                          className="inline-flex items-center px-4 py-2 border border-gray-300 text-sm font-medium rounded-md text-gray-700 bg-white hover:bg-gray-50"
                        >
                          Cancel
                        </button>
                      </div>
                    </div>
                  )}
                </div>

                {/* Tokens Table */}
                {loading ? (
                  <div className="text-center py-8">
                    <div className="inline-block animate-spin rounded-full h-8 w-8 border-b-2 border-indigo-600"></div>
                    <p className="mt-2 text-sm text-gray-500">Loading tokens...</p>
                  </div>
                ) : (
                  <div className="overflow-hidden shadow ring-1 ring-black ring-opacity-5 md:rounded-lg">
                    <table className="min-w-full divide-y divide-gray-300">
                      <thead className="bg-gray-50">
                        <tr>
                          <th className="px-3 py-3.5 text-left text-sm font-semibold text-gray-900">Name</th>
                          <th className="px-3 py-3.5 text-left text-sm font-semibold text-gray-900">Prefix</th>
                          <th className="px-3 py-3.5 text-left text-sm font-semibold text-gray-900">Status</th>
                          <th className="px-3 py-3.5 text-left text-sm font-semibold text-gray-900">Created</th>
                          <th className="px-3 py-3.5 text-left text-sm font-semibold text-gray-900">Expires</th>
                          <th className="px-3 py-3.5 text-left text-sm font-semibold text-gray-900">Last Used</th>
                          <th className="relative py-3.5 pl-3 pr-4 sm:pr-6">
                            <span className="sr-only">Actions</span>
                          </th>
                        </tr>
                      </thead>
                      <tbody className="divide-y divide-gray-200 bg-white">
                        {tokens.length === 0 ? (
                          <tr>
                            <td colSpan={7} className="px-3 py-8 text-sm text-gray-500 text-center">
                              No SCIM tokens configured. Generate one to enable provisioning.
                            </td>
                          </tr>
                        ) : (
                          tokens.map((token) => (
                            <tr key={token.id}>
                              <td className="whitespace-nowrap px-3 py-4 text-sm">
                                <div className="font-medium text-gray-900">{token.name}</div>
                                {token.description && (
                                  <div className="text-gray-500 text-xs">{token.description}</div>
                                )}
                              </td>
                              <td className="whitespace-nowrap px-3 py-4 text-sm">
                                <code className="text-gray-600 bg-gray-100 px-1.5 py-0.5 rounded text-xs">
                                  {token.tokenPrefix}...
                                </code>
                              </td>
                              <td className="whitespace-nowrap px-3 py-4 text-sm">
                                <span
                                  className={`inline-flex rounded-full px-2 text-xs font-semibold leading-5 ${
                                    token.isActive
                                      ? 'bg-green-100 text-green-800'
                                      : 'bg-red-100 text-red-800'
                                  }`}
                                >
                                  {token.isActive ? 'Active' : 'Revoked'}
                                </span>
                              </td>
                              <td className="whitespace-nowrap px-3 py-4 text-sm text-gray-500">
                                {formatDate(token.createdAt)}
                              </td>
                              <td className="whitespace-nowrap px-3 py-4 text-sm text-gray-500">
                                {token.expiresAt ? formatDate(token.expiresAt) : 'Never'}
                              </td>
                              <td className="whitespace-nowrap px-3 py-4 text-sm text-gray-500">
                                {formatDate(token.lastUsedAt)}
                              </td>
                              <td className="relative whitespace-nowrap py-4 pl-3 pr-4 text-right text-sm font-medium sm:pr-6">
                                {token.isActive && (
                                  <button
                                    onClick={() => handleRevokeToken(token.id)}
                                    className="text-red-600 hover:text-red-900"
                                  >
                                    Revoke
                                  </button>
                                )}
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

            {/* Provisioning Logs Tab */}
            {activeTab === 'logs' && (
              <div className="mt-6">
                <div className="flex justify-between items-center mb-4">
                  <h3 className="text-lg font-medium text-gray-900">Recent Provisioning Activity</h3>
                  <button
                    onClick={loadLogs}
                    className="inline-flex items-center px-3 py-1.5 border border-gray-300 text-sm font-medium rounded-md text-gray-700 bg-white hover:bg-gray-50"
                  >
                    Refresh
                  </button>
                </div>

                <div className="overflow-hidden shadow ring-1 ring-black ring-opacity-5 md:rounded-lg">
                  <table className="min-w-full divide-y divide-gray-300">
                    <thead className="bg-gray-50">
                      <tr>
                        <th className="px-3 py-3.5 text-left text-sm font-semibold text-gray-900">Time</th>
                        <th className="px-3 py-3.5 text-left text-sm font-semibold text-gray-900">Operation</th>
                        <th className="px-3 py-3.5 text-left text-sm font-semibold text-gray-900">Resource</th>
                        <th className="px-3 py-3.5 text-left text-sm font-semibold text-gray-900">External ID</th>
                        <th className="px-3 py-3.5 text-left text-sm font-semibold text-gray-900">Status</th>
                        <th className="px-3 py-3.5 text-left text-sm font-semibold text-gray-900">Details</th>
                      </tr>
                    </thead>
                    <tbody className="divide-y divide-gray-200 bg-white">
                      {logs.length === 0 ? (
                        <tr>
                          <td colSpan={6} className="px-3 py-8 text-sm text-gray-500 text-center">
                            No provisioning activity yet.
                          </td>
                        </tr>
                      ) : (
                        logs.map((log) => (
                          <tr key={log.id}>
                            <td className="whitespace-nowrap px-3 py-4 text-sm text-gray-500">
                              {formatDate(log.createdAt)}
                            </td>
                            <td className="whitespace-nowrap px-3 py-4 text-sm">
                              <span
                                className={`inline-flex rounded-full px-2 text-xs font-semibold leading-5 ${
                                  log.operation === 'Create'
                                    ? 'bg-green-100 text-green-800'
                                    : log.operation === 'Update'
                                    ? 'bg-blue-100 text-blue-800'
                                    : log.operation === 'Delete'
                                    ? 'bg-red-100 text-red-800'
                                    : 'bg-gray-100 text-gray-800'
                                }`}
                              >
                                {log.operation}
                              </span>
                            </td>
                            <td className="whitespace-nowrap px-3 py-4 text-sm">
                              <span className="inline-flex rounded-full bg-indigo-100 px-2 text-xs font-semibold leading-5 text-indigo-800">
                                {log.resourceType}
                              </span>
                            </td>
                            <td className="whitespace-nowrap px-3 py-4 text-sm text-gray-500 font-mono text-xs">
                              {log.externalId || '-'}
                            </td>
                            <td className="whitespace-nowrap px-3 py-4 text-sm">
                              <span
                                className={`inline-flex rounded-full px-2 text-xs font-semibold leading-5 ${
                                  log.status === 'Success'
                                    ? 'bg-green-100 text-green-800'
                                    : log.status === 'Failed'
                                    ? 'bg-red-100 text-red-800'
                                    : 'bg-yellow-100 text-yellow-800'
                                }`}
                              >
                                {log.status}
                              </span>
                            </td>
                            <td className="px-3 py-4 text-sm text-gray-500 max-w-xs truncate">
                              {log.details || '-'}
                            </td>
                          </tr>
                        ))
                      )}
                    </tbody>
                  </table>
                </div>
              </div>
            )}

            {/* Endpoint Reference Tab */}
            {activeTab === 'endpoints' && (
              <div className="mt-6 space-y-6">
                {/* SCIM Base URL */}
                <div className="bg-white shadow rounded-lg p-6">
                  <h3 className="text-lg font-medium text-gray-900 mb-2">SCIM Base URL</h3>
                  <p className="text-sm text-gray-500 mb-3">
                    Use this URL when configuring your identity provider (Okta, Azure AD, OneLogin, etc.)
                  </p>
                  <div className="flex items-center gap-2">
                    <code className="flex-1 bg-gray-100 border border-gray-300 rounded px-3 py-2 text-sm font-mono">
                      {getBaseUrl()}/scim/v2
                    </code>
                    <button
                      onClick={() => copyToClipboard(`${getBaseUrl()}/scim/v2`)}
                      className="inline-flex items-center px-3 py-2 border border-gray-300 text-sm font-medium rounded-md text-gray-700 bg-white hover:bg-gray-50"
                    >
                      Copy
                    </button>
                  </div>
                </div>

                {/* Endpoint Table */}
                <div className="bg-white shadow rounded-lg p-6">
                  <h3 className="text-lg font-medium text-gray-900 mb-4">Available Endpoints</h3>
                  <div className="overflow-hidden ring-1 ring-gray-200 rounded-lg">
                    <table className="min-w-full divide-y divide-gray-200">
                      <thead className="bg-gray-50">
                        <tr>
                          <th className="px-4 py-3 text-left text-sm font-semibold text-gray-900">Method</th>
                          <th className="px-4 py-3 text-left text-sm font-semibold text-gray-900">Endpoint</th>
                          <th className="px-4 py-3 text-left text-sm font-semibold text-gray-900">Description</th>
                        </tr>
                      </thead>
                      <tbody className="divide-y divide-gray-200">
                        {[
                          { method: 'GET', endpoint: '/scim/v2/Users', desc: 'List/search users' },
                          { method: 'POST', endpoint: '/scim/v2/Users', desc: 'Create user' },
                          { method: 'GET', endpoint: '/scim/v2/Users/{id}', desc: 'Get user by ID' },
                          { method: 'PUT', endpoint: '/scim/v2/Users/{id}', desc: 'Replace user' },
                          { method: 'PATCH', endpoint: '/scim/v2/Users/{id}', desc: 'Patch user (partial update)' },
                          { method: 'DELETE', endpoint: '/scim/v2/Users/{id}', desc: 'Delete user (soft delete)' },
                          { method: 'GET', endpoint: '/scim/v2/Groups', desc: 'List/search groups' },
                          { method: 'POST', endpoint: '/scim/v2/Groups', desc: 'Create group' },
                          { method: 'GET', endpoint: '/scim/v2/Groups/{id}', desc: 'Get group by ID' },
                          { method: 'PUT', endpoint: '/scim/v2/Groups/{id}', desc: 'Replace group' },
                          { method: 'PATCH', endpoint: '/scim/v2/Groups/{id}', desc: 'Patch group' },
                          { method: 'DELETE', endpoint: '/scim/v2/Groups/{id}', desc: 'Delete group' },
                          { method: 'GET', endpoint: '/scim/v2/ServiceProviderConfig', desc: 'SCIM capabilities' },
                          { method: 'GET', endpoint: '/scim/v2/Schemas', desc: 'Schema definitions' },
                          { method: 'GET', endpoint: '/scim/v2/ResourceTypes', desc: 'Resource type definitions' },
                        ].map((row, i) => (
                          <tr key={i}>
                            <td className="whitespace-nowrap px-4 py-3 text-sm">
                              <span
                                className={`inline-flex rounded px-2 py-0.5 text-xs font-bold ${
                                  row.method === 'GET'
                                    ? 'bg-blue-100 text-blue-800'
                                    : row.method === 'POST'
                                    ? 'bg-green-100 text-green-800'
                                    : row.method === 'PUT'
                                    ? 'bg-yellow-100 text-yellow-800'
                                    : row.method === 'PATCH'
                                    ? 'bg-purple-100 text-purple-800'
                                    : 'bg-red-100 text-red-800'
                                }`}
                              >
                                {row.method}
                              </span>
                            </td>
                            <td className="whitespace-nowrap px-4 py-3 text-sm font-mono text-gray-900">
                              {row.endpoint}
                            </td>
                            <td className="px-4 py-3 text-sm text-gray-500">{row.desc}</td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  </div>
                </div>

                {/* Supported Filters */}
                <div className="bg-white shadow rounded-lg p-6">
                  <h3 className="text-lg font-medium text-gray-900 mb-4">Supported Filter Operators</h3>
                  <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 gap-4">
                    {[
                      { op: 'eq', example: 'userName eq "john@example.com"', desc: 'Equal' },
                      { op: 'co', example: 'displayName co "John"', desc: 'Contains' },
                      { op: 'sw', example: 'userName sw "john"', desc: 'Starts with' },
                      { op: 'pr', example: 'phoneNumbers pr', desc: 'Present (has value)' },
                      { op: 'gt', example: 'meta.created gt "2026-01-01"', desc: 'Greater than' },
                      { op: 'lt', example: 'meta.created lt "2026-12-31"', desc: 'Less than' },
                    ].map((filter, i) => (
                      <div key={i} className="border border-gray-200 rounded-lg p-3">
                        <div className="flex items-center gap-2 mb-1">
                          <code className="bg-indigo-100 text-indigo-800 px-1.5 py-0.5 rounded text-xs font-bold">
                            {filter.op}
                          </code>
                          <span className="text-sm font-medium text-gray-900">{filter.desc}</span>
                        </div>
                        <code className="text-xs text-gray-500">{filter.example}</code>
                      </div>
                    ))}
                  </div>
                </div>

                {/* Authentication Info */}
                <div className="bg-white shadow rounded-lg p-6">
                  <h3 className="text-lg font-medium text-gray-900 mb-2">Authentication</h3>
                  <p className="text-sm text-gray-500 mb-3">
                    All SCIM endpoints require a Bearer token in the Authorization header:
                  </p>
                  <code className="block bg-gray-100 border border-gray-300 rounded px-3 py-2 text-sm font-mono">
                    Authorization: Bearer &lt;your-scim-token&gt;
                  </code>
                  <p className="mt-3 text-sm text-gray-500">
                    Generate a token in the "Bearer Tokens" tab above. Each token is scoped to a specific tenant.
                  </p>
                </div>
              </div>
            )}
          </>
        )}
      </div>
    </DashboardLayout>
  );
}
