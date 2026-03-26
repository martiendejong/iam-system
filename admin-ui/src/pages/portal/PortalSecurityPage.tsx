import { useState, useEffect } from 'react';
import { useNavigate } from 'react-router-dom';
import PortalLayout from '../../components/layout/PortalLayout';
import { portalApi } from '../../services/portalApi';
import type { SecuritySummary } from '../../services/portalApi';

export default function PortalSecurityPage() {
  const navigate = useNavigate();
  const [summary, setSummary] = useState<SecuritySummary | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');

  // Change password state
  const [currentPassword, setCurrentPassword] = useState('');
  const [newPassword, setNewPassword] = useState('');
  const [confirmPassword, setConfirmPassword] = useState('');
  const [passwordError, setPasswordError] = useState('');
  const [passwordSuccess, setPasswordSuccess] = useState('');
  const [changingPassword, setChangingPassword] = useState(false);

  // Recovery codes state
  const [recoveryCodes, setRecoveryCodes] = useState<string[] | null>(null);
  const [regenerating, setRegenerating] = useState(false);

  useEffect(() => {
    loadSummary();
  }, []);

  const loadSummary = async () => {
    try {
      setLoading(true);
      setError('');
      const data = await portalApi.getSecuritySummary();
      setSummary(data);
    } catch (err: any) {
      setError(err.response?.data?.error || 'Failed to load security summary');
    } finally {
      setLoading(false);
    }
  };

  const handleChangePassword = async (e: React.FormEvent) => {
    e.preventDefault();
    setPasswordError('');
    setPasswordSuccess('');

    if (newPassword !== confirmPassword) {
      setPasswordError('New passwords do not match');
      return;
    }

    if (newPassword.length < 8) {
      setPasswordError('New password must be at least 8 characters');
      return;
    }

    try {
      setChangingPassword(true);
      await portalApi.changePassword({ currentPassword, newPassword });
      setPasswordSuccess('Password changed successfully');
      setCurrentPassword('');
      setNewPassword('');
      setConfirmPassword('');
      setTimeout(() => setPasswordSuccess(''), 3000);
    } catch (err: any) {
      setPasswordError(err.response?.data?.error || 'Failed to change password');
    } finally {
      setChangingPassword(false);
    }
  };

  const handleRegenerateRecoveryCodes = async () => {
    if (!confirm('This will invalidate all existing recovery codes. Continue?')) return;

    try {
      setRegenerating(true);
      const result = await portalApi.regenerateRecoveryCodes();
      setRecoveryCodes(result.recoveryCodes);
      // Refresh summary to update the count
      loadSummary();
    } catch (err: any) {
      setError(err.response?.data?.error || 'Failed to regenerate recovery codes');
    } finally {
      setRegenerating(false);
    }
  };

  if (loading) {
    return (
      <PortalLayout>
        <div className="bg-white rounded-lg shadow-sm p-6">
          <div className="animate-pulse space-y-4">
            <div className="h-6 bg-gray-200 rounded w-1/4"></div>
            <div className="grid grid-cols-2 gap-4">
              <div className="h-24 bg-gray-200 rounded"></div>
              <div className="h-24 bg-gray-200 rounded"></div>
            </div>
          </div>
        </div>
      </PortalLayout>
    );
  }

  return (
    <PortalLayout>
      <div className="space-y-6">
        {error && (
          <div className="bg-red-50 border border-red-200 text-red-700 px-4 py-3 rounded text-sm">
            {error}
          </div>
        )}

        {/* Security Summary Cards */}
        <div className="bg-white rounded-lg shadow-sm">
          <div className="px-6 py-4 border-b border-gray-200">
            <h2 className="text-lg font-semibold text-gray-900">Security Overview</h2>
          </div>
          <div className="p-6 grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-4 gap-4">
            {/* MFA Status */}
            <div className="border border-gray-200 rounded-lg p-4">
              <div className="text-sm font-medium text-gray-500">Multi-Factor Auth</div>
              <div className="mt-2 flex items-center gap-2">
                <span className={`inline-flex rounded-full px-2 py-0.5 text-xs font-semibold ${
                  summary?.mfaEnabled
                    ? 'bg-green-100 text-green-800'
                    : 'bg-yellow-100 text-yellow-800'
                }`}>
                  {summary?.mfaEnabled ? 'Enabled' : 'Disabled'}
                </span>
                {summary?.mfaMethod && (
                  <span className="text-xs text-gray-500 uppercase">{summary.mfaMethod}</span>
                )}
              </div>
            </div>

            {/* Passkey Count */}
            <div className="border border-gray-200 rounded-lg p-4">
              <div className="text-sm font-medium text-gray-500">Passkeys</div>
              <div className="mt-2">
                <span className="text-2xl font-bold text-gray-900">{summary?.passkeyCount ?? 0}</span>
                <span className="text-sm text-gray-500 ml-1">registered</span>
              </div>
            </div>

            {/* Active Sessions */}
            <div className="border border-gray-200 rounded-lg p-4">
              <div className="text-sm font-medium text-gray-500">Active Sessions</div>
              <div className="mt-2">
                <span className="text-2xl font-bold text-gray-900">{summary?.sessionCount ?? 0}</span>
                <span className="text-sm text-gray-500 ml-1">active</span>
              </div>
            </div>

            {/* Last Login */}
            <div className="border border-gray-200 rounded-lg p-4">
              <div className="text-sm font-medium text-gray-500">Last Login</div>
              <div className="mt-2 text-sm text-gray-900">
                {summary?.lastLoginAt
                  ? new Date(summary.lastLoginAt).toLocaleString()
                  : 'Never'}
              </div>
            </div>
          </div>
        </div>

        {/* Change Password */}
        <div className="bg-white rounded-lg shadow-sm">
          <div className="px-6 py-4 border-b border-gray-200">
            <h2 className="text-lg font-semibold text-gray-900">Change Password</h2>
          </div>
          <form onSubmit={handleChangePassword} className="px-6 py-4 space-y-4">
            {passwordError && (
              <div className="bg-red-50 border border-red-200 text-red-700 px-4 py-3 rounded text-sm">
                {passwordError}
              </div>
            )}
            {passwordSuccess && (
              <div className="bg-green-50 border border-green-200 text-green-700 px-4 py-3 rounded text-sm">
                {passwordSuccess}
              </div>
            )}

            <div>
              <label htmlFor="currentPassword" className="block text-sm font-medium text-gray-700 mb-1">
                Current Password
              </label>
              <input
                id="currentPassword"
                type="password"
                value={currentPassword}
                onChange={(e) => setCurrentPassword(e.target.value)}
                required
                className="block w-full max-w-md rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
              />
            </div>

            <div>
              <label htmlFor="newPassword" className="block text-sm font-medium text-gray-700 mb-1">
                New Password
              </label>
              <input
                id="newPassword"
                type="password"
                value={newPassword}
                onChange={(e) => setNewPassword(e.target.value)}
                required
                minLength={8}
                className="block w-full max-w-md rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
              />
            </div>

            <div>
              <label htmlFor="confirmPassword" className="block text-sm font-medium text-gray-700 mb-1">
                Confirm New Password
              </label>
              <input
                id="confirmPassword"
                type="password"
                value={confirmPassword}
                onChange={(e) => setConfirmPassword(e.target.value)}
                required
                minLength={8}
                className="block w-full max-w-md rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
              />
            </div>

            <div>
              <button
                type="submit"
                disabled={changingPassword}
                className="inline-flex items-center px-4 py-2 border border-transparent text-sm font-medium rounded-md text-white bg-indigo-600 hover:bg-indigo-700 disabled:opacity-50 disabled:cursor-not-allowed"
              >
                {changingPassword ? 'Changing...' : 'Change Password'}
              </button>
            </div>
          </form>
        </div>

        {/* MFA Section */}
        <div className="bg-white rounded-lg shadow-sm">
          <div className="px-6 py-4 border-b border-gray-200">
            <h2 className="text-lg font-semibold text-gray-900">Two-Factor Authentication</h2>
          </div>
          <div className="px-6 py-4">
            {summary?.mfaEnabled ? (
              <div className="space-y-4">
                <div className="flex items-center gap-2">
                  <span className="inline-flex rounded-full px-2 py-0.5 text-xs font-semibold bg-green-100 text-green-800">
                    Enabled
                  </span>
                  <span className="text-sm text-gray-600">
                    via {summary.mfaMethod?.toUpperCase() || 'TOTP'} authenticator app
                  </span>
                </div>
                <p className="text-sm text-gray-500">
                  To disable MFA, use the MFA settings in the authentication section.
                </p>
              </div>
            ) : (
              <div className="space-y-4">
                <p className="text-sm text-gray-600">
                  Two-factor authentication adds an extra layer of security to your account.
                  When enabled, you will need to provide a code from your authenticator app in addition to your password.
                </p>
                <button
                  onClick={() => navigate('/portal/security')}
                  className="inline-flex items-center px-4 py-2 border border-transparent text-sm font-medium rounded-md text-white bg-indigo-600 hover:bg-indigo-700"
                >
                  Enable MFA
                </button>
              </div>
            )}
          </div>
        </div>

        {/* Recovery Codes */}
        {summary?.mfaEnabled && (
          <div className="bg-white rounded-lg shadow-sm">
            <div className="px-6 py-4 border-b border-gray-200">
              <h2 className="text-lg font-semibold text-gray-900">Recovery Codes</h2>
            </div>
            <div className="px-6 py-4 space-y-4">
              <p className="text-sm text-gray-600">
                Recovery codes can be used to access your account if you lose your authenticator device.
                You have <span className="font-semibold">{summary.recoveryCodesRemaining}</span> codes remaining.
              </p>

              {recoveryCodes && (
                <div className="bg-gray-50 border border-gray-200 rounded-lg p-4">
                  <p className="text-sm font-medium text-red-600 mb-2">
                    Save these codes in a safe place. They will not be shown again.
                  </p>
                  <div className="grid grid-cols-2 gap-2">
                    {recoveryCodes.map((code, index) => (
                      <code key={index} className="bg-white border border-gray-200 rounded px-3 py-1.5 text-sm font-mono text-gray-900">
                        {code}
                      </code>
                    ))}
                  </div>
                </div>
              )}

              <button
                onClick={handleRegenerateRecoveryCodes}
                disabled={regenerating}
                className="inline-flex items-center px-4 py-2 border border-gray-300 text-sm font-medium rounded-md text-gray-700 bg-white hover:bg-gray-50 disabled:opacity-50 disabled:cursor-not-allowed"
              >
                {regenerating ? 'Regenerating...' : 'Regenerate Recovery Codes'}
              </button>
            </div>
          </div>
        )}
      </div>
    </PortalLayout>
  );
}
