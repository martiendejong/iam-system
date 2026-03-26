import { useState, useEffect } from 'react';
import PortalLayout from '../../components/layout/PortalLayout';
import { portalApi } from '../../services/portalApi';
import type { PasskeyCredential } from '../../services/portalApi';

export default function PortalPasskeysPage() {
  const [passkeys, setPasskeys] = useState<PasskeyCredential[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [success, setSuccess] = useState('');
  const [deletingId, setDeletingId] = useState<string | null>(null);

  useEffect(() => {
    loadPasskeys();
  }, []);

  const loadPasskeys = async () => {
    try {
      setLoading(true);
      setError('');
      const data = await portalApi.getPasskeys();
      setPasskeys(data);
    } catch (err: any) {
      setError(err.response?.data?.error || 'Failed to load passkeys');
    } finally {
      setLoading(false);
    }
  };

  const handleDelete = async (credentialId: string, name?: string) => {
    if (!confirm(`Delete passkey "${name || 'Unnamed'}"? This cannot be undone.`)) return;

    try {
      setDeletingId(credentialId);
      setError('');
      await portalApi.deletePasskey(credentialId);
      setSuccess('Passkey deleted successfully');
      setTimeout(() => setSuccess(''), 3000);
      await loadPasskeys();
    } catch (err: any) {
      setError(err.response?.data?.error || 'Failed to delete passkey');
    } finally {
      setDeletingId(null);
    }
  };

  const handleRegister = () => {
    // WebAuthn registration is complex and uses the existing passkey controller endpoints.
    // For a full implementation, this would invoke the WebAuthn browser API.
    setError('Passkey registration requires WebAuthn browser API integration. Use the dedicated registration flow.');
  };

  return (
    <PortalLayout>
      <div className="bg-white rounded-lg shadow-sm">
        <div className="px-6 py-4 border-b border-gray-200 flex justify-between items-center">
          <div>
            <h2 className="text-lg font-semibold text-gray-900">Passkeys</h2>
            <p className="mt-1 text-sm text-gray-500">
              Manage passkeys registered to your account for passwordless sign-in.
            </p>
          </div>
          <button
            onClick={handleRegister}
            className="inline-flex items-center px-4 py-2 border border-transparent text-sm font-medium rounded-md text-white bg-indigo-600 hover:bg-indigo-700"
          >
            Register New Passkey
          </button>
        </div>

        <div className="px-6 py-4">
          {error && (
            <div className="mb-4 bg-red-50 border border-red-200 text-red-700 px-4 py-3 rounded text-sm">
              {error}
            </div>
          )}
          {success && (
            <div className="mb-4 bg-green-50 border border-green-200 text-green-700 px-4 py-3 rounded text-sm">
              {success}
            </div>
          )}

          {loading ? (
            <div className="text-center py-8">
              <div className="inline-block animate-spin rounded-full h-8 w-8 border-b-2 border-indigo-600"></div>
              <p className="mt-2 text-sm text-gray-500">Loading passkeys...</p>
            </div>
          ) : passkeys.length === 0 ? (
            <div className="text-center py-8">
              <div className="mx-auto h-12 w-12 text-gray-400">
                <svg fill="none" viewBox="0 0 24 24" strokeWidth={1.5} stroke="currentColor">
                  <path strokeLinecap="round" strokeLinejoin="round" d="M15.75 5.25a3 3 0 013 3m3 0a6 6 0 01-7.029 5.912c-.563-.097-1.159.026-1.563.43L10.5 17.25H8.25v2.25H6v2.25H2.25v-2.818c0-.597.237-1.17.659-1.591l6.499-6.499c.404-.404.527-1 .43-1.563A6 6 0 1121.75 8.25z" />
                </svg>
              </div>
              <h3 className="mt-2 text-sm font-medium text-gray-900">No passkeys registered</h3>
              <p className="mt-1 text-sm text-gray-500">
                Register a passkey to enable passwordless sign-in.
              </p>
            </div>
          ) : (
            <div className="space-y-3">
              {passkeys.map((passkey) => (
                <div
                  key={passkey.id}
                  className="border border-gray-200 rounded-lg p-4 flex justify-between items-start"
                >
                  <div className="flex gap-3">
                    <div className="mt-0.5">
                      <div className="h-10 w-10 rounded-full bg-indigo-100 flex items-center justify-center">
                        <svg className="h-5 w-5 text-indigo-600" fill="none" viewBox="0 0 24 24" strokeWidth={1.5} stroke="currentColor">
                          <path strokeLinecap="round" strokeLinejoin="round" d="M15.75 5.25a3 3 0 013 3m3 0a6 6 0 01-7.029 5.912c-.563-.097-1.159.026-1.563.43L10.5 17.25H8.25v2.25H6v2.25H2.25v-2.818c0-.597.237-1.17.659-1.591l6.499-6.499c.404-.404.527-1 .43-1.563A6 6 0 1121.75 8.25z" />
                        </svg>
                      </div>
                    </div>
                    <div>
                      <p className="text-sm font-medium text-gray-900">
                        {passkey.name || 'Unnamed Passkey'}
                      </p>
                      <div className="mt-1 text-xs text-gray-500 space-y-0.5">
                        <p>Type: {passkey.credType}</p>
                        {passkey.deviceType && <p>Device: {passkey.deviceType}</p>}
                        <p>Registered: {new Date(passkey.createdAt).toLocaleDateString()}</p>
                        {passkey.lastUsedAt && (
                          <p>Last used: {new Date(passkey.lastUsedAt).toLocaleDateString()}</p>
                        )}
                      </div>
                    </div>
                  </div>

                  <button
                    onClick={() => handleDelete(passkey.id, passkey.name)}
                    disabled={deletingId === passkey.id}
                    className="inline-flex items-center px-3 py-1.5 border border-red-300 text-xs font-medium rounded-md text-red-700 bg-white hover:bg-red-50 disabled:opacity-50 disabled:cursor-not-allowed"
                  >
                    {deletingId === passkey.id ? 'Deleting...' : 'Delete'}
                  </button>
                </div>
              ))}
            </div>
          )}
        </div>
      </div>
    </PortalLayout>
  );
}
