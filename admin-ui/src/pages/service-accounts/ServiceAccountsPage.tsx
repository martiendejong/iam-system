import { useState, useEffect } from 'react';
import DashboardLayout from '../../components/layout/DashboardLayout';
import { serviceAccountApi } from '../../services/serviceAccountApi';
import type { ServiceAccount, CreateServiceAccountRequest } from '../../services/serviceAccountApi';

const SERVICE_ACCOUNT_TYPES = [
  { value: 0, label: 'API' },
  { value: 1, label: 'Service' },
  { value: 2, label: 'Worker' },
];

interface Tenant {
  id: string;
  name: string;
  slug: string;
}

export default function ServiceAccountsPage() {
  const [accounts, setAccounts] = useState<ServiceAccount[]>([]);
  const [tenants, setTenants] = useState<Tenant[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');
  const [success, setSuccess] = useState('');
  const [activeTab, setActiveTab] = useState<'list' | 'create'>('list');

  // Filters
  const [filterTenantId, setFilterTenantId] = useState('');
  const [filterType, setFilterType] = useState('');
  const [filterActive, setFilterActive] = useState<string>('true');

  // Create form
  const [newName, setNewName] = useState('');
  const [newTenantId, setNewTenantId] = useState('');
  const [newType, setNewType] = useState(0);
  const [newDescription, setNewDescription] = useState('');
  const [newPermissions, setNewPermissions] = useState('');
  const [newCertThumbprint, setNewCertThumbprint] = useState('');

  // Secret display (shown once after create/rotate)
  const [displayedSecret, setDisplayedSecret] = useState<{ clientId: string; secret: string } | null>(null);

  // Test connectivity
  const [testClientId, setTestClientId] = useState('');
  const [testClientSecret, setTestClientSecret] = useState('');
  const [testResult, setTestResult] = useState<string | null>(null);
  const [testLoading, setTestLoading] = useState(false);

  useEffect(() => {
    loadAccounts();
    loadTenants();
  }, []);

  const loadAccounts = async () => {
    setLoading(true);
    setError('');
    try {
      const isActive = filterActive === '' ? undefined : filterActive === 'true';
      const data = await serviceAccountApi.list(
        filterTenantId || undefined,
        filterType || undefined,
        isActive
      );
      setAccounts(data);
    } catch (err: any) {
      setError(err.response?.data?.error || 'Failed to load service accounts');
    } finally {
      setLoading(false);
    }
  };

  const loadTenants = async () => {
    try {
      const data = await serviceAccountApi.getTenants();
      setTenants(data);
    } catch {
      // Non-critical
    }
  };

  const handleCreate = async (e: React.FormEvent) => {
    e.preventDefault();
    setLoading(true);
    setError('');
    setSuccess('');
    setDisplayedSecret(null);

    try {
      const permissions = newPermissions
        .split(',')
        .map(p => p.trim())
        .filter(p => p.length > 0);

      const request: CreateServiceAccountRequest = {
        name: newName,
        tenantId: newTenantId || null,
        type: newType,
        permissions: permissions.length > 0 ? permissions : undefined,
        certificateThumbprint: newCertThumbprint || null,
        description: newDescription || null,
      };

      const result = await serviceAccountApi.create(request);

      setDisplayedSecret({
        clientId: result.clientId,
        secret: result.clientSecret || '',
      });

      setSuccess('Service account created successfully. Save the client secret below - it will NOT be shown again.');

      // Reset form
      setNewName('');
      setNewTenantId('');
      setNewType(0);
      setNewDescription('');
      setNewPermissions('');
      setNewCertThumbprint('');

      // Refresh list
      loadAccounts();
    } catch (err: any) {
      setError(err.response?.data?.error || 'Failed to create service account');
    } finally {
      setLoading(false);
    }
  };

  const handleRotateSecret = async (id: string) => {
    if (!confirm('Are you sure? This will invalidate the current client secret immediately.')) return;

    setError('');
    setSuccess('');
    setDisplayedSecret(null);

    try {
      const result = await serviceAccountApi.rotateSecret(id);
      setDisplayedSecret({
        clientId: accounts.find(a => a.id === id)?.clientId || '',
        secret: result.clientSecret,
      });
      setSuccess('Client secret rotated. Save the new secret below - it will NOT be shown again.');
    } catch (err: any) {
      setError(err.response?.data?.error || 'Failed to rotate secret');
    }
  };

  const handleToggleActive = async (account: ServiceAccount) => {
    setError('');
    try {
      await serviceAccountApi.update(account.id, { isActive: !account.isActive });
      loadAccounts();
    } catch (err: any) {
      setError(err.response?.data?.error || 'Failed to update service account');
    }
  };

  const handleDelete = async (id: string) => {
    if (!confirm('Are you sure you want to permanently delete this service account?')) return;

    setError('');
    try {
      await serviceAccountApi.delete(id);
      setSuccess('Service account deleted.');
      loadAccounts();
    } catch (err: any) {
      setError(err.response?.data?.error || 'Failed to delete service account');
    }
  };

  const handleTestConnectivity = async (e: React.FormEvent) => {
    e.preventDefault();
    setTestLoading(true);
    setTestResult(null);
    setError('');

    try {
      const result = await serviceAccountApi.testToken(testClientId, testClientSecret);
      setTestResult(`Success! Token received. Expires in ${result.expires_in}s. Token type: ${result.token_type}`);
    } catch (err: any) {
      const msg = err.response?.data?.error_description || err.response?.data?.error || 'Authentication failed';
      setTestResult(`Failed: ${msg}`);
    } finally {
      setTestLoading(false);
    }
  };

  const copyToClipboard = (text: string) => {
    navigator.clipboard.writeText(text);
    setSuccess('Copied to clipboard!');
    setTimeout(() => setSuccess(''), 2000);
  };

  return (
    <DashboardLayout>
      <div className="space-y-6">
        <div className="flex justify-between items-center">
          <h1 className="text-2xl font-bold text-gray-900">Service Accounts</h1>
          <div className="space-x-2">
            <button
              onClick={() => { setActiveTab('list'); setDisplayedSecret(null); }}
              className={`px-4 py-2 rounded-md text-sm font-medium ${
                activeTab === 'list'
                  ? 'bg-indigo-600 text-white'
                  : 'bg-white text-gray-700 border border-gray-300 hover:bg-gray-50'
              }`}
            >
              List
            </button>
            <button
              onClick={() => { setActiveTab('create'); setDisplayedSecret(null); }}
              className={`px-4 py-2 rounded-md text-sm font-medium ${
                activeTab === 'create'
                  ? 'bg-indigo-600 text-white'
                  : 'bg-white text-gray-700 border border-gray-300 hover:bg-gray-50'
              }`}
            >
              Create New
            </button>
          </div>
        </div>

        {/* Messages */}
        {error && (
          <div className="bg-red-50 border border-red-200 text-red-700 px-4 py-3 rounded">{error}</div>
        )}
        {success && (
          <div className="bg-green-50 border border-green-200 text-green-700 px-4 py-3 rounded">{success}</div>
        )}

        {/* Displayed Secret (shown once after create/rotate) */}
        {displayedSecret && (
          <div className="bg-yellow-50 border border-yellow-300 rounded-lg p-4">
            <h3 className="text-lg font-semibold text-yellow-800 mb-2">Client Credentials (Save Now!)</h3>
            <p className="text-sm text-yellow-700 mb-3">
              These credentials will NOT be shown again. Copy and store them securely.
            </p>
            <div className="space-y-2">
              <div className="flex items-center space-x-2">
                <span className="text-sm font-medium text-gray-700 w-24">Client ID:</span>
                <code className="flex-1 bg-white px-3 py-1 rounded border text-sm font-mono">
                  {displayedSecret.clientId}
                </code>
                <button
                  onClick={() => copyToClipboard(displayedSecret.clientId)}
                  className="text-sm text-indigo-600 hover:text-indigo-800"
                >
                  Copy
                </button>
              </div>
              <div className="flex items-center space-x-2">
                <span className="text-sm font-medium text-gray-700 w-24">Secret:</span>
                <code className="flex-1 bg-white px-3 py-1 rounded border text-sm font-mono break-all">
                  {displayedSecret.secret}
                </code>
                <button
                  onClick={() => copyToClipboard(displayedSecret.secret)}
                  className="text-sm text-indigo-600 hover:text-indigo-800"
                >
                  Copy
                </button>
              </div>
            </div>
          </div>
        )}

        {activeTab === 'list' && (
          <>
            {/* Filters */}
            <div className="bg-white shadow rounded-lg p-4">
              <div className="grid grid-cols-4 gap-4">
                <div>
                  <label className="block text-sm font-medium text-gray-700 mb-1">Tenant</label>
                  <select
                    value={filterTenantId}
                    onChange={e => setFilterTenantId(e.target.value)}
                    className="w-full border-gray-300 rounded-md shadow-sm text-sm"
                  >
                    <option value="">All Tenants</option>
                    {tenants.map(t => (
                      <option key={t.id} value={t.id}>{t.name}</option>
                    ))}
                  </select>
                </div>
                <div>
                  <label className="block text-sm font-medium text-gray-700 mb-1">Type</label>
                  <select
                    value={filterType}
                    onChange={e => setFilterType(e.target.value)}
                    className="w-full border-gray-300 rounded-md shadow-sm text-sm"
                  >
                    <option value="">All Types</option>
                    {SERVICE_ACCOUNT_TYPES.map(t => (
                      <option key={t.value} value={t.value}>{t.label}</option>
                    ))}
                  </select>
                </div>
                <div>
                  <label className="block text-sm font-medium text-gray-700 mb-1">Status</label>
                  <select
                    value={filterActive}
                    onChange={e => setFilterActive(e.target.value)}
                    className="w-full border-gray-300 rounded-md shadow-sm text-sm"
                  >
                    <option value="">All</option>
                    <option value="true">Active</option>
                    <option value="false">Inactive</option>
                  </select>
                </div>
                <div className="flex items-end">
                  <button
                    onClick={loadAccounts}
                    className="px-4 py-2 bg-indigo-600 text-white rounded-md text-sm hover:bg-indigo-700"
                  >
                    Filter
                  </button>
                </div>
              </div>
            </div>

            {/* Accounts Table */}
            <div className="bg-white shadow rounded-lg overflow-hidden">
              {loading ? (
                <div className="p-8 text-center text-gray-500">Loading...</div>
              ) : accounts.length === 0 ? (
                <div className="p-8 text-center text-gray-500">No service accounts found</div>
              ) : (
                <table className="min-w-full divide-y divide-gray-200">
                  <thead className="bg-gray-50">
                    <tr>
                      <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase">Name</th>
                      <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase">Client ID</th>
                      <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase">Type</th>
                      <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase">Tenant</th>
                      <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase">Status</th>
                      <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase">Last Auth</th>
                      <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase">Actions</th>
                    </tr>
                  </thead>
                  <tbody className="bg-white divide-y divide-gray-200">
                    {accounts.map(account => (
                      <tr key={account.id} className="hover:bg-gray-50">
                        <td className="px-6 py-4 whitespace-nowrap">
                          <div>
                            <div className="text-sm font-medium text-gray-900">{account.name}</div>
                            {account.description && (
                              <div className="text-xs text-gray-500">{account.description}</div>
                            )}
                          </div>
                        </td>
                        <td className="px-6 py-4 whitespace-nowrap">
                          <code className="text-xs bg-gray-100 px-2 py-1 rounded font-mono">
                            {account.clientId}
                          </code>
                        </td>
                        <td className="px-6 py-4 whitespace-nowrap">
                          <span className={`inline-flex items-center px-2.5 py-0.5 rounded-full text-xs font-medium ${
                            account.type === 'Api' ? 'bg-blue-100 text-blue-800' :
                            account.type === 'Service' ? 'bg-purple-100 text-purple-800' :
                            'bg-orange-100 text-orange-800'
                          }`}>
                            {account.type}
                          </span>
                        </td>
                        <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-500">
                          {account.tenantName || '-'}
                        </td>
                        <td className="px-6 py-4 whitespace-nowrap">
                          <span className={`inline-flex items-center px-2.5 py-0.5 rounded-full text-xs font-medium ${
                            account.isActive ? 'bg-green-100 text-green-800' : 'bg-red-100 text-red-800'
                          }`}>
                            {account.isActive ? 'Active' : 'Inactive'}
                          </span>
                        </td>
                        <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-500">
                          {account.lastAuthenticatedAt
                            ? new Date(account.lastAuthenticatedAt).toLocaleString()
                            : 'Never'}
                        </td>
                        <td className="px-6 py-4 whitespace-nowrap text-sm space-x-2">
                          <button
                            onClick={() => handleRotateSecret(account.id)}
                            className="text-indigo-600 hover:text-indigo-900"
                          >
                            Rotate Secret
                          </button>
                          <button
                            onClick={() => handleToggleActive(account)}
                            className={account.isActive ? 'text-yellow-600 hover:text-yellow-900' : 'text-green-600 hover:text-green-900'}
                          >
                            {account.isActive ? 'Deactivate' : 'Activate'}
                          </button>
                          <button
                            onClick={() => handleDelete(account.id)}
                            className="text-red-600 hover:text-red-900"
                          >
                            Delete
                          </button>
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              )}
            </div>

            {/* Test Connectivity */}
            <div className="bg-white shadow rounded-lg p-6">
              <h2 className="text-lg font-semibold text-gray-900 mb-4">Test Connectivity</h2>
              <p className="text-sm text-gray-500 mb-4">
                Test a service account's client credentials by requesting an access token.
              </p>
              <form onSubmit={handleTestConnectivity} className="space-y-4">
                <div className="grid grid-cols-2 gap-4">
                  <div>
                    <label className="block text-sm font-medium text-gray-700 mb-1">Client ID</label>
                    <input
                      type="text"
                      value={testClientId}
                      onChange={e => setTestClientId(e.target.value)}
                      required
                      className="w-full border-gray-300 rounded-md shadow-sm text-sm"
                      placeholder="svc_..."
                    />
                  </div>
                  <div>
                    <label className="block text-sm font-medium text-gray-700 mb-1">Client Secret</label>
                    <input
                      type="password"
                      value={testClientSecret}
                      onChange={e => setTestClientSecret(e.target.value)}
                      required
                      className="w-full border-gray-300 rounded-md shadow-sm text-sm"
                      placeholder="svc_secret_..."
                    />
                  </div>
                </div>
                <div className="flex items-center space-x-4">
                  <button
                    type="submit"
                    disabled={testLoading}
                    className="px-4 py-2 bg-indigo-600 text-white rounded-md text-sm hover:bg-indigo-700 disabled:opacity-50"
                  >
                    {testLoading ? 'Testing...' : 'Test Authentication'}
                  </button>
                  {testResult && (
                    <span className={`text-sm ${testResult.startsWith('Success') ? 'text-green-600' : 'text-red-600'}`}>
                      {testResult}
                    </span>
                  )}
                </div>
              </form>
            </div>
          </>
        )}

        {activeTab === 'create' && (
          <div className="bg-white shadow rounded-lg p-6">
            <h2 className="text-lg font-semibold text-gray-900 mb-4">Create Service Account</h2>
            <form onSubmit={handleCreate} className="space-y-4">
              <div className="grid grid-cols-2 gap-4">
                <div>
                  <label className="block text-sm font-medium text-gray-700 mb-1">Name *</label>
                  <input
                    type="text"
                    value={newName}
                    onChange={e => setNewName(e.target.value)}
                    required
                    className="w-full border-gray-300 rounded-md shadow-sm text-sm"
                    placeholder="e.g. Payment Service"
                  />
                </div>
                <div>
                  <label className="block text-sm font-medium text-gray-700 mb-1">Type</label>
                  <select
                    value={newType}
                    onChange={e => setNewType(Number(e.target.value))}
                    className="w-full border-gray-300 rounded-md shadow-sm text-sm"
                  >
                    {SERVICE_ACCOUNT_TYPES.map(t => (
                      <option key={t.value} value={t.value}>{t.label}</option>
                    ))}
                  </select>
                </div>
              </div>

              <div className="grid grid-cols-2 gap-4">
                <div>
                  <label className="block text-sm font-medium text-gray-700 mb-1">Tenant (optional)</label>
                  <select
                    value={newTenantId}
                    onChange={e => setNewTenantId(e.target.value)}
                    className="w-full border-gray-300 rounded-md shadow-sm text-sm"
                  >
                    <option value="">No tenant (global)</option>
                    {tenants.map(t => (
                      <option key={t.id} value={t.id}>{t.name}</option>
                    ))}
                  </select>
                </div>
                <div>
                  <label className="block text-sm font-medium text-gray-700 mb-1">Certificate Thumbprint (mTLS)</label>
                  <input
                    type="text"
                    value={newCertThumbprint}
                    onChange={e => setNewCertThumbprint(e.target.value)}
                    className="w-full border-gray-300 rounded-md shadow-sm text-sm"
                    placeholder="Optional - for mTLS authentication"
                  />
                </div>
              </div>

              <div>
                <label className="block text-sm font-medium text-gray-700 mb-1">
                  Permissions (comma-separated)
                </label>
                <input
                  type="text"
                  value={newPermissions}
                  onChange={e => setNewPermissions(e.target.value)}
                  className="w-full border-gray-300 rounded-md shadow-sm text-sm"
                  placeholder="e.g. users.read, orders.write, payments.process"
                />
              </div>

              <div>
                <label className="block text-sm font-medium text-gray-700 mb-1">Description</label>
                <textarea
                  value={newDescription}
                  onChange={e => setNewDescription(e.target.value)}
                  rows={2}
                  className="w-full border-gray-300 rounded-md shadow-sm text-sm"
                  placeholder="What is this service account used for?"
                />
              </div>

              <div className="flex justify-end">
                <button
                  type="submit"
                  disabled={loading || !newName}
                  className="px-6 py-2 bg-indigo-600 text-white rounded-md text-sm hover:bg-indigo-700 disabled:opacity-50"
                >
                  {loading ? 'Creating...' : 'Create Service Account'}
                </button>
              </div>
            </form>
          </div>
        )}
      </div>
    </DashboardLayout>
  );
}
