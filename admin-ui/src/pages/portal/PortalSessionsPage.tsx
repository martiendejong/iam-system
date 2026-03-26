import { useState, useEffect } from 'react';
import PortalLayout from '../../components/layout/PortalLayout';
import { portalApi } from '../../services/portalApi';
import type { Session } from '../../services/portalApi';

export default function PortalSessionsPage() {
  const [sessions, setSessions] = useState<Session[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [actionLoading, setActionLoading] = useState<string | null>(null);
  const [success, setSuccess] = useState('');

  useEffect(() => {
    loadSessions();
  }, []);

  const loadSessions = async () => {
    try {
      setLoading(true);
      setError('');
      const data = await portalApi.getSessions();
      setSessions(data);
    } catch (err: any) {
      setError(err.response?.data?.error || 'Failed to load sessions');
    } finally {
      setLoading(false);
    }
  };

  const handleRevoke = async (sessionId: string) => {
    if (!confirm('Revoke this session? The device will be logged out.')) return;

    try {
      setActionLoading(sessionId);
      setError('');
      await portalApi.revokeSession(sessionId);
      setSuccess('Session revoked successfully');
      setTimeout(() => setSuccess(''), 3000);
      await loadSessions();
    } catch (err: any) {
      setError(err.response?.data?.error || 'Failed to revoke session');
    } finally {
      setActionLoading(null);
    }
  };

  const handleRevokeOthers = async () => {
    if (!confirm('Revoke all other sessions? All other devices will be logged out.')) return;

    try {
      setActionLoading('revoke-others');
      setError('');
      const result = await portalApi.revokeOtherSessions();
      setSuccess(`Revoked ${result.revokedCount} session(s)`);
      setTimeout(() => setSuccess(''), 3000);
      await loadSessions();
    } catch (err: any) {
      setError(err.response?.data?.error || 'Failed to revoke sessions');
    } finally {
      setActionLoading(null);
    }
  };

  const parseUserAgent = (ua?: string): string => {
    if (!ua) return 'Unknown device';
    // Simple UA parsing
    if (ua.includes('Chrome') && !ua.includes('Edge')) return 'Chrome';
    if (ua.includes('Firefox')) return 'Firefox';
    if (ua.includes('Safari') && !ua.includes('Chrome')) return 'Safari';
    if (ua.includes('Edge')) return 'Edge';
    return 'Unknown browser';
  };

  const getDeviceIcon = (ua?: string): string => {
    if (!ua) return 'Unknown';
    if (ua.includes('Mobile') || ua.includes('Android') || ua.includes('iPhone')) return 'Mobile';
    return 'Desktop';
  };

  return (
    <PortalLayout>
      <div className="bg-white rounded-lg shadow-sm">
        <div className="px-6 py-4 border-b border-gray-200 flex justify-between items-center">
          <div>
            <h2 className="text-lg font-semibold text-gray-900">Active Sessions</h2>
            <p className="mt-1 text-sm text-gray-500">Manage your active sessions across devices.</p>
          </div>
          {sessions.length > 1 && (
            <button
              onClick={handleRevokeOthers}
              disabled={actionLoading === 'revoke-others'}
              className="inline-flex items-center px-4 py-2 border border-red-300 text-sm font-medium rounded-md text-red-700 bg-white hover:bg-red-50 disabled:opacity-50 disabled:cursor-not-allowed"
            >
              {actionLoading === 'revoke-others' ? 'Revoking...' : 'Revoke All Other Sessions'}
            </button>
          )}
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
              <p className="mt-2 text-sm text-gray-500">Loading sessions...</p>
            </div>
          ) : sessions.length === 0 ? (
            <p className="text-sm text-gray-500 text-center py-8">No active sessions found.</p>
          ) : (
            <div className="space-y-3">
              {sessions.map((session) => (
                <div
                  key={session.id}
                  className={`border rounded-lg p-4 ${
                    session.isCurrent
                      ? 'border-indigo-300 bg-indigo-50'
                      : 'border-gray-200'
                  }`}
                >
                  <div className="flex justify-between items-start">
                    <div className="flex gap-3">
                      <div className="mt-0.5">
                        <div className="h-10 w-10 rounded-full bg-gray-100 flex items-center justify-center">
                          <span className="text-xs text-gray-600 font-medium">
                            {getDeviceIcon(session.userAgent) === 'Mobile' ? 'M' : 'D'}
                          </span>
                        </div>
                      </div>
                      <div>
                        <div className="flex items-center gap-2">
                          <span className="text-sm font-medium text-gray-900">
                            {session.deviceInfo || parseUserAgent(session.userAgent)}
                          </span>
                          {session.isCurrent && (
                            <span className="inline-flex rounded-full px-2 py-0.5 text-xs font-semibold bg-indigo-100 text-indigo-800">
                              Current Session
                            </span>
                          )}
                        </div>
                        <div className="mt-1 text-xs text-gray-500 space-y-0.5">
                          {session.ipAddress && (
                            <p>IP: <span className="font-mono">{session.ipAddress}</span></p>
                          )}
                          {session.location && <p>Location: {session.location}</p>}
                          <p>
                            Last active: {session.lastActivityAt
                              ? new Date(session.lastActivityAt).toLocaleString()
                              : new Date(session.createdAt).toLocaleString()}
                          </p>
                          <p>Created: {new Date(session.createdAt).toLocaleString()}</p>
                        </div>
                      </div>
                    </div>

                    {!session.isCurrent && (
                      <button
                        onClick={() => handleRevoke(session.id)}
                        disabled={actionLoading === session.id}
                        className="inline-flex items-center px-3 py-1.5 border border-red-300 text-xs font-medium rounded-md text-red-700 bg-white hover:bg-red-50 disabled:opacity-50 disabled:cursor-not-allowed"
                      >
                        {actionLoading === session.id ? 'Revoking...' : 'Revoke'}
                      </button>
                    )}
                  </div>
                </div>
              ))}
            </div>
          )}
        </div>
      </div>
    </PortalLayout>
  );
}
