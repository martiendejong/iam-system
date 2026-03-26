import { useState, useEffect } from 'react';
import PortalLayout from '../../components/layout/PortalLayout';
import { portalApi } from '../../services/portalApi';
import type { LinkedIdentity, DuplicateEmailGroup } from '../../services/portalApi';

// Provider display config
const PROVIDER_CONFIG: Record<string, { label: string; color: string; bgColor: string }> = {
  Google: { label: 'Google', color: 'text-red-700', bgColor: 'bg-red-50' },
  Microsoft: { label: 'Microsoft', color: 'text-blue-700', bgColor: 'bg-blue-50' },
  GitHub: { label: 'GitHub', color: 'text-gray-900', bgColor: 'bg-gray-100' },
  Apple: { label: 'Apple', color: 'text-gray-800', bgColor: 'bg-gray-50' },
  SAML: { label: 'SAML SSO', color: 'text-purple-700', bgColor: 'bg-purple-50' },
  OIDC: { label: 'OpenID Connect', color: 'text-indigo-700', bgColor: 'bg-indigo-50' },
};

function getProviderDisplay(provider: string) {
  return PROVIDER_CONFIG[provider] || { label: provider, color: 'text-gray-700', bgColor: 'bg-gray-50' };
}

export default function PortalLinkedAccountsPage() {
  const [identities, setIdentities] = useState<LinkedIdentity[]>([]);
  const [mergeSuggestions, setMergeSuggestions] = useState<DuplicateEmailGroup[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [success, setSuccess] = useState('');
  const [actionLoading, setActionLoading] = useState<string | null>(null);
  const [showMergeConfirm, setShowMergeConfirm] = useState<{ email: string; secondaryUserId: string } | null>(null);

  useEffect(() => {
    loadData();
  }, []);

  const loadData = async () => {
    try {
      setLoading(true);
      setError('');
      const [ids, suggestions] = await Promise.all([
        portalApi.getLinkedIdentities(),
        portalApi.getMergeSuggestions(),
      ]);
      setIdentities(ids);
      setMergeSuggestions(suggestions);
    } catch (err: any) {
      setError(err.response?.data?.error || 'Failed to load linked accounts');
    } finally {
      setLoading(false);
    }
  };

  const handleUnlink = async (provider: string) => {
    if (!confirm(`Are you sure you want to unlink your ${provider} account?`)) return;

    try {
      setActionLoading(provider);
      setError('');
      setSuccess('');
      await portalApi.unlinkProvider(provider);
      setSuccess(`${provider} account unlinked successfully`);
      await loadData();
    } catch (err: any) {
      setError(err.response?.data?.error || `Failed to unlink ${provider}`);
    } finally {
      setActionLoading(null);
    }
  };

  const handleSetPrimary = async (id: string, provider: string) => {
    try {
      setActionLoading(id);
      setError('');
      setSuccess('');
      await portalApi.setPrimaryIdentity(id);
      setSuccess(`${provider} set as primary identity`);
      await loadData();
    } catch (err: any) {
      setError(err.response?.data?.error || 'Failed to set primary identity');
    } finally {
      setActionLoading(null);
    }
  };

  const handleMerge = async () => {
    if (!showMergeConfirm) return;

    try {
      setActionLoading('merge');
      setError('');
      setSuccess('');
      await portalApi.mergeAccounts(showMergeConfirm.secondaryUserId);
      setSuccess('Accounts merged successfully. The duplicate account has been deactivated.');
      setShowMergeConfirm(null);
      await loadData();
    } catch (err: any) {
      setError(err.response?.data?.error || 'Failed to merge accounts');
    } finally {
      setActionLoading(null);
    }
  };

  const formatDate = (dateStr: string) => {
    return new Date(dateStr).toLocaleDateString('en-US', {
      year: 'numeric',
      month: 'short',
      day: 'numeric',
      hour: '2-digit',
      minute: '2-digit',
    });
  };

  return (
    <PortalLayout>
      <div className="space-y-6">
        {/* Header */}
        <div>
          <h1 className="text-2xl font-bold text-gray-900">Linked Accounts</h1>
          <p className="mt-1 text-sm text-gray-500">
            Manage your connected external accounts and identity providers.
          </p>
        </div>

        {/* Status Messages */}
        {error && (
          <div className="rounded-md bg-red-50 p-4">
            <p className="text-sm text-red-700">{error}</p>
          </div>
        )}
        {success && (
          <div className="rounded-md bg-green-50 p-4">
            <p className="text-sm text-green-700">{success}</p>
          </div>
        )}

        {loading ? (
          <div className="bg-white rounded-lg shadow-sm p-8 text-center text-gray-500">
            Loading linked accounts...
          </div>
        ) : (
          <>
            {/* Linked Identities */}
            <div className="bg-white rounded-lg shadow-sm">
              <div className="px-6 py-4 border-b border-gray-200">
                <h2 className="text-lg font-medium text-gray-900">External Identities</h2>
                <p className="mt-1 text-sm text-gray-500">
                  {identities.length === 0
                    ? 'No external accounts linked yet.'
                    : `You have ${identities.length} linked external ${identities.length === 1 ? 'account' : 'accounts'}.`}
                </p>
              </div>

              {identities.length > 0 && (
                <ul className="divide-y divide-gray-200">
                  {identities.map((identity) => {
                    const display = getProviderDisplay(identity.provider);
                    const isLoading = actionLoading === identity.provider || actionLoading === identity.id;

                    return (
                      <li key={identity.id} className="px-6 py-4">
                        <div className="flex items-center justify-between">
                          <div className="flex items-center gap-4">
                            {/* Provider Badge */}
                            <span
                              className={`inline-flex items-center px-3 py-1 rounded-full text-sm font-medium ${display.bgColor} ${display.color}`}
                            >
                              {display.label}
                            </span>

                            <div>
                              <div className="flex items-center gap-2">
                                <p className="text-sm font-medium text-gray-900">
                                  {identity.displayName || identity.email || identity.providerUserId}
                                </p>
                                {identity.isPrimary && (
                                  <span className="inline-flex items-center px-2 py-0.5 rounded text-xs font-medium bg-indigo-100 text-indigo-800">
                                    Primary
                                  </span>
                                )}
                              </div>
                              {identity.email && (
                                <p className="text-sm text-gray-500">{identity.email}</p>
                              )}
                              <p className="text-xs text-gray-400 mt-1">
                                Linked {formatDate(identity.linkedAt)}
                                {identity.lastUsedAt && (
                                  <> &middot; Last used {formatDate(identity.lastUsedAt)}</>
                                )}
                              </p>
                            </div>
                          </div>

                          {/* Actions */}
                          <div className="flex items-center gap-2">
                            {!identity.isPrimary && (
                              <button
                                onClick={() => handleSetPrimary(identity.id, identity.provider)}
                                disabled={isLoading}
                                className="inline-flex items-center px-3 py-1.5 border border-gray-300 text-sm font-medium rounded-md text-gray-700 bg-white hover:bg-gray-50 disabled:opacity-50"
                              >
                                {isLoading ? 'Setting...' : 'Set Primary'}
                              </button>
                            )}
                            <button
                              onClick={() => handleUnlink(identity.provider)}
                              disabled={isLoading}
                              className="inline-flex items-center px-3 py-1.5 border border-red-300 text-sm font-medium rounded-md text-red-700 bg-white hover:bg-red-50 disabled:opacity-50"
                            >
                              {isLoading ? 'Unlinking...' : 'Unlink'}
                            </button>
                          </div>
                        </div>
                      </li>
                    );
                  })}
                </ul>
              )}

              {identities.length === 0 && (
                <div className="px-6 py-8 text-center text-gray-500">
                  <LinkIcon className="mx-auto h-12 w-12 text-gray-300" />
                  <p className="mt-2 text-sm">
                    Link an external account to enable single sign-on and simplify your login experience.
                  </p>
                </div>
              )}
            </div>

            {/* Merge Suggestions */}
            {mergeSuggestions.length > 0 && (
              <div className="bg-white rounded-lg shadow-sm border-l-4 border-amber-400">
                <div className="px-6 py-4 border-b border-gray-200">
                  <div className="flex items-center gap-2">
                    <WarningIcon className="h-5 w-5 text-amber-500" />
                    <h2 className="text-lg font-medium text-gray-900">Duplicate Account Detected</h2>
                  </div>
                  <p className="mt-1 text-sm text-gray-500">
                    We found other accounts that share an email address with your linked accounts.
                    You can merge them to consolidate your access.
                  </p>
                </div>

                <ul className="divide-y divide-gray-200">
                  {mergeSuggestions.map((group) => (
                    <li key={group.email} className="px-6 py-4">
                      <p className="text-sm font-medium text-gray-900 mb-3">
                        Shared email: <span className="text-indigo-600">{group.email}</span>
                      </p>
                      <div className="space-y-2">
                        {group.accounts.map((account) => (
                          <div
                            key={account.userId}
                            className="flex items-center justify-between bg-gray-50 rounded-lg px-4 py-3"
                          >
                            <div>
                              <p className="text-sm font-medium text-gray-900">
                                {account.firstName} {account.lastName}
                              </p>
                              <p className="text-xs text-gray-500">
                                {account.email} &middot; Created {formatDate(account.createdAt)}
                                {account.lastLoginAt && <> &middot; Last login {formatDate(account.lastLoginAt)}</>}
                              </p>
                              <div className="flex items-center gap-1 mt-1">
                                {account.hasPassword && (
                                  <span className="inline-flex items-center px-2 py-0.5 rounded text-xs font-medium bg-gray-100 text-gray-600">
                                    Password
                                  </span>
                                )}
                                {account.linkedProviders.map((p) => (
                                  <span
                                    key={p}
                                    className={`inline-flex items-center px-2 py-0.5 rounded text-xs font-medium ${getProviderDisplay(p).bgColor} ${getProviderDisplay(p).color}`}
                                  >
                                    {p}
                                  </span>
                                ))}
                              </div>
                            </div>

                            <button
                              onClick={() =>
                                setShowMergeConfirm({ email: group.email, secondaryUserId: account.userId })
                              }
                              className="inline-flex items-center px-3 py-1.5 border border-amber-300 text-sm font-medium rounded-md text-amber-700 bg-white hover:bg-amber-50"
                            >
                              Merge into my account
                            </button>
                          </div>
                        ))}
                      </div>
                    </li>
                  ))}
                </ul>
              </div>
            )}
          </>
        )}

        {/* Merge Confirmation Modal */}
        {showMergeConfirm && (
          <div className="fixed inset-0 z-50 overflow-y-auto">
            <div className="flex min-h-full items-center justify-center p-4">
              <div
                className="fixed inset-0 bg-gray-500 bg-opacity-75 transition-opacity"
                onClick={() => setShowMergeConfirm(null)}
              />
              <div className="relative bg-white rounded-lg shadow-xl max-w-md w-full p-6">
                <h3 className="text-lg font-medium text-gray-900 mb-2">Confirm Account Merge</h3>
                <p className="text-sm text-gray-500 mb-4">
                  This will merge the duplicate account into yours. All external logins, roles, and
                  activity history from the other account will be transferred to your account. The
                  duplicate account will be deactivated.
                </p>
                <p className="text-sm font-medium text-red-600 mb-4">
                  This action cannot be undone.
                </p>
                <div className="flex justify-end gap-3">
                  <button
                    onClick={() => setShowMergeConfirm(null)}
                    disabled={actionLoading === 'merge'}
                    className="px-4 py-2 text-sm font-medium text-gray-700 bg-white border border-gray-300 rounded-md hover:bg-gray-50"
                  >
                    Cancel
                  </button>
                  <button
                    onClick={handleMerge}
                    disabled={actionLoading === 'merge'}
                    className="px-4 py-2 text-sm font-medium text-white bg-amber-600 border border-transparent rounded-md hover:bg-amber-700 disabled:opacity-50"
                  >
                    {actionLoading === 'merge' ? 'Merging...' : 'Confirm Merge'}
                  </button>
                </div>
              </div>
            </div>
          </div>
        )}
      </div>
    </PortalLayout>
  );
}

// ─── Simple inline SVG icons ─────────────────────────────

function LinkIcon({ className }: { className?: string }) {
  return (
    <svg className={className} fill="none" viewBox="0 0 24 24" strokeWidth={1.5} stroke="currentColor">
      <path
        strokeLinecap="round"
        strokeLinejoin="round"
        d="M13.19 8.688a4.5 4.5 0 011.242 7.244l-4.5 4.5a4.5 4.5 0 01-6.364-6.364l1.757-1.757m9.86-2.502a4.5 4.5 0 00-6.364-6.364L4.5 8.25a4.5 4.5 0 006.364 6.364l1.757-1.757"
      />
    </svg>
  );
}

function WarningIcon({ className }: { className?: string }) {
  return (
    <svg className={className} fill="none" viewBox="0 0 24 24" strokeWidth={1.5} stroke="currentColor">
      <path
        strokeLinecap="round"
        strokeLinejoin="round"
        d="M12 9v3.75m-9.303 3.376c-.866 1.5.217 3.374 1.948 3.374h14.71c1.73 0 2.813-1.874 1.948-3.374L13.949 3.378c-.866-1.5-3.032-1.5-3.898 0L2.697 16.126zM12 15.75h.007v.008H12v-.008z"
      />
    </svg>
  );
}
