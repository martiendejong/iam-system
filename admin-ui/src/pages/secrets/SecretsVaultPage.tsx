import { Fragment, useState, useEffect, useCallback } from 'react';
import DashboardLayout from '../../components/layout/DashboardLayout';
import { secretsApi } from '../../services/secretsApi';
import type { SecretEntry, SecretHistory } from '../../services/secretsApi';

const SECRET_TYPES = ['Generic', 'ApiKey', 'Certificate', 'DatabaseCredential', 'OAuthClientSecret'];

export default function SecretsVaultPage() {
  const [secrets, setSecrets] = useState<SecretEntry[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [showActiveOnly, setShowActiveOnly] = useState(true);

  const [showCreate, setShowCreate] = useState(false);
  const [creating, setCreating] = useState(false);
  const [createForm, setCreateForm] = useState({
    name: '',
    value: '',
    secretType: 'Generic',
    description: '',
  });

  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [history, setHistory] = useState<SecretHistory | null>(null);
  const [loadingHistory, setLoadingHistory] = useState(false);

  const [rotatingId, setRotatingId] = useState<string | null>(null);
  const [rotateValue, setRotateValue] = useState('');
  const [rotating, setRotating] = useState(false);

  const loadSecrets = useCallback(async () => {
    try {
      setLoading(true);
      setError('');
      const data = await secretsApi.getSecrets(showActiveOnly ? { isActive: true } : undefined);
      setSecrets(data);
    } catch (err: any) {
      setError(err.response?.data?.error || err.message || 'Failed to load secrets');
    } finally {
      setLoading(false);
    }
  }, [showActiveOnly]);

  useEffect(() => {
    loadSecrets();
  }, [loadSecrets]);

  const handleCreate = async () => {
    if (!createForm.name.trim() || !createForm.value.trim()) return;
    try {
      setCreating(true);
      setError('');
      await secretsApi.createSecret({
        name: createForm.name.trim(),
        value: createForm.value,
        secretType: createForm.secretType,
        description: createForm.description || undefined,
      });
      setCreateForm({ name: '', value: '', secretType: 'Generic', description: '' });
      setShowCreate(false);
      await loadSecrets();
    } catch (err: any) {
      setError(err.response?.data?.error || err.message || 'Failed to create secret');
    } finally {
      setCreating(false);
    }
  };

  const handleShowHistory = async (id: string) => {
    if (selectedId === id) {
      setSelectedId(null);
      setHistory(null);
      return;
    }
    try {
      setSelectedId(id);
      setLoadingHistory(true);
      const data = await secretsApi.getSecretHistory(id);
      setHistory(data);
    } catch (err: any) {
      setError(err.response?.data?.error || err.message || 'Failed to load secret history');
    } finally {
      setLoadingHistory(false);
    }
  };

  const handleRotate = async (id: string) => {
    if (!rotateValue.trim()) return;
    try {
      setRotating(true);
      setError('');
      await secretsApi.rotateSecret(id, { newValue: rotateValue, reason: 'Manual' });
      setRotatingId(null);
      setRotateValue('');
      await loadSecrets();
      if (selectedId === id) {
        const data = await secretsApi.getSecretHistory(id);
        setHistory(data);
      }
    } catch (err: any) {
      setError(err.response?.data?.error || err.message || 'Failed to rotate secret');
    } finally {
      setRotating(false);
    }
  };

  const handleDelete = async (id: string) => {
    if (!window.confirm('Deactivate this secret? It will no longer be usable.')) return;
    try {
      setError('');
      await secretsApi.deleteSecret(id);
      await loadSecrets();
    } catch (err: any) {
      setError(err.response?.data?.error || err.message || 'Failed to delete secret');
    }
  };

  return (
    <DashboardLayout>
      <div className="px-4 sm:px-6 lg:px-8">
        {/* Header */}
        <div className="sm:flex sm:items-center">
          <div className="sm:flex-auto">
            <h1 className="text-2xl font-semibold text-gray-900">Secrets Vault</h1>
            <p className="mt-2 text-sm text-gray-700">
              Store and rotate sensitive values (API keys, credentials, certificates) encrypted at rest.
              Values are never returned by the API once created.
            </p>
          </div>
          <div className="mt-4 sm:mt-0 sm:ml-16 sm:flex-none flex gap-2">
            <button
              onClick={loadSecrets}
              className="inline-flex items-center px-4 py-2 text-sm font-medium rounded-md border border-gray-300 bg-white text-gray-700 hover:bg-gray-50"
            >
              Refresh
            </button>
            <button
              onClick={() => setShowCreate((v) => !v)}
              className="inline-flex items-center px-4 py-2 text-sm font-medium rounded-md border border-transparent bg-indigo-600 text-white hover:bg-indigo-700"
            >
              New Secret
            </button>
          </div>
        </div>

        {/* Error */}
        {error && (
          <div className="mt-4 bg-red-50 border border-red-200 text-red-700 px-4 py-3 rounded">
            {typeof error === 'string' ? error : JSON.stringify(error)}
          </div>
        )}

        {/* Create form */}
        {showCreate && (
          <div className="mt-6 bg-white rounded-lg shadow p-6">
            <h3 className="text-lg font-medium text-gray-900 mb-4">New Secret</h3>
            <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
              <div>
                <label className="block text-sm font-medium text-gray-700 mb-1">Name</label>
                <input
                  type="text"
                  value={createForm.name}
                  onChange={(e) => setCreateForm((f) => ({ ...f, name: e.target.value }))}
                  className="block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                />
              </div>
              <div>
                <label className="block text-sm font-medium text-gray-700 mb-1">Type</label>
                <select
                  value={createForm.secretType}
                  onChange={(e) => setCreateForm((f) => ({ ...f, secretType: e.target.value }))}
                  className="block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                >
                  {SECRET_TYPES.map((t) => (
                    <option key={t} value={t}>{t}</option>
                  ))}
                </select>
              </div>
              <div className="md:col-span-2">
                <label className="block text-sm font-medium text-gray-700 mb-1">Value</label>
                <input
                  type="password"
                  value={createForm.value}
                  onChange={(e) => setCreateForm((f) => ({ ...f, value: e.target.value }))}
                  className="block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm font-mono"
                  autoComplete="new-password"
                />
              </div>
              <div className="md:col-span-2">
                <label className="block text-sm font-medium text-gray-700 mb-1">Description (optional)</label>
                <input
                  type="text"
                  value={createForm.description}
                  onChange={(e) => setCreateForm((f) => ({ ...f, description: e.target.value }))}
                  className="block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                />
              </div>
            </div>
            <div className="mt-6 flex justify-end gap-2">
              <button
                onClick={() => setShowCreate(false)}
                className="inline-flex items-center px-4 py-2 text-sm font-medium rounded-md border border-gray-300 bg-white text-gray-700 hover:bg-gray-50"
              >
                Cancel
              </button>
              <button
                onClick={handleCreate}
                disabled={creating || !createForm.name.trim() || !createForm.value.trim()}
                className="inline-flex items-center px-4 py-2 text-sm font-medium rounded-md border border-transparent bg-indigo-600 text-white hover:bg-indigo-700 disabled:opacity-50 disabled:cursor-not-allowed"
              >
                {creating ? 'Creating...' : 'Create Secret'}
              </button>
            </div>
          </div>
        )}

        {/* Filter */}
        <div className="mt-6 flex items-center gap-2">
          <label className="flex items-center gap-2 text-sm font-medium text-gray-700">
            <input
              type="checkbox"
              checked={showActiveOnly}
              onChange={(e) => setShowActiveOnly(e.target.checked)}
              className="rounded border-gray-300 text-indigo-600 focus:ring-indigo-500"
            />
            Active only
          </label>
        </div>

        {/* Loading */}
        {loading ? (
          <div className="mt-8 text-center">
            <div className="inline-block animate-spin rounded-full h-8 w-8 border-b-2 border-indigo-600" />
            <p className="mt-2 text-sm text-gray-500">Loading secrets...</p>
          </div>
        ) : (
          <div className="mt-6 bg-white rounded-lg shadow overflow-hidden">
            <div className="overflow-x-auto">
              <table className="min-w-full divide-y divide-gray-200">
                <thead className="bg-gray-50">
                  <tr>
                    <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase">Name</th>
                    <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase">Type</th>
                    <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase">Version</th>
                    <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase">Status</th>
                    <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase">Last Rotated</th>
                    <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase">Next Rotation</th>
                    <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase">Actions</th>
                  </tr>
                </thead>
                <tbody className="bg-white divide-y divide-gray-200">
                  {secrets.length === 0 ? (
                    <tr>
                      <td colSpan={7} className="px-6 py-8 text-center text-sm text-gray-500">
                        No secrets found.
                      </td>
                    </tr>
                  ) : (
                    secrets.map((secret) => (
                      <Fragment key={secret.id}>
                        <tr className="hover:bg-gray-50">
                          <td className="px-6 py-3 text-sm font-medium text-gray-900">{secret.name}</td>
                          <td className="px-6 py-3 text-sm text-gray-500">{secret.secretType}</td>
                          <td className="px-6 py-3 text-sm text-gray-500">v{secret.version}</td>
                          <td className="px-6 py-3 whitespace-nowrap">
                            <span
                              className={`inline-flex items-center px-2.5 py-0.5 rounded-full text-xs font-semibold ${
                                secret.isActive ? 'bg-green-100 text-green-800' : 'bg-gray-100 text-gray-600'
                              }`}
                            >
                              {secret.isActive ? 'Active' : 'Inactive'}
                            </span>
                          </td>
                          <td className="px-6 py-3 text-sm text-gray-500 whitespace-nowrap">
                            {secret.lastRotatedAt ? new Date(secret.lastRotatedAt).toLocaleString() : '-'}
                          </td>
                          <td className="px-6 py-3 text-sm text-gray-500 whitespace-nowrap">
                            {secret.nextRotationAt ? new Date(secret.nextRotationAt).toLocaleString() : '-'}
                          </td>
                          <td className="px-6 py-3 text-sm whitespace-nowrap">
                            <div className="flex gap-3">
                              <button
                                onClick={() => setRotatingId(rotatingId === secret.id ? null : secret.id)}
                                className="text-indigo-600 hover:text-indigo-800 font-medium"
                              >
                                Rotate
                              </button>
                              <button
                                onClick={() => handleShowHistory(secret.id)}
                                className="text-gray-600 hover:text-gray-800 font-medium"
                              >
                                {selectedId === secret.id ? 'Hide History' : 'History'}
                              </button>
                              <button
                                onClick={() => handleDelete(secret.id)}
                                className="text-red-600 hover:text-red-800 font-medium"
                              >
                                Delete
                              </button>
                            </div>
                          </td>
                        </tr>
                        {rotatingId === secret.id && (
                          <tr>
                            <td colSpan={7} className="px-6 py-4 bg-indigo-50">
                              <div className="flex items-end gap-3">
                                <div className="flex-1 max-w-md">
                                  <label className="block text-sm font-medium text-gray-700 mb-1">New Value</label>
                                  <input
                                    type="password"
                                    value={rotateValue}
                                    onChange={(e) => setRotateValue(e.target.value)}
                                    className="block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm font-mono"
                                    autoComplete="new-password"
                                  />
                                </div>
                                <button
                                  onClick={() => handleRotate(secret.id)}
                                  disabled={rotating || !rotateValue.trim()}
                                  className="inline-flex items-center px-4 py-2 text-sm font-medium rounded-md border border-transparent bg-indigo-600 text-white hover:bg-indigo-700 disabled:opacity-50"
                                >
                                  {rotating ? 'Rotating...' : 'Confirm Rotate'}
                                </button>
                                <button
                                  onClick={() => { setRotatingId(null); setRotateValue(''); }}
                                  className="inline-flex items-center px-4 py-2 text-sm font-medium rounded-md border border-gray-300 bg-white text-gray-700 hover:bg-gray-50"
                                >
                                  Cancel
                                </button>
                              </div>
                            </td>
                          </tr>
                        )}
                        {selectedId === secret.id && (
                          <tr>
                            <td colSpan={7} className="px-6 py-4 bg-gray-50">
                              {loadingHistory ? (
                                <p className="text-sm text-gray-500">Loading history...</p>
                              ) : history && history.history.length > 0 ? (
                                <table className="min-w-full divide-y divide-gray-200">
                                  <thead>
                                    <tr>
                                      <th className="px-3 py-1 text-left text-xs font-medium text-gray-500 uppercase">Version</th>
                                      <th className="px-3 py-1 text-left text-xs font-medium text-gray-500 uppercase">Reason</th>
                                      <th className="px-3 py-1 text-left text-xs font-medium text-gray-500 uppercase">Rotated At</th>
                                      <th className="px-3 py-1 text-left text-xs font-medium text-gray-500 uppercase">Revoked</th>
                                    </tr>
                                  </thead>
                                  <tbody className="divide-y divide-gray-200">
                                    {history.history.map((h) => (
                                      <tr key={h.id}>
                                        <td className="px-3 py-1 text-sm text-gray-900">v{h.version}</td>
                                        <td className="px-3 py-1 text-sm text-gray-500">{h.rotationReason}</td>
                                        <td className="px-3 py-1 text-sm text-gray-500">{new Date(h.createdAt).toLocaleString()}</td>
                                        <td className="px-3 py-1 text-sm text-gray-500">{h.isRevoked ? 'Yes' : 'No'}</td>
                                      </tr>
                                    ))}
                                  </tbody>
                                </table>
                              ) : (
                                <p className="text-sm text-gray-500">No rotation history yet.</p>
                              )}
                            </td>
                          </tr>
                        )}
                      </Fragment>
                    ))
                  )}
                </tbody>
              </table>
            </div>
          </div>
        )}
      </div>
    </DashboardLayout>
  );
}
