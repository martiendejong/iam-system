import { useState, useEffect } from 'react';
import DashboardLayout from '../../components/layout/DashboardLayout';
import { api } from '../../services/api';

interface DirectorySyncConfig {
  id: string;
  tenantId: string;
  tenantName?: string;
  name: string;
  ldapUrl: string;
  bindDn: string;
  searchBase: string;
  searchFilter: string;
  syncInterval: number;
  attributeMapping: Record<string, string> | null;
  groupToRoleMapping: Record<string, string> | null;
  isActive: boolean;
  lastSyncAt: string | null;
  lastSyncStatus: string | null;
  createdAt: string;
  updatedAt: string;
}

interface SyncLog {
  id: string;
  configId: string;
  syncType: string;
  status: string;
  usersCreated: number;
  usersUpdated: number;
  usersDisabled: number;
  groupsSynced: number;
  errors: string[] | null;
  startedAt: string;
  completedAt: string | null;
  duration: number | null;
}

interface TestResult {
  success: boolean;
  message: string;
  userCount?: number;
  serverType?: string;
}

type ModalMode = 'create' | 'edit' | 'logs' | 'test' | null;

const defaultAttributeMapping: Record<string, string> = {
  email: 'mail',
  firstName: 'givenName',
  lastName: 'sn',
  phoneNumber: 'telephoneNumber',
};

export default function DirectorySyncPage() {
  const [configs, setConfigs] = useState<DirectorySyncConfig[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [tenantId, setTenantId] = useState('');
  const [tenants, setTenants] = useState<{ id: string; name: string }[]>([]);

  // Modal state
  const [modalMode, setModalMode] = useState<ModalMode>(null);
  const [selectedConfig, setSelectedConfig] = useState<DirectorySyncConfig | null>(null);

  // Form state
  const [formData, setFormData] = useState({
    name: '',
    ldapUrl: '',
    bindDn: '',
    bindPassword: '',
    searchBase: '',
    searchFilter: '(objectClass=person)',
    syncInterval: 60,
    isActive: true,
    attributeMapping: { ...defaultAttributeMapping },
    groupToRoleMapping: {} as Record<string, string>,
  });

  // Sync logs
  const [syncLogs, setSyncLogs] = useState<SyncLog[]>([]);
  const [logsLoading, setLogsLoading] = useState(false);

  // Test connection
  const [testResult, setTestResult] = useState<TestResult | null>(null);
  const [testLoading, setTestLoading] = useState(false);

  // Syncing state
  const [syncing, setSyncing] = useState<string | null>(null);

  useEffect(() => {
    loadTenants();
  }, []);

  useEffect(() => {
    if (tenantId) {
      loadConfigs();
    }
  }, [tenantId]);

  const loadTenants = async () => {
    try {
      const data = await api.getTenants();
      setTenants(data);
      if (data.length > 0) {
        setTenantId(data[0].id);
      }
    } catch (err: any) {
      setError('Failed to load tenants');
    }
  };

  const loadConfigs = async () => {
    try {
      setLoading(true);
      const client = api.getClient();
      const response = await client.get('/directory-sync/configs', { params: { tenantId } });
      setConfigs(response.data);
    } catch (err: any) {
      setError(err.response?.data?.error || 'Failed to load directory sync configs');
    } finally {
      setLoading(false);
    }
  };

  const openCreateModal = () => {
    setFormData({
      name: '',
      ldapUrl: '',
      bindDn: '',
      bindPassword: '',
      searchBase: '',
      searchFilter: '(objectClass=person)',
      syncInterval: 60,
      isActive: true,
      attributeMapping: { ...defaultAttributeMapping },
      groupToRoleMapping: {},
    });
    setSelectedConfig(null);
    setModalMode('create');
  };

  const openEditModal = (config: DirectorySyncConfig) => {
    setFormData({
      name: config.name,
      ldapUrl: config.ldapUrl,
      bindDn: config.bindDn,
      bindPassword: '',
      searchBase: config.searchBase,
      searchFilter: config.searchFilter,
      syncInterval: config.syncInterval,
      isActive: config.isActive,
      attributeMapping: config.attributeMapping || { ...defaultAttributeMapping },
      groupToRoleMapping: config.groupToRoleMapping || {},
    });
    setSelectedConfig(config);
    setModalMode('edit');
  };

  const openLogsModal = async (config: DirectorySyncConfig) => {
    setSelectedConfig(config);
    setModalMode('logs');
    setLogsLoading(true);
    try {
      const client = api.getClient();
      const response = await client.get(`/directory-sync/configs/${config.id}/logs`);
      setSyncLogs(response.data);
    } catch {
      setSyncLogs([]);
    } finally {
      setLogsLoading(false);
    }
  };

  const handleSave = async () => {
    try {
      const client = api.getClient();
      if (modalMode === 'create') {
        await client.post('/directory-sync/configs', {
          tenantId,
          ...formData,
        });
      } else if (modalMode === 'edit' && selectedConfig) {
        await client.put(`/directory-sync/configs/${selectedConfig.id}`, formData);
      }
      setModalMode(null);
      await loadConfigs();
    } catch (err: any) {
      setError(err.response?.data?.error || 'Failed to save configuration');
    }
  };

  const handleDelete = async (config: DirectorySyncConfig) => {
    if (!confirm(`Are you sure you want to delete "${config.name}"? This will also delete all sync logs.`)) {
      return;
    }
    try {
      const client = api.getClient();
      await client.delete(`/directory-sync/configs/${config.id}`);
      await loadConfigs();
    } catch (err: any) {
      alert(err.response?.data?.error || 'Failed to delete configuration');
    }
  };

  const handleTestConnection = async (config: DirectorySyncConfig) => {
    setSelectedConfig(config);
    setModalMode('test');
    setTestResult(null);
    setTestLoading(true);
    try {
      const client = api.getClient();
      const response = await client.post(`/directory-sync/configs/${config.id}/test-connection`);
      setTestResult(response.data);
    } catch (err: any) {
      setTestResult({ success: false, message: err.response?.data?.error || 'Connection test failed' });
    } finally {
      setTestLoading(false);
    }
  };

  const handleTriggerSync = async (config: DirectorySyncConfig, type: 'full' | 'delta') => {
    setSyncing(config.id);
    try {
      const client = api.getClient();
      const response = await client.post(`/directory-sync/configs/${config.id}/sync?type=${type}`);
      const result = response.data;
      alert(
        `Sync completed!\n` +
        `Status: ${result.status}\n` +
        `Users created: ${result.usersCreated}\n` +
        `Users updated: ${result.usersUpdated}\n` +
        `Users disabled: ${result.usersDisabled}\n` +
        `Groups synced: ${result.groupsSynced}`
      );
      await loadConfigs();
    } catch (err: any) {
      alert(err.response?.data?.error || 'Sync failed');
    } finally {
      setSyncing(null);
    }
  };

  const updateAttrMapping = (key: string, value: string) => {
    setFormData((prev) => ({
      ...prev,
      attributeMapping: { ...prev.attributeMapping, [key]: value },
    }));
  };

  const removeAttrMapping = (key: string) => {
    setFormData((prev) => {
      const newMapping = { ...prev.attributeMapping };
      delete newMapping[key];
      return { ...prev, attributeMapping: newMapping };
    });
  };

  const [newAttrKey, setNewAttrKey] = useState('');
  const [newAttrValue, setNewAttrValue] = useState('');

  const addAttrMapping = () => {
    if (newAttrKey && newAttrValue) {
      updateAttrMapping(newAttrKey, newAttrValue);
      setNewAttrKey('');
      setNewAttrValue('');
    }
  };

  const statusColors: Record<string, string> = {
    Success: 'bg-green-100 text-green-800',
    Failed: 'bg-red-100 text-red-800',
    PartialSuccess: 'bg-yellow-100 text-yellow-800',
    Running: 'bg-blue-100 text-blue-800',
  };

  return (
    <DashboardLayout>
      <div className="px-4 sm:px-6 lg:px-8">
        {/* Header */}
        <div className="sm:flex sm:items-center">
          <div className="sm:flex-auto">
            <h1 className="text-2xl font-semibold text-gray-900">Directory Sync</h1>
            <p className="mt-2 text-sm text-gray-700">
              Synchronize users and groups from LDAP/Active Directory into IAM.
            </p>
          </div>
          <div className="mt-4 sm:mt-0 sm:ml-16 sm:flex-none">
            <button
              onClick={openCreateModal}
              disabled={!tenantId}
              className="inline-flex items-center justify-center rounded-md border border-transparent bg-indigo-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-indigo-700 focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:ring-offset-2 disabled:opacity-50 disabled:cursor-not-allowed"
            >
              Add Directory Connection
            </button>
          </div>
        </div>

        {/* Tenant Selector */}
        <div className="mt-6">
          <label className="block text-sm font-medium text-gray-700">Tenant</label>
          <select
            value={tenantId}
            onChange={(e) => setTenantId(e.target.value)}
            className="mt-1 block w-full sm:w-64 rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
          >
            <option value="">Select a tenant...</option>
            {tenants.map((t) => (
              <option key={t.id} value={t.id}>
                {t.name}
              </option>
            ))}
          </select>
        </div>

        {/* Error */}
        {error && (
          <div className="mt-4 bg-red-50 border border-red-200 text-red-700 px-4 py-3 rounded">
            {error}
            <button onClick={() => setError('')} className="ml-4 text-red-500 underline text-sm">
              dismiss
            </button>
          </div>
        )}

        {/* Loading */}
        {loading ? (
          <div className="mt-8 text-center">
            <div className="inline-block animate-spin rounded-full h-8 w-8 border-b-2 border-indigo-600"></div>
            <p className="mt-2 text-sm text-gray-500">Loading configurations...</p>
          </div>
        ) : (
          /* Config Cards */
          <div className="mt-8 space-y-4">
            {configs.length === 0 ? (
              <div className="text-center py-12 text-gray-500 bg-white rounded-lg border border-gray-200">
                No directory sync configurations found for this tenant.
              </div>
            ) : (
              configs.map((config) => (
                <div
                  key={config.id}
                  className="bg-white rounded-lg border border-gray-200 p-6 shadow-sm"
                >
                  <div className="flex items-start justify-between">
                    <div className="flex-1">
                      <div className="flex items-center gap-3">
                        <h3 className="text-lg font-medium text-gray-900">{config.name}</h3>
                        <span
                          className={`inline-flex items-center px-2 py-0.5 rounded text-xs font-medium ${
                            config.isActive
                              ? 'bg-green-100 text-green-800'
                              : 'bg-gray-100 text-gray-800'
                          }`}
                        >
                          {config.isActive ? 'Active' : 'Inactive'}
                        </span>
                        {config.lastSyncStatus && (
                          <span
                            className={`inline-flex items-center px-2 py-0.5 rounded text-xs font-medium ${
                              statusColors[config.lastSyncStatus] || 'bg-gray-100 text-gray-800'
                            }`}
                          >
                            Last: {config.lastSyncStatus}
                          </span>
                        )}
                      </div>
                      <div className="mt-2 grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-4 gap-2 text-sm text-gray-500">
                        <div>
                          <span className="font-medium text-gray-700">URL:</span> {config.ldapUrl}
                        </div>
                        <div>
                          <span className="font-medium text-gray-700">Base:</span> {config.searchBase}
                        </div>
                        <div>
                          <span className="font-medium text-gray-700">Interval:</span>{' '}
                          {config.syncInterval > 0 ? `${config.syncInterval} min` : 'Manual only'}
                        </div>
                        <div>
                          <span className="font-medium text-gray-700">Last sync:</span>{' '}
                          {config.lastSyncAt
                            ? new Date(config.lastSyncAt).toLocaleString()
                            : 'Never'}
                        </div>
                      </div>
                    </div>
                  </div>

                  {/* Actions */}
                  <div className="mt-4 flex flex-wrap gap-2">
                    <button
                      onClick={() => handleTriggerSync(config, 'full')}
                      disabled={syncing === config.id}
                      className="px-3 py-1.5 border border-indigo-300 rounded-md text-sm font-medium text-indigo-700 bg-white hover:bg-indigo-50 disabled:opacity-50"
                    >
                      {syncing === config.id ? 'Syncing...' : 'Full Sync'}
                    </button>
                    <button
                      onClick={() => handleTriggerSync(config, 'delta')}
                      disabled={syncing === config.id || !config.lastSyncAt}
                      className="px-3 py-1.5 border border-indigo-300 rounded-md text-sm font-medium text-indigo-700 bg-white hover:bg-indigo-50 disabled:opacity-50"
                    >
                      Delta Sync
                    </button>
                    <button
                      onClick={() => handleTestConnection(config)}
                      className="px-3 py-1.5 border border-green-300 rounded-md text-sm font-medium text-green-700 bg-white hover:bg-green-50"
                    >
                      Test Connection
                    </button>
                    <button
                      onClick={() => openLogsModal(config)}
                      className="px-3 py-1.5 border border-gray-300 rounded-md text-sm font-medium text-gray-700 bg-white hover:bg-gray-50"
                    >
                      View Logs
                    </button>
                    <button
                      onClick={() => openEditModal(config)}
                      className="px-3 py-1.5 border border-gray-300 rounded-md text-sm font-medium text-gray-700 bg-white hover:bg-gray-50"
                    >
                      Edit
                    </button>
                    <button
                      onClick={() => handleDelete(config)}
                      className="px-3 py-1.5 border border-red-300 rounded-md text-sm font-medium text-red-700 bg-white hover:bg-red-50"
                    >
                      Delete
                    </button>
                  </div>
                </div>
              ))
            )}
          </div>
        )}

        {/* Modal: Create/Edit Config */}
        {(modalMode === 'create' || modalMode === 'edit') && (
          <div className="fixed inset-0 z-50 overflow-y-auto">
            <div className="flex items-center justify-center min-h-screen px-4">
              <div className="fixed inset-0 bg-gray-500 bg-opacity-75" onClick={() => setModalMode(null)} />
              <div className="relative bg-white rounded-lg shadow-xl max-w-2xl w-full max-h-[90vh] overflow-y-auto p-6">
                <h2 className="text-xl font-semibold text-gray-900 mb-4">
                  {modalMode === 'create' ? 'Add Directory Connection' : 'Edit Directory Connection'}
                </h2>

                <div className="space-y-4">
                  {/* Connection Settings */}
                  <div>
                    <h3 className="text-sm font-medium text-gray-700 mb-2">Connection Settings</h3>
                    <div className="grid grid-cols-1 gap-3">
                      <div>
                        <label className="block text-sm text-gray-600">Name</label>
                        <input
                          type="text"
                          value={formData.name}
                          onChange={(e) => setFormData({ ...formData, name: e.target.value })}
                          placeholder="Corporate Active Directory"
                          className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                        />
                      </div>
                      <div>
                        <label className="block text-sm text-gray-600">LDAP URL</label>
                        <input
                          type="text"
                          value={formData.ldapUrl}
                          onChange={(e) => setFormData({ ...formData, ldapUrl: e.target.value })}
                          placeholder="ldap://dc.example.com:389 or ldaps://dc.example.com:636"
                          className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                        />
                      </div>
                      <div className="grid grid-cols-2 gap-3">
                        <div>
                          <label className="block text-sm text-gray-600">Bind DN</label>
                          <input
                            type="text"
                            value={formData.bindDn}
                            onChange={(e) => setFormData({ ...formData, bindDn: e.target.value })}
                            placeholder="cn=admin,dc=example,dc=com"
                            className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                          />
                        </div>
                        <div>
                          <label className="block text-sm text-gray-600">Bind Password</label>
                          <input
                            type="password"
                            value={formData.bindPassword}
                            onChange={(e) => setFormData({ ...formData, bindPassword: e.target.value })}
                            placeholder={modalMode === 'edit' ? '(leave blank to keep current)' : ''}
                            className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                          />
                        </div>
                      </div>
                      <div>
                        <label className="block text-sm text-gray-600">Search Base DN</label>
                        <input
                          type="text"
                          value={formData.searchBase}
                          onChange={(e) => setFormData({ ...formData, searchBase: e.target.value })}
                          placeholder="ou=users,dc=example,dc=com"
                          className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                        />
                      </div>
                      <div className="grid grid-cols-2 gap-3">
                        <div>
                          <label className="block text-sm text-gray-600">Search Filter</label>
                          <input
                            type="text"
                            value={formData.searchFilter}
                            onChange={(e) => setFormData({ ...formData, searchFilter: e.target.value })}
                            className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                          />
                        </div>
                        <div>
                          <label className="block text-sm text-gray-600">
                            Sync Interval (minutes, 0 = manual)
                          </label>
                          <input
                            type="number"
                            min="0"
                            value={formData.syncInterval}
                            onChange={(e) =>
                              setFormData({ ...formData, syncInterval: parseInt(e.target.value) || 0 })
                            }
                            className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                          />
                        </div>
                      </div>
                      <div className="flex items-center">
                        <input
                          type="checkbox"
                          checked={formData.isActive}
                          onChange={(e) => setFormData({ ...formData, isActive: e.target.checked })}
                          className="h-4 w-4 text-indigo-600 border-gray-300 rounded focus:ring-indigo-500"
                        />
                        <label className="ml-2 block text-sm text-gray-700">Active</label>
                      </div>
                    </div>
                  </div>

                  {/* Attribute Mapping */}
                  <div>
                    <h3 className="text-sm font-medium text-gray-700 mb-2">
                      Attribute Mapping (IAM Field → LDAP Attribute)
                    </h3>
                    <div className="space-y-2">
                      {Object.entries(formData.attributeMapping).map(([key, value]) => (
                        <div key={key} className="flex items-center gap-2">
                          <span className="w-32 text-sm font-medium text-gray-600">{key}</span>
                          <input
                            type="text"
                            value={value}
                            onChange={(e) => updateAttrMapping(key, e.target.value)}
                            className="flex-1 rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                          />
                          <button
                            onClick={() => removeAttrMapping(key)}
                            className="text-red-500 hover:text-red-700 text-sm"
                          >
                            Remove
                          </button>
                        </div>
                      ))}
                      <div className="flex items-center gap-2 mt-2">
                        <input
                          type="text"
                          value={newAttrKey}
                          onChange={(e) => setNewAttrKey(e.target.value)}
                          placeholder="IAM field"
                          className="w-32 rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                        />
                        <input
                          type="text"
                          value={newAttrValue}
                          onChange={(e) => setNewAttrValue(e.target.value)}
                          placeholder="LDAP attribute"
                          className="flex-1 rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                        />
                        <button
                          onClick={addAttrMapping}
                          className="px-3 py-1.5 border border-gray-300 rounded-md text-sm font-medium text-gray-700 bg-white hover:bg-gray-50"
                        >
                          Add
                        </button>
                      </div>
                    </div>
                  </div>
                </div>

                {/* Modal Actions */}
                <div className="mt-6 flex justify-end gap-3">
                  <button
                    onClick={() => setModalMode(null)}
                    className="px-4 py-2 border border-gray-300 rounded-md text-sm font-medium text-gray-700 bg-white hover:bg-gray-50"
                  >
                    Cancel
                  </button>
                  <button
                    onClick={handleSave}
                    className="px-4 py-2 border border-transparent rounded-md text-sm font-medium text-white bg-indigo-600 hover:bg-indigo-700"
                  >
                    {modalMode === 'create' ? 'Create' : 'Save Changes'}
                  </button>
                </div>
              </div>
            </div>
          </div>
        )}

        {/* Modal: Test Connection */}
        {modalMode === 'test' && (
          <div className="fixed inset-0 z-50 overflow-y-auto">
            <div className="flex items-center justify-center min-h-screen px-4">
              <div className="fixed inset-0 bg-gray-500 bg-opacity-75" onClick={() => setModalMode(null)} />
              <div className="relative bg-white rounded-lg shadow-xl max-w-md w-full p-6">
                <h2 className="text-xl font-semibold text-gray-900 mb-4">
                  Connection Test: {selectedConfig?.name}
                </h2>
                {testLoading ? (
                  <div className="text-center py-8">
                    <div className="inline-block animate-spin rounded-full h-8 w-8 border-b-2 border-indigo-600"></div>
                    <p className="mt-2 text-sm text-gray-500">Testing connection...</p>
                  </div>
                ) : testResult ? (
                  <div>
                    <div
                      className={`p-4 rounded-md ${
                        testResult.success ? 'bg-green-50 border border-green-200' : 'bg-red-50 border border-red-200'
                      }`}
                    >
                      <p
                        className={`text-sm font-medium ${
                          testResult.success ? 'text-green-800' : 'text-red-800'
                        }`}
                      >
                        {testResult.success ? 'Connection Successful' : 'Connection Failed'}
                      </p>
                      <p className={`mt-1 text-sm ${testResult.success ? 'text-green-700' : 'text-red-700'}`}>
                        {testResult.message}
                      </p>
                      {testResult.serverType && (
                        <p className="mt-1 text-sm text-gray-600">
                          Server type: {testResult.serverType}
                        </p>
                      )}
                    </div>
                  </div>
                ) : null}
                <div className="mt-4 flex justify-end">
                  <button
                    onClick={() => setModalMode(null)}
                    className="px-4 py-2 border border-gray-300 rounded-md text-sm font-medium text-gray-700 bg-white hover:bg-gray-50"
                  >
                    Close
                  </button>
                </div>
              </div>
            </div>
          </div>
        )}

        {/* Modal: Sync Logs */}
        {modalMode === 'logs' && (
          <div className="fixed inset-0 z-50 overflow-y-auto">
            <div className="flex items-center justify-center min-h-screen px-4">
              <div className="fixed inset-0 bg-gray-500 bg-opacity-75" onClick={() => setModalMode(null)} />
              <div className="relative bg-white rounded-lg shadow-xl max-w-4xl w-full max-h-[90vh] overflow-y-auto p-6">
                <h2 className="text-xl font-semibold text-gray-900 mb-4">
                  Sync Logs: {selectedConfig?.name}
                </h2>
                {logsLoading ? (
                  <div className="text-center py-8">
                    <div className="inline-block animate-spin rounded-full h-8 w-8 border-b-2 border-indigo-600"></div>
                    <p className="mt-2 text-sm text-gray-500">Loading logs...</p>
                  </div>
                ) : syncLogs.length === 0 ? (
                  <p className="text-center py-8 text-gray-500">No sync logs found.</p>
                ) : (
                  <div className="overflow-x-auto">
                    <table className="min-w-full divide-y divide-gray-300">
                      <thead>
                        <tr>
                          <th className="px-3 py-2 text-left text-xs font-medium text-gray-500 uppercase">
                            Started
                          </th>
                          <th className="px-3 py-2 text-left text-xs font-medium text-gray-500 uppercase">
                            Type
                          </th>
                          <th className="px-3 py-2 text-left text-xs font-medium text-gray-500 uppercase">
                            Status
                          </th>
                          <th className="px-3 py-2 text-right text-xs font-medium text-gray-500 uppercase">
                            Created
                          </th>
                          <th className="px-3 py-2 text-right text-xs font-medium text-gray-500 uppercase">
                            Updated
                          </th>
                          <th className="px-3 py-2 text-right text-xs font-medium text-gray-500 uppercase">
                            Disabled
                          </th>
                          <th className="px-3 py-2 text-right text-xs font-medium text-gray-500 uppercase">
                            Groups
                          </th>
                          <th className="px-3 py-2 text-right text-xs font-medium text-gray-500 uppercase">
                            Duration
                          </th>
                          <th className="px-3 py-2 text-left text-xs font-medium text-gray-500 uppercase">
                            Errors
                          </th>
                        </tr>
                      </thead>
                      <tbody className="divide-y divide-gray-200">
                        {syncLogs.map((log) => (
                          <tr key={log.id}>
                            <td className="px-3 py-2 text-sm text-gray-900 whitespace-nowrap">
                              {new Date(log.startedAt).toLocaleString()}
                            </td>
                            <td className="px-3 py-2 text-sm text-gray-600">{log.syncType}</td>
                            <td className="px-3 py-2">
                              <span
                                className={`inline-flex items-center px-2 py-0.5 rounded text-xs font-medium ${
                                  statusColors[log.status] || 'bg-gray-100 text-gray-800'
                                }`}
                              >
                                {log.status}
                              </span>
                            </td>
                            <td className="px-3 py-2 text-sm text-gray-600 text-right">
                              {log.usersCreated}
                            </td>
                            <td className="px-3 py-2 text-sm text-gray-600 text-right">
                              {log.usersUpdated}
                            </td>
                            <td className="px-3 py-2 text-sm text-gray-600 text-right">
                              {log.usersDisabled}
                            </td>
                            <td className="px-3 py-2 text-sm text-gray-600 text-right">
                              {log.groupsSynced}
                            </td>
                            <td className="px-3 py-2 text-sm text-gray-600 text-right whitespace-nowrap">
                              {log.duration != null ? `${log.duration.toFixed(1)}s` : '-'}
                            </td>
                            <td className="px-3 py-2 text-sm text-red-600 max-w-xs truncate">
                              {log.errors && Array.isArray(log.errors) ? log.errors.length : 0} errors
                            </td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  </div>
                )}
                <div className="mt-4 flex justify-end">
                  <button
                    onClick={() => setModalMode(null)}
                    className="px-4 py-2 border border-gray-300 rounded-md text-sm font-medium text-gray-700 bg-white hover:bg-gray-50"
                  >
                    Close
                  </button>
                </div>
              </div>
            </div>
          </div>
        )}
      </div>
    </DashboardLayout>
  );
}
