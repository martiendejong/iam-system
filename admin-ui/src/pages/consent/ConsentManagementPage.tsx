import { useState, useEffect } from 'react';
import DashboardLayout from '../../components/layout/DashboardLayout';
import { consentApi } from '../../services/consentApi';
import type { AdminDataRequest } from '../../services/consentApi';

type TabType = 'pending' | 'all';

export default function ConsentManagementPage() {
  const [activeTab, setActiveTab] = useState<TabType>('pending');
  const [pendingRequests, setPendingRequests] = useState<AdminDataRequest[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [processingId, setProcessingId] = useState<string | null>(null);
  const [successMessage, setSuccessMessage] = useState('');

  useEffect(() => {
    loadPendingRequests();
  }, []);

  const loadPendingRequests = async () => {
    try {
      setLoading(true);
      setError('');
      const data = await consentApi.getPendingRequests();
      setPendingRequests(data);
    } catch (err: any) {
      setError(err.response?.data?.error || 'Failed to load pending requests');
    } finally {
      setLoading(false);
    }
  };

  const handleProcessRequest = async (id: string) => {
    if (!confirm('Are you sure you want to process this request? This action may be irreversible for deletion requests.')) {
      return;
    }

    try {
      setProcessingId(id);
      setError('');
      await consentApi.processRequest(id);
      setSuccessMessage('Request processed successfully');
      setTimeout(() => setSuccessMessage(''), 3000);
      // Reload the list
      await loadPendingRequests();
    } catch (err: any) {
      setError(err.response?.data?.error || 'Failed to process request');
    } finally {
      setProcessingId(null);
    }
  };

  const getTypeColor = (type: string) => {
    switch (type) {
      case 'Export': return 'bg-blue-100 text-blue-800';
      case 'Deletion': return 'bg-red-100 text-red-800';
      case 'Rectification': return 'bg-yellow-100 text-yellow-800';
      default: return 'bg-gray-100 text-gray-800';
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

  return (
    <DashboardLayout>
      <div className="px-4 sm:px-6 lg:px-8">
        {/* Header */}
        <div className="sm:flex sm:items-center">
          <div className="sm:flex-auto">
            <h1 className="text-2xl font-semibold text-gray-900">Consent & GDPR Management</h1>
            <p className="mt-2 text-sm text-gray-700">
              Manage user consent records and process GDPR data subject requests.
            </p>
          </div>
        </div>

        {/* Tabs */}
        <div className="mt-6 border-b border-gray-200">
          <nav className="-mb-px flex space-x-8">
            <button
              onClick={() => setActiveTab('pending')}
              className={`py-4 px-1 border-b-2 font-medium text-sm ${
                activeTab === 'pending'
                  ? 'border-indigo-500 text-indigo-600'
                  : 'border-transparent text-gray-500 hover:text-gray-700 hover:border-gray-300'
              }`}
            >
              Pending Requests
              {pendingRequests.length > 0 && (
                <span className="ml-2 inline-flex items-center rounded-full bg-red-100 px-2 py-0.5 text-xs font-medium text-red-800">
                  {pendingRequests.length}
                </span>
              )}
            </button>
            <button
              onClick={() => setActiveTab('all')}
              className={`py-4 px-1 border-b-2 font-medium text-sm ${
                activeTab === 'all'
                  ? 'border-indigo-500 text-indigo-600'
                  : 'border-transparent text-gray-500 hover:text-gray-700 hover:border-gray-300'
              }`}
            >
              Overview
            </button>
          </nav>
        </div>

        {/* Messages */}
        {error && (
          <div className="mt-4 bg-red-50 border border-red-200 text-red-700 px-4 py-3 rounded text-sm">
            {error}
          </div>
        )}
        {successMessage && (
          <div className="mt-4 bg-green-50 border border-green-200 text-green-700 px-4 py-3 rounded text-sm">
            {successMessage}
          </div>
        )}

        {/* Pending Requests Tab */}
        {activeTab === 'pending' && (
          <div className="mt-6">
            {loading ? (
              <div className="text-center py-8">
                <div className="inline-block animate-spin rounded-full h-8 w-8 border-b-2 border-indigo-600"></div>
                <p className="mt-2 text-sm text-gray-500">Loading pending requests...</p>
              </div>
            ) : pendingRequests.length === 0 ? (
              <div className="text-center py-12">
                <svg className="mx-auto h-12 w-12 text-gray-400" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 12l2 2 4-4m6 2a9 9 0 11-18 0 9 9 0 0118 0z" />
                </svg>
                <h3 className="mt-2 text-sm font-medium text-gray-900">No pending requests</h3>
                <p className="mt-1 text-sm text-gray-500">All data subject requests have been processed.</p>
              </div>
            ) : (
              <div className="overflow-hidden shadow ring-1 ring-black ring-opacity-5 md:rounded-lg">
                <table className="min-w-full divide-y divide-gray-300">
                  <thead className="bg-gray-50">
                    <tr>
                      <th className="px-3 py-3.5 text-left text-sm font-semibold text-gray-900">User ID</th>
                      <th className="px-3 py-3.5 text-left text-sm font-semibold text-gray-900">Type</th>
                      <th className="px-3 py-3.5 text-left text-sm font-semibold text-gray-900">Status</th>
                      <th className="px-3 py-3.5 text-left text-sm font-semibold text-gray-900">Requested</th>
                      <th className="px-3 py-3.5 text-left text-sm font-semibold text-gray-900">Notes</th>
                      <th className="px-3 py-3.5 text-right text-sm font-semibold text-gray-900">Actions</th>
                    </tr>
                  </thead>
                  <tbody className="divide-y divide-gray-200 bg-white">
                    {pendingRequests.map((request) => (
                      <tr key={request.id}>
                        <td className="px-3 py-4 text-sm text-gray-500 font-mono">
                          {request.userId.slice(0, 8)}...
                        </td>
                        <td className="px-3 py-4 text-sm">
                          <span className={`inline-flex rounded-full px-2 py-0.5 text-xs font-semibold ${getTypeColor(request.type)}`}>
                            {request.type}
                          </span>
                        </td>
                        <td className="px-3 py-4 text-sm">
                          <span className={`inline-flex rounded-full px-2 py-0.5 text-xs font-semibold ${getStatusColor(request.status)}`}>
                            {request.status}
                          </span>
                        </td>
                        <td className="px-3 py-4 text-sm text-gray-500">
                          {new Date(request.requestedAt).toLocaleString()}
                        </td>
                        <td className="px-3 py-4 text-sm text-gray-500 max-w-xs truncate">
                          {request.notes || '-'}
                        </td>
                        <td className="px-3 py-4 text-sm text-right">
                          <button
                            onClick={() => handleProcessRequest(request.id)}
                            disabled={processingId === request.id}
                            className={`inline-flex items-center px-3 py-1.5 border border-transparent text-xs font-medium rounded-md text-white ${
                              request.type === 'Deletion'
                                ? 'bg-red-600 hover:bg-red-700'
                                : 'bg-indigo-600 hover:bg-indigo-700'
                            } disabled:opacity-50 disabled:cursor-not-allowed`}
                          >
                            {processingId === request.id ? 'Processing...' : 'Process'}
                          </button>
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </div>
        )}

        {/* Overview Tab */}
        {activeTab === 'all' && (
          <div className="mt-6 space-y-6">
            {/* GDPR Information Cards */}
            <div className="grid grid-cols-1 gap-5 sm:grid-cols-3">
              <div className="bg-white overflow-hidden shadow rounded-lg">
                <div className="p-5">
                  <div className="flex items-center">
                    <div className="flex-shrink-0">
                      <svg className="h-6 w-6 text-blue-400" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                        <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 12h6m-6 4h6m2 5H7a2 2 0 01-2-2V5a2 2 0 012-2h5.586a1 1 0 01.707.293l5.414 5.414a1 1 0 01.293.707V19a2 2 0 01-2 2z" />
                      </svg>
                    </div>
                    <div className="ml-5 w-0 flex-1">
                      <dl>
                        <dt className="text-sm font-medium text-gray-500 truncate">Article 15</dt>
                        <dd className="text-lg font-medium text-gray-900">Right of Access</dd>
                      </dl>
                      <p className="mt-1 text-xs text-gray-500">Users can request a copy of all their personal data.</p>
                    </div>
                  </div>
                </div>
              </div>

              <div className="bg-white overflow-hidden shadow rounded-lg">
                <div className="p-5">
                  <div className="flex items-center">
                    <div className="flex-shrink-0">
                      <svg className="h-6 w-6 text-red-400" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                        <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M19 7l-.867 12.142A2 2 0 0116.138 21H7.862a2 2 0 01-1.995-1.858L5 7m5 4v6m4-6v6m1-10V4a1 1 0 00-1-1h-4a1 1 0 00-1 1v3M4 7h16" />
                      </svg>
                    </div>
                    <div className="ml-5 w-0 flex-1">
                      <dl>
                        <dt className="text-sm font-medium text-gray-500 truncate">Article 17</dt>
                        <dd className="text-lg font-medium text-gray-900">Right to Erasure</dd>
                      </dl>
                      <p className="mt-1 text-xs text-gray-500">Users can request anonymization of their data.</p>
                    </div>
                  </div>
                </div>
              </div>

              <div className="bg-white overflow-hidden shadow rounded-lg">
                <div className="p-5">
                  <div className="flex items-center">
                    <div className="flex-shrink-0">
                      <svg className="h-6 w-6 text-green-400" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                        <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 12l2 2 4-4m5.618-4.016A11.955 11.955 0 0112 2.944a11.955 11.955 0 01-8.618 3.04A12.02 12.02 0 003 9c0 5.591 3.824 10.29 9 11.622 5.176-1.332 9-6.03 9-11.622 0-1.042-.133-2.052-.382-3.016z" />
                      </svg>
                    </div>
                    <div className="ml-5 w-0 flex-1">
                      <dl>
                        <dt className="text-sm font-medium text-gray-500 truncate">Article 7</dt>
                        <dd className="text-lg font-medium text-gray-900">Consent Management</dd>
                      </dl>
                      <p className="mt-1 text-xs text-gray-500">Track and manage user consent for OAuth2 clients.</p>
                    </div>
                  </div>
                </div>
              </div>
            </div>

            <div className="bg-white rounded-lg shadow-sm p-6">
              <h3 className="text-lg font-medium text-gray-900 mb-4">GDPR Compliance Status</h3>
              <div className="space-y-3">
                <div className="flex items-center gap-3">
                  <span className="inline-flex rounded-full px-2 py-0.5 text-xs font-semibold bg-green-100 text-green-800">Active</span>
                  <span className="text-sm text-gray-700">Consent records with audit trail</span>
                </div>
                <div className="flex items-center gap-3">
                  <span className="inline-flex rounded-full px-2 py-0.5 text-xs font-semibold bg-green-100 text-green-800">Active</span>
                  <span className="text-sm text-gray-700">Data export (right of access) processing</span>
                </div>
                <div className="flex items-center gap-3">
                  <span className="inline-flex rounded-full px-2 py-0.5 text-xs font-semibold bg-green-100 text-green-800">Active</span>
                  <span className="text-sm text-gray-700">Data anonymization (right to erasure) - preserves audit trail integrity</span>
                </div>
                <div className="flex items-center gap-3">
                  <span className="inline-flex rounded-full px-2 py-0.5 text-xs font-semibold bg-green-100 text-green-800">Active</span>
                  <span className="text-sm text-gray-700">48-hour expiring download links for exported data</span>
                </div>
              </div>
            </div>
          </div>
        )}
      </div>
    </DashboardLayout>
  );
}
