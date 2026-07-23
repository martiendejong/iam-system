import { useState, useEffect } from 'react';
import PortalLayout from '../../components/layout/PortalLayout';
import { portalApi } from '../../services/portalApi';
import type { ConsentRecord, DataRequestEntry } from '../../services/portalApi';

export default function PortalPrivacyPage() {
  const [consents, setConsents] = useState<ConsentRecord[]>([]);
  const [dataRequests, setDataRequests] = useState<DataRequestEntry[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [successMessage, setSuccessMessage] = useState('');
  const [revokingClientId, setRevokingClientId] = useState<string | null>(null);
  const [requestingExport, setRequestingExport] = useState(false);
  const [showDeletionDialog, setShowDeletionDialog] = useState(false);
  const [deletionReason, setDeletionReason] = useState('');
  const [requestingDeletion, setRequestingDeletion] = useState(false);
  const [downloadingId, setDownloadingId] = useState<string | null>(null);

  useEffect(() => {
    loadData();
  }, []);

  const loadData = async () => {
    try {
      setLoading(true);
      setError('');
      const [consentsData, requestsData] = await Promise.all([
        portalApi.getMyConsents(),
        portalApi.getMyDataRequests(),
      ]);
      setConsents(consentsData);
      setDataRequests(requestsData);
    } catch (err: any) {
      setError(err.response?.data?.error || 'Failed to load privacy data');
    } finally {
      setLoading(false);
    }
  };

  const handleRevokeConsent = async (clientId: string) => {
    if (!confirm(`Are you sure you want to revoke consent for client "${clientId}"? The application will no longer have access to your data.`)) {
      return;
    }

    try {
      setRevokingClientId(clientId);
      setError('');
      await portalApi.revokeConsent(clientId);
      setSuccessMessage('Consent revoked successfully');
      setTimeout(() => setSuccessMessage(''), 3000);
      await loadData();
    } catch (err: any) {
      setError(err.response?.data?.error || 'Failed to revoke consent');
    } finally {
      setRevokingClientId(null);
    }
  };

  const handleRequestExport = async () => {
    try {
      setRequestingExport(true);
      setError('');
      await portalApi.requestDataExport();
      setSuccessMessage('Data export request submitted. You will be notified when it is ready for download.');
      setTimeout(() => setSuccessMessage(''), 5000);
      await loadData();
    } catch (err: any) {
      setError(err.response?.data?.error || 'Failed to request data export');
    } finally {
      setRequestingExport(false);
    }
  };

  const handleRequestDeletion = async () => {
    try {
      setRequestingDeletion(true);
      setError('');
      await portalApi.requestAccountDeletion(deletionReason || undefined);
      setShowDeletionDialog(false);
      setDeletionReason('');
      setSuccessMessage('Account deletion request submitted. An administrator will process your request.');
      setTimeout(() => setSuccessMessage(''), 5000);
      await loadData();
    } catch (err: any) {
      setError(err.response?.data?.error || 'Failed to request account deletion');
    } finally {
      setRequestingDeletion(false);
    }
  };

  const handleDownloadExport = async (requestId: string) => {
    try {
      setDownloadingId(requestId);
      const blob = await portalApi.downloadExport(requestId);
      const url = URL.createObjectURL(blob);
      const link = document.createElement('a');
      link.href = url;
      link.download = `my-data-export.json`;
      link.click();
      URL.revokeObjectURL(url);
    } catch (err: any) {
      setError(err.response?.data?.error || 'Failed to download export');
    } finally {
      setDownloadingId(null);
    }
  };

  const getStatusColor = (status: string) => {
    switch (status) {
      case 'Pending': return 'bg-yellow-100 text-yellow-800';
      case 'Processing': return 'bg-blue-100 text-blue-800';
      case 'Completed': return 'bg-green-100 text-green-800';
      case 'Rejected': return 'bg-red-100 text-red-800';
      default: return 'bg-gray-100 text-gray-800';
    }
  };

  if (loading) {
    return (
      <PortalLayout>
        <div className="bg-white rounded-lg shadow-sm p-6">
          <div className="animate-pulse space-y-4">
            <div className="h-6 bg-gray-200 rounded w-1/4"></div>
            <div className="space-y-3">
              <div className="h-16 bg-gray-200 rounded"></div>
              <div className="h-16 bg-gray-200 rounded"></div>
            </div>
          </div>
        </div>
      </PortalLayout>
    );
  }

  return (
    <PortalLayout>
      <div className="space-y-6">
        {/* Messages */}
        {error && (
          <div className="bg-red-50 border border-red-200 text-red-700 px-4 py-3 rounded text-sm">
            {error}
          </div>
        )}
        {successMessage && (
          <div className="bg-green-50 border border-green-200 text-green-700 px-4 py-3 rounded text-sm">
            {successMessage}
          </div>
        )}

        {/* My Consents */}
        <div className="bg-white rounded-lg shadow-sm">
          <div className="px-6 py-4 border-b border-gray-200">
            <h2 className="text-lg font-semibold text-gray-900">My Consents</h2>
            <p className="mt-1 text-sm text-gray-500">
              Applications you have granted access to your data.
            </p>
          </div>
          <div className="px-6 py-4">
            {consents.length === 0 ? (
              <p className="text-sm text-gray-500 py-4 text-center">
                No active consents. You have not granted any applications access to your data.
              </p>
            ) : (
              <div className="space-y-3">
                {consents.map((consent) => (
                  <div key={consent.id} className="flex items-center justify-between border border-gray-200 rounded-lg p-4">
                    <div>
                      <div className="text-sm font-medium text-gray-900">{consent.clientId}</div>
                      <div className="text-xs text-gray-500 mt-1">
                        Scopes: <span className="font-mono">{consent.scopes}</span>
                      </div>
                      <div className="text-xs text-gray-400 mt-0.5">
                        Granted {new Date(consent.grantedAt).toLocaleDateString()}
                        {consent.ipAddress && ` from ${consent.ipAddress}`}
                      </div>
                    </div>
                    <button
                      onClick={() => handleRevokeConsent(consent.clientId)}
                      disabled={revokingClientId === consent.clientId}
                      className="inline-flex items-center px-3 py-1.5 border border-red-300 text-xs font-medium rounded-md text-red-700 bg-white hover:bg-red-50 disabled:opacity-50 disabled:cursor-not-allowed"
                    >
                      {revokingClientId === consent.clientId ? 'Revoking...' : 'Revoke'}
                    </button>
                  </div>
                ))}
              </div>
            )}
          </div>
        </div>

        {/* Data Rights Actions */}
        <div className="bg-white rounded-lg shadow-sm">
          <div className="px-6 py-4 border-b border-gray-200">
            <h2 className="text-lg font-semibold text-gray-900">Your Data Rights</h2>
            <p className="mt-1 text-sm text-gray-500">
              Under GDPR, you have the right to access and delete your personal data.
            </p>
          </div>
          <div className="px-6 py-4 space-y-4">
            {/* Export */}
            <div className="flex items-center justify-between border border-gray-200 rounded-lg p-4">
              <div>
                <div className="text-sm font-medium text-gray-900">Export My Data</div>
                <div className="text-xs text-gray-500 mt-1">
                  Download a JSON file containing all your personal data stored in our system.
                  The download link will be available for 48 hours.
                </div>
              </div>
              <button
                onClick={handleRequestExport}
                disabled={requestingExport}
                className="inline-flex items-center px-4 py-2 border border-transparent text-sm font-medium rounded-md text-white bg-indigo-600 hover:bg-indigo-700 disabled:opacity-50 disabled:cursor-not-allowed flex-shrink-0"
              >
                {requestingExport ? 'Requesting...' : 'Request Export'}
              </button>
            </div>

            {/* Deletion */}
            <div className="flex items-center justify-between border border-red-200 rounded-lg p-4 bg-red-50">
              <div>
                <div className="text-sm font-medium text-red-900">Delete My Account</div>
                <div className="text-xs text-red-700 mt-1">
                  Request permanent anonymization of your account data. This action is irreversible
                  and will be processed by an administrator.
                </div>
              </div>
              <button
                onClick={() => setShowDeletionDialog(true)}
                className="inline-flex items-center px-4 py-2 border border-transparent text-sm font-medium rounded-md text-white bg-red-600 hover:bg-red-700 flex-shrink-0"
              >
                Request Deletion
              </button>
            </div>
          </div>
        </div>

        {/* My Data Requests */}
        {dataRequests.length > 0 && (
          <div className="bg-white rounded-lg shadow-sm">
            <div className="px-6 py-4 border-b border-gray-200">
              <h2 className="text-lg font-semibold text-gray-900">My Data Requests</h2>
            </div>
            <div className="px-6 py-4">
              <div className="space-y-3">
                {dataRequests.map((request) => (
                  <div key={request.id} className="flex items-center justify-between border border-gray-200 rounded-lg p-4">
                    <div>
                      <div className="flex items-center gap-2">
                        <span className={`inline-flex rounded-full px-2 py-0.5 text-xs font-semibold ${
                          request.type === 'Export' ? 'bg-blue-100 text-blue-800' : 'bg-red-100 text-red-800'
                        }`}>
                          {request.type}
                        </span>
                        <span className={`inline-flex rounded-full px-2 py-0.5 text-xs font-semibold ${getStatusColor(request.status)}`}>
                          {request.status}
                        </span>
                      </div>
                      <div className="text-xs text-gray-500 mt-1">
                        Requested {new Date(request.requestedAt).toLocaleString()}
                        {request.completedAt && ` | Completed ${new Date(request.completedAt).toLocaleString()}`}
                      </div>
                      {request.notes && (
                        <div className="text-xs text-gray-400 mt-0.5">{request.notes}</div>
                      )}
                    </div>
                    {request.hasDownload && (
                      <button
                        onClick={() => handleDownloadExport(request.id)}
                        disabled={downloadingId === request.id}
                        className="inline-flex items-center px-3 py-1.5 border border-indigo-300 text-xs font-medium rounded-md text-indigo-700 bg-white hover:bg-indigo-50 disabled:opacity-50 disabled:cursor-not-allowed"
                      >
                        {downloadingId === request.id ? 'Downloading...' : 'Download'}
                      </button>
                    )}
                  </div>
                ))}
              </div>
            </div>
          </div>
        )}

        {/* Deletion Confirmation Dialog */}
        {showDeletionDialog && (
          <div className="fixed inset-0 bg-gray-500 bg-opacity-75 flex items-center justify-center z-50">
            <div className="bg-white rounded-lg shadow-xl max-w-md w-full mx-4">
              <div className="px-6 py-4 border-b border-gray-200">
                <h3 className="text-lg font-semibold text-gray-900">Confirm Account Deletion</h3>
              </div>
              <div className="px-6 py-4 space-y-4">
                <div className="bg-red-50 border border-red-200 rounded-lg p-4">
                  <div className="flex">
                    <svg className="h-5 w-5 text-red-400 flex-shrink-0" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 9v2m0 4h.01m-6.938 4h13.856c1.54 0 2.502-1.667 1.732-2.5L13.732 4c-.77-.833-1.732-.833-2.5 0L4.232 16.5c-.77.833.192 2.5 1.732 2.5z" />
                    </svg>
                    <div className="ml-3">
                      <h3 className="text-sm font-medium text-red-800">This action is irreversible</h3>
                      <p className="mt-1 text-xs text-red-700">
                        Your personal data will be permanently anonymized. You will lose access to your account,
                        all active sessions will be terminated, and your authentication credentials will be removed.
                        Audit logs will be preserved with anonymized references.
                      </p>
                    </div>
                  </div>
                </div>

                <div>
                  <label htmlFor="deletionReason" className="block text-sm font-medium text-gray-700 mb-1">
                    Reason (optional)
                  </label>
                  <textarea
                    id="deletionReason"
                    value={deletionReason}
                    onChange={(e) => setDeletionReason(e.target.value)}
                    rows={3}
                    placeholder="Please let us know why you are leaving..."
                    className="block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                  />
                </div>
              </div>
              <div className="px-6 py-4 bg-gray-50 rounded-b-lg flex justify-end gap-3">
                <button
                  onClick={() => {
                    setShowDeletionDialog(false);
                    setDeletionReason('');
                  }}
                  className="inline-flex items-center px-4 py-2 border border-gray-300 text-sm font-medium rounded-md text-gray-700 bg-white hover:bg-gray-50"
                >
                  Cancel
                </button>
                <button
                  onClick={handleRequestDeletion}
                  disabled={requestingDeletion}
                  className="inline-flex items-center px-4 py-2 border border-transparent text-sm font-medium rounded-md text-white bg-red-600 hover:bg-red-700 disabled:opacity-50 disabled:cursor-not-allowed"
                >
                  {requestingDeletion ? 'Submitting...' : 'Confirm Deletion Request'}
                </button>
              </div>
            </div>
          </div>
        )}
      </div>
    </PortalLayout>
  );
}
