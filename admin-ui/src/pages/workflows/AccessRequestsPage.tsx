import { useState, useEffect } from 'react';
import DashboardLayout from '../../components/layout/DashboardLayout';
import { accessRequestApi } from '../../services/accessRequestApi';
import type { AccessRequest, AccessRequestDetail, CreateAccessRequestDto } from '../../services/accessRequestApi';

type TabType = 'my-requests' | 'approvals' | 'new-request';

export default function AccessRequestsPage() {
  const [activeTab, setActiveTab] = useState<TabType>('my-requests');
  const [myRequests, setMyRequests] = useState<AccessRequest[]>([]);
  const [pendingApprovals, setPendingApprovals] = useState<AccessRequest[]>([]);
  const [selectedRequest, setSelectedRequest] = useState<AccessRequestDetail | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [successMessage, setSuccessMessage] = useState('');
  const [actionComment, setActionComment] = useState('');
  const [processingId, setProcessingId] = useState<string | null>(null);
  const [showDetail, setShowDetail] = useState(false);

  // New request form state
  const [newRequest, setNewRequest] = useState<CreateAccessRequestDto>({
    resourceType: 'Role',
    justification: '',
    priority: 'Normal',
  });

  useEffect(() => {
    loadData();
  }, []);

  const loadData = async () => {
    try {
      setLoading(true);
      setError('');
      const [requests, approvals] = await Promise.all([
        accessRequestApi.getMyRequests(),
        accessRequestApi.getPendingApprovals(),
      ]);
      setMyRequests(requests);
      setPendingApprovals(approvals);
    } catch (err: any) {
      setError(err.response?.data?.error || 'Failed to load data');
    } finally {
      setLoading(false);
    }
  };

  const handleSubmitRequest = async (e: React.FormEvent) => {
    e.preventDefault();
    try {
      setError('');
      await accessRequestApi.createRequest(newRequest);
      setSuccessMessage('Access request submitted successfully');
      setNewRequest({ resourceType: 'Role', justification: '', priority: 'Normal' });
      setActiveTab('my-requests');
      setTimeout(() => setSuccessMessage(''), 3000);
      await loadData();
    } catch (err: any) {
      setError(err.response?.data?.error || 'Failed to submit request');
    }
  };

  const handleApprove = async (id: string) => {
    try {
      setProcessingId(id);
      setError('');
      await accessRequestApi.approve(id, actionComment || undefined);
      setSuccessMessage('Request approved');
      setActionComment('');
      setTimeout(() => setSuccessMessage(''), 3000);
      await loadData();
      setShowDetail(false);
    } catch (err: any) {
      setError(err.response?.data?.error || 'Failed to approve request');
    } finally {
      setProcessingId(null);
    }
  };

  const handleDeny = async (id: string) => {
    if (!confirm('Are you sure you want to deny this request?')) return;
    try {
      setProcessingId(id);
      setError('');
      await accessRequestApi.deny(id, actionComment || undefined);
      setSuccessMessage('Request denied');
      setActionComment('');
      setTimeout(() => setSuccessMessage(''), 3000);
      await loadData();
      setShowDetail(false);
    } catch (err: any) {
      setError(err.response?.data?.error || 'Failed to deny request');
    } finally {
      setProcessingId(null);
    }
  };

  const handleCancel = async (id: string) => {
    if (!confirm('Are you sure you want to cancel this request?')) return;
    try {
      setProcessingId(id);
      setError('');
      await accessRequestApi.cancel(id);
      setSuccessMessage('Request cancelled');
      setTimeout(() => setSuccessMessage(''), 3000);
      await loadData();
    } catch (err: any) {
      setError(err.response?.data?.error || 'Failed to cancel request');
    } finally {
      setProcessingId(null);
    }
  };

  const handleViewDetail = async (id: string) => {
    try {
      const detail = await accessRequestApi.getRequest(id);
      setSelectedRequest(detail);
      setShowDetail(true);
    } catch (err: any) {
      setError(err.response?.data?.error || 'Failed to load request details');
    }
  };

  const getStatusColor = (status: string) => {
    switch (status) {
      case 'Pending': return 'bg-yellow-100 text-yellow-800';
      case 'Approved': return 'bg-green-100 text-green-800';
      case 'Denied': return 'bg-red-100 text-red-800';
      case 'Expired': return 'bg-gray-100 text-gray-800';
      case 'Cancelled': return 'bg-gray-100 text-gray-500';
      default: return 'bg-gray-100 text-gray-800';
    }
  };

  const getPriorityColor = (priority: string) => {
    switch (priority) {
      case 'Critical': return 'bg-red-100 text-red-800';
      case 'High': return 'bg-orange-100 text-orange-800';
      case 'Normal': return 'bg-blue-100 text-blue-800';
      case 'Low': return 'bg-gray-100 text-gray-600';
      default: return 'bg-gray-100 text-gray-800';
    }
  };

  const getStepStatusColor = (status: string) => {
    switch (status) {
      case 'Pending': return 'bg-yellow-100 text-yellow-800';
      case 'Approved': return 'bg-green-100 text-green-800';
      case 'Denied': return 'bg-red-100 text-red-800';
      case 'Skipped': return 'bg-gray-100 text-gray-500';
      default: return 'bg-gray-100 text-gray-800';
    }
  };

  const renderRequestTable = (requests: AccessRequest[], showActions: boolean) => (
    <div className="overflow-hidden shadow ring-1 ring-black ring-opacity-5 rounded-lg">
      <table className="min-w-full divide-y divide-gray-300">
        <thead className="bg-gray-50">
          <tr>
            <th className="px-3 py-3.5 text-left text-sm font-semibold text-gray-900">Resource</th>
            <th className="px-3 py-3.5 text-left text-sm font-semibold text-gray-900">
              {showActions ? 'Requester' : 'Role'}
            </th>
            <th className="px-3 py-3.5 text-left text-sm font-semibold text-gray-900">Priority</th>
            <th className="px-3 py-3.5 text-left text-sm font-semibold text-gray-900">Status</th>
            <th className="px-3 py-3.5 text-left text-sm font-semibold text-gray-900">Created</th>
            <th className="px-3 py-3.5 text-left text-sm font-semibold text-gray-900">Actions</th>
          </tr>
        </thead>
        <tbody className="divide-y divide-gray-200 bg-white">
          {requests.length === 0 ? (
            <tr>
              <td colSpan={6} className="px-3 py-8 text-center text-sm text-gray-500">
                No requests found
              </td>
            </tr>
          ) : requests.map((req) => (
            <tr key={req.id} className="hover:bg-gray-50">
              <td className="whitespace-nowrap px-3 py-4 text-sm text-gray-900">
                {req.resourceType}
                {req.tenantName && <span className="text-gray-500 ml-1">({req.tenantName})</span>}
              </td>
              <td className="whitespace-nowrap px-3 py-4 text-sm text-gray-700">
                {showActions ? req.requesterName || req.requesterId : (req.roleName || '-')}
              </td>
              <td className="whitespace-nowrap px-3 py-4 text-sm">
                <span className={`inline-flex items-center rounded-full px-2.5 py-0.5 text-xs font-medium ${getPriorityColor(req.priority)}`}>
                  {req.priority}
                </span>
              </td>
              <td className="whitespace-nowrap px-3 py-4 text-sm">
                <span className={`inline-flex items-center rounded-full px-2.5 py-0.5 text-xs font-medium ${getStatusColor(req.status)}`}>
                  {req.status}
                </span>
                {req.stepsCount > 0 && req.status === 'Pending' && (
                  <span className="ml-1 text-xs text-gray-500">
                    Step {req.currentStep || 1}/{req.stepsCount}
                  </span>
                )}
              </td>
              <td className="whitespace-nowrap px-3 py-4 text-sm text-gray-500">
                {new Date(req.createdAt).toLocaleDateString()}
              </td>
              <td className="whitespace-nowrap px-3 py-4 text-sm">
                <button
                  onClick={() => handleViewDetail(req.id)}
                  className="text-indigo-600 hover:text-indigo-900 mr-2"
                >
                  View
                </button>
                {showActions && req.status === 'Pending' && (
                  <>
                    <button
                      onClick={() => handleApprove(req.id)}
                      disabled={processingId === req.id}
                      className="text-green-600 hover:text-green-900 mr-2 disabled:opacity-50"
                    >
                      Approve
                    </button>
                    <button
                      onClick={() => handleDeny(req.id)}
                      disabled={processingId === req.id}
                      className="text-red-600 hover:text-red-900 disabled:opacity-50"
                    >
                      Deny
                    </button>
                  </>
                )}
                {!showActions && req.status === 'Pending' && (
                  <button
                    onClick={() => handleCancel(req.id)}
                    disabled={processingId === req.id}
                    className="text-red-600 hover:text-red-900 disabled:opacity-50"
                  >
                    Cancel
                  </button>
                )}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );

  return (
    <DashboardLayout>
      <div className="px-4 sm:px-6 lg:px-8">
        {/* Header */}
        <div className="sm:flex sm:items-center">
          <div className="sm:flex-auto">
            <h1 className="text-2xl font-semibold text-gray-900">Access Requests</h1>
            <p className="mt-2 text-sm text-gray-700">
              Submit access requests, review pending approvals, and track request status.
            </p>
          </div>
        </div>

        {/* Alerts */}
        {error && (
          <div className="mt-4 rounded-md bg-red-50 p-4">
            <p className="text-sm text-red-700">{error}</p>
          </div>
        )}
        {successMessage && (
          <div className="mt-4 rounded-md bg-green-50 p-4">
            <p className="text-sm text-green-700">{successMessage}</p>
          </div>
        )}

        {/* Tabs */}
        <div className="mt-6 border-b border-gray-200">
          <nav className="-mb-px flex space-x-8">
            {[
              { key: 'my-requests' as TabType, label: 'My Requests', count: myRequests.length },
              { key: 'approvals' as TabType, label: 'Pending Approvals', count: pendingApprovals.length },
              { key: 'new-request' as TabType, label: 'New Request' },
            ].map((tab) => (
              <button
                key={tab.key}
                onClick={() => setActiveTab(tab.key)}
                className={`whitespace-nowrap border-b-2 py-4 px-1 text-sm font-medium ${
                  activeTab === tab.key
                    ? 'border-indigo-500 text-indigo-600'
                    : 'border-transparent text-gray-500 hover:border-gray-300 hover:text-gray-700'
                }`}
              >
                {tab.label}
                {tab.count !== undefined && (
                  <span className={`ml-2 inline-flex items-center rounded-full px-2.5 py-0.5 text-xs font-medium ${
                    activeTab === tab.key ? 'bg-indigo-100 text-indigo-600' : 'bg-gray-100 text-gray-900'
                  }`}>
                    {tab.count}
                  </span>
                )}
              </button>
            ))}
          </nav>
        </div>

        {/* Tab Content */}
        <div className="mt-6">
          {loading ? (
            <div className="text-center py-12">
              <div className="animate-spin rounded-full h-8 w-8 border-b-2 border-indigo-600 mx-auto"></div>
              <p className="mt-2 text-sm text-gray-500">Loading...</p>
            </div>
          ) : (
            <>
              {activeTab === 'my-requests' && renderRequestTable(myRequests, false)}

              {activeTab === 'approvals' && renderRequestTable(pendingApprovals, true)}

              {activeTab === 'new-request' && (
                <div className="max-w-2xl">
                  <form onSubmit={handleSubmitRequest} className="space-y-6">
                    <div>
                      <label className="block text-sm font-medium text-gray-700">Resource Type</label>
                      <select
                        value={newRequest.resourceType}
                        onChange={(e) => setNewRequest({ ...newRequest, resourceType: e.target.value })}
                        className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                      >
                        <option value="Role">Role</option>
                        <option value="Building">Building</option>
                        <option value="Device">Device</option>
                        <option value="Permission">Permission</option>
                        <option value="Other">Other</option>
                      </select>
                    </div>

                    <div>
                      <label className="block text-sm font-medium text-gray-700">Role ID (optional)</label>
                      <input
                        type="text"
                        value={newRequest.roleId || ''}
                        onChange={(e) => setNewRequest({ ...newRequest, roleId: e.target.value || undefined })}
                        className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                        placeholder="UUID of the role to request"
                      />
                    </div>

                    <div>
                      <label className="block text-sm font-medium text-gray-700">Resource ID (optional)</label>
                      <input
                        type="text"
                        value={newRequest.resourceId || ''}
                        onChange={(e) => setNewRequest({ ...newRequest, resourceId: e.target.value || undefined })}
                        className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                        placeholder="UUID of the specific resource"
                      />
                    </div>

                    <div>
                      <label className="block text-sm font-medium text-gray-700">Tenant ID (optional)</label>
                      <input
                        type="text"
                        value={newRequest.tenantId || ''}
                        onChange={(e) => setNewRequest({ ...newRequest, tenantId: e.target.value || undefined })}
                        className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                        placeholder="UUID of the tenant context"
                      />
                    </div>

                    <div>
                      <label className="block text-sm font-medium text-gray-700">Priority</label>
                      <select
                        value={newRequest.priority}
                        onChange={(e) => setNewRequest({ ...newRequest, priority: e.target.value })}
                        className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                      >
                        <option value="Low">Low</option>
                        <option value="Normal">Normal</option>
                        <option value="High">High</option>
                        <option value="Critical">Critical</option>
                      </select>
                    </div>

                    <div>
                      <label className="block text-sm font-medium text-gray-700">Justification</label>
                      <textarea
                        required
                        rows={4}
                        value={newRequest.justification}
                        onChange={(e) => setNewRequest({ ...newRequest, justification: e.target.value })}
                        className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                        placeholder="Explain why you need this access..."
                      />
                    </div>

                    <div>
                      <button
                        type="submit"
                        className="inline-flex justify-center rounded-md border border-transparent bg-indigo-600 py-2 px-4 text-sm font-medium text-white shadow-sm hover:bg-indigo-700 focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:ring-offset-2"
                      >
                        Submit Request
                      </button>
                    </div>
                  </form>
                </div>
              )}
            </>
          )}
        </div>

        {/* Detail Modal */}
        {showDetail && selectedRequest && (
          <div className="fixed inset-0 z-50 overflow-y-auto">
            <div className="flex items-center justify-center min-h-screen px-4">
              <div className="fixed inset-0 bg-gray-500 bg-opacity-75" onClick={() => setShowDetail(false)} />
              <div className="relative bg-white rounded-lg shadow-xl max-w-3xl w-full p-6 z-10">
                <div className="flex justify-between items-start">
                  <h3 className="text-lg font-semibold text-gray-900">
                    Access Request Details
                  </h3>
                  <button
                    onClick={() => setShowDetail(false)}
                    className="text-gray-400 hover:text-gray-500"
                  >
                    <span className="text-2xl">&times;</span>
                  </button>
                </div>

                <div className="mt-4 space-y-4">
                  <div className="grid grid-cols-2 gap-4 text-sm">
                    <div>
                      <span className="font-medium text-gray-500">Requester:</span>{' '}
                      <span className="text-gray-900">{selectedRequest.requesterName}</span>
                    </div>
                    <div>
                      <span className="font-medium text-gray-500">Status:</span>{' '}
                      <span className={`inline-flex items-center rounded-full px-2.5 py-0.5 text-xs font-medium ${getStatusColor(selectedRequest.status)}`}>
                        {selectedRequest.status}
                      </span>
                    </div>
                    <div>
                      <span className="font-medium text-gray-500">Resource Type:</span>{' '}
                      <span className="text-gray-900">{selectedRequest.resourceType}</span>
                    </div>
                    <div>
                      <span className="font-medium text-gray-500">Priority:</span>{' '}
                      <span className={`inline-flex items-center rounded-full px-2.5 py-0.5 text-xs font-medium ${getPriorityColor(selectedRequest.priority)}`}>
                        {selectedRequest.priority}
                      </span>
                    </div>
                    {selectedRequest.roleName && (
                      <div>
                        <span className="font-medium text-gray-500">Role:</span>{' '}
                        <span className="text-gray-900">{selectedRequest.roleName}</span>
                      </div>
                    )}
                    {selectedRequest.tenantName && (
                      <div>
                        <span className="font-medium text-gray-500">Tenant:</span>{' '}
                        <span className="text-gray-900">{selectedRequest.tenantName}</span>
                      </div>
                    )}
                    {selectedRequest.workflowTemplateName && (
                      <div>
                        <span className="font-medium text-gray-500">Workflow:</span>{' '}
                        <span className="text-gray-900">{selectedRequest.workflowTemplateName}</span>
                      </div>
                    )}
                    {selectedRequest.expiresAt && (
                      <div>
                        <span className="font-medium text-gray-500">Expires:</span>{' '}
                        <span className="text-gray-900">{new Date(selectedRequest.expiresAt).toLocaleString()}</span>
                      </div>
                    )}
                  </div>

                  <div>
                    <span className="font-medium text-gray-500 text-sm">Justification:</span>
                    <p className="mt-1 text-sm text-gray-900 bg-gray-50 rounded p-3">
                      {selectedRequest.justification}
                    </p>
                  </div>

                  {/* Approval Steps Timeline */}
                  {selectedRequest.approvalSteps && selectedRequest.approvalSteps.length > 0 && (
                    <div>
                      <h4 className="font-medium text-gray-900 text-sm mb-3">Approval Chain</h4>
                      <div className="space-y-3">
                        {selectedRequest.approvalSteps.map((step) => (
                          <div key={step.id} className="flex items-start space-x-3 bg-gray-50 rounded p-3">
                            <div className="flex-shrink-0">
                              <span className="inline-flex items-center justify-center h-8 w-8 rounded-full bg-gray-200 text-sm font-medium text-gray-600">
                                {step.stepOrder}
                              </span>
                            </div>
                            <div className="flex-1 min-w-0">
                              <div className="flex items-center space-x-2">
                                <span className={`inline-flex items-center rounded-full px-2 py-0.5 text-xs font-medium ${getStepStatusColor(step.status)}`}>
                                  {step.status}
                                </span>
                                <span className="text-sm text-gray-500">
                                  {step.approverName || step.approverRoleName || 'Unassigned'}
                                </span>
                                {step.quorumCount > 1 && (
                                  <span className="text-xs text-gray-400">
                                    ({step.approvalsReceived}/{step.quorumCount} quorum)
                                  </span>
                                )}
                              </div>
                              {step.decidedByUserName && (
                                <p className="text-xs text-gray-500 mt-1">
                                  Decided by {step.decidedByUserName} on {new Date(step.decidedAt!).toLocaleString()}
                                </p>
                              )}
                              {step.comment && (
                                <p className="text-xs text-gray-600 mt-1 italic">"{step.comment}"</p>
                              )}
                            </div>
                          </div>
                        ))}
                      </div>
                    </div>
                  )}

                  {/* Action area for approvers */}
                  {selectedRequest.status === 'Pending' && (
                    <div className="border-t pt-4">
                      <div className="mb-3">
                        <label className="block text-sm font-medium text-gray-700 mb-1">Comment (optional)</label>
                        <textarea
                          rows={2}
                          value={actionComment}
                          onChange={(e) => setActionComment(e.target.value)}
                          className="block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                          placeholder="Add a comment..."
                        />
                      </div>
                      <div className="flex space-x-3">
                        <button
                          onClick={() => handleApprove(selectedRequest.id)}
                          disabled={processingId === selectedRequest.id}
                          className="inline-flex justify-center rounded-md bg-green-600 px-4 py-2 text-sm font-medium text-white hover:bg-green-700 disabled:opacity-50"
                        >
                          Approve
                        </button>
                        <button
                          onClick={() => handleDeny(selectedRequest.id)}
                          disabled={processingId === selectedRequest.id}
                          className="inline-flex justify-center rounded-md bg-red-600 px-4 py-2 text-sm font-medium text-white hover:bg-red-700 disabled:opacity-50"
                        >
                          Deny
                        </button>
                      </div>
                    </div>
                  )}
                </div>
              </div>
            </div>
          </div>
        )}
      </div>
    </DashboardLayout>
  );
}
