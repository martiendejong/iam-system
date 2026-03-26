import { useState, useEffect } from 'react';
import DashboardLayout from '../../components/layout/DashboardLayout';
import { pamApi } from '../../services/pamApi';
import type { PrivilegedSession, PamPolicy, PamCheckoutRequest, PamBreakGlassRequest, PamPolicyRequest } from '../../services/pamApi';

type TabType = 'active' | 'checkout' | 'break-glass' | 'history' | 'policies';

export default function PamDashboardPage() {
  const [activeTab, setActiveTab] = useState<TabType>('active');
  const [activeSessions, setActiveSessions] = useState<PrivilegedSession[]>([]);
  const [historyItems, setHistoryItems] = useState<PrivilegedSession[]>([]);
  const [policies, setPolicies] = useState<PamPolicy[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [successMessage, setSuccessMessage] = useState('');
  const [processingId, setProcessingId] = useState<string | null>(null);

  // Checkout form
  const [checkoutForm, setCheckoutForm] = useState<PamCheckoutRequest>({
    roleId: '',
    tenantId: '',
    justification: '',
    durationMinutes: 60,
  });

  // Break-glass form
  const [breakGlassForm, setBreakGlassForm] = useState<PamBreakGlassRequest>({
    roleId: '',
    tenantId: '',
    justification: '',
    approverUserIds: [],
    durationMinutes: 60,
  });
  const [approverInput, setApproverInput] = useState('');

  // Policy form
  const [showPolicyForm, setShowPolicyForm] = useState(false);
  const [editingPolicyId, setEditingPolicyId] = useState<string | null>(null);
  const [policyForm, setPolicyForm] = useState<PamPolicyRequest>({
    tenantId: '',
    roleId: '',
    maxDurationMinutes: 480,
    requireJustification: true,
    requireApproval: true,
    breakGlassEnabled: false,
    breakGlassApproversRequired: 2,
    isActive: true,
  });

  useEffect(() => {
    loadData();
  }, []);

  const loadData = async () => {
    try {
      setLoading(true);
      setError('');
      const [sessions, history, pols] = await Promise.all([
        pamApi.getActiveSessions(),
        pamApi.getSessionHistory({ take: 50 }),
        pamApi.getPolicies(),
      ]);
      setActiveSessions(sessions);
      setHistoryItems(history);
      setPolicies(pols);
    } catch (err: any) {
      setError(err.response?.data?.error || 'Failed to load PAM data');
    } finally {
      setLoading(false);
    }
  };

  const showSuccess = (msg: string) => {
    setSuccessMessage(msg);
    setTimeout(() => setSuccessMessage(''), 4000);
  };

  // --- Session Actions ---
  const handleCheckin = async (sessionId: string) => {
    try {
      setProcessingId(sessionId);
      setError('');
      await pamApi.checkin(sessionId);
      showSuccess('Privileged access checked in successfully');
      await loadData();
    } catch (err: any) {
      setError(err.response?.data?.error || 'Failed to check in');
    } finally {
      setProcessingId(null);
    }
  };

  const handleApprove = async (sessionId: string) => {
    try {
      setProcessingId(sessionId);
      setError('');
      await pamApi.approveSession(sessionId);
      showSuccess('Session approved');
      await loadData();
    } catch (err: any) {
      setError(err.response?.data?.error || 'Failed to approve session');
    } finally {
      setProcessingId(null);
    }
  };

  const handleDeny = async (sessionId: string) => {
    try {
      setProcessingId(sessionId);
      setError('');
      await pamApi.denySession(sessionId);
      showSuccess('Session denied');
      await loadData();
    } catch (err: any) {
      setError(err.response?.data?.error || 'Failed to deny session');
    } finally {
      setProcessingId(null);
    }
  };

  // --- Checkout ---
  const handleCheckout = async (e: React.FormEvent) => {
    e.preventDefault();
    try {
      setError('');
      await pamApi.checkout(checkoutForm);
      showSuccess('Privileged access checkout requested');
      setCheckoutForm({ roleId: '', tenantId: '', justification: '', durationMinutes: 60 });
      setActiveTab('active');
      await loadData();
    } catch (err: any) {
      setError(err.response?.data?.error || 'Failed to checkout');
    }
  };

  // --- Break-Glass ---
  const addApprover = () => {
    const trimmed = approverInput.trim();
    if (trimmed && !breakGlassForm.approverUserIds.includes(trimmed)) {
      setBreakGlassForm({
        ...breakGlassForm,
        approverUserIds: [...breakGlassForm.approverUserIds, trimmed],
      });
      setApproverInput('');
    }
  };

  const removeApprover = (id: string) => {
    setBreakGlassForm({
      ...breakGlassForm,
      approverUserIds: breakGlassForm.approverUserIds.filter(a => a !== id),
    });
  };

  const handleBreakGlass = async (e: React.FormEvent) => {
    e.preventDefault();
    try {
      setError('');
      await pamApi.breakGlass(breakGlassForm);
      showSuccess('Break-glass emergency access granted');
      setBreakGlassForm({ roleId: '', tenantId: '', justification: '', approverUserIds: [], durationMinutes: 60 });
      setActiveTab('active');
      await loadData();
    } catch (err: any) {
      setError(err.response?.data?.error || 'Failed to activate break-glass access');
    }
  };

  // --- Policy CRUD ---
  const handleCreatePolicy = async (e: React.FormEvent) => {
    e.preventDefault();
    try {
      setError('');
      if (editingPolicyId) {
        await pamApi.updatePolicy(editingPolicyId, policyForm);
        showSuccess('Policy updated');
      } else {
        await pamApi.createPolicy(policyForm);
        showSuccess('Policy created');
      }
      setShowPolicyForm(false);
      setEditingPolicyId(null);
      resetPolicyForm();
      await loadData();
    } catch (err: any) {
      setError(err.response?.data?.error || 'Failed to save policy');
    }
  };

  const handleEditPolicy = (policy: PamPolicy) => {
    setEditingPolicyId(policy.id);
    setPolicyForm({
      tenantId: policy.tenantId,
      roleId: policy.roleId,
      maxDurationMinutes: policy.maxDurationMinutes,
      requireJustification: policy.requireJustification,
      requireApproval: policy.requireApproval,
      approverRoleId: policy.approverRoleId,
      breakGlassEnabled: policy.breakGlassEnabled,
      breakGlassApproversRequired: policy.breakGlassApproversRequired,
      isActive: policy.isActive,
    });
    setShowPolicyForm(true);
  };

  const handleDeletePolicy = async (policyId: string) => {
    if (!confirm('Delete this PAM policy?')) return;
    try {
      setError('');
      await pamApi.deletePolicy(policyId);
      showSuccess('Policy deleted');
      await loadData();
    } catch (err: any) {
      setError(err.response?.data?.error || 'Failed to delete policy');
    }
  };

  const resetPolicyForm = () => {
    setPolicyForm({
      tenantId: '',
      roleId: '',
      maxDurationMinutes: 480,
      requireJustification: true,
      requireApproval: true,
      breakGlassEnabled: false,
      breakGlassApproversRequired: 2,
      isActive: true,
    });
  };

  // --- Helpers ---
  const statusColor = (status: string) => {
    switch (status) {
      case 'Active': return 'bg-green-100 text-green-800';
      case 'Pending': return 'bg-yellow-100 text-yellow-800';
      case 'Expired': return 'bg-gray-100 text-gray-800';
      case 'CheckedIn': return 'bg-blue-100 text-blue-800';
      case 'Denied': return 'bg-red-100 text-red-800';
      case 'Revoked': return 'bg-red-100 text-red-800';
      default: return 'bg-gray-100 text-gray-800';
    }
  };

  const timeRemaining = (expiresAt: string) => {
    const diff = new Date(expiresAt).getTime() - Date.now();
    if (diff <= 0) return 'Expired';
    const mins = Math.floor(diff / 60000);
    if (mins < 60) return `${mins}m remaining`;
    const hrs = Math.floor(mins / 60);
    return `${hrs}h ${mins % 60}m remaining`;
  };

  const tabClasses = (tab: TabType) =>
    `px-4 py-2 text-sm font-medium rounded-t-lg cursor-pointer ${
      activeTab === tab
        ? 'bg-white text-indigo-600 border-b-2 border-indigo-600'
        : 'text-gray-500 hover:text-gray-700 hover:bg-gray-50'
    }`;

  return (
    <DashboardLayout>
      <div className="space-y-6">
        <div className="flex justify-between items-center">
          <div>
            <h1 className="text-2xl font-bold text-gray-900">Privileged Access Management</h1>
            <p className="text-sm text-gray-500">Time-boxed privilege elevation with check-out/check-in and break-glass emergency access</p>
          </div>
          <div className="flex items-center space-x-2">
            <span className="inline-flex items-center px-3 py-1 rounded-full text-sm font-medium bg-green-100 text-green-800">
              {activeSessions.filter(s => s.status === 'Active').length} Active Sessions
            </span>
            <span className="inline-flex items-center px-3 py-1 rounded-full text-sm font-medium bg-yellow-100 text-yellow-800">
              {activeSessions.filter(s => s.status === 'Pending').length} Pending Approval
            </span>
          </div>
        </div>

        {/* Messages */}
        {error && (
          <div className="bg-red-50 border border-red-200 text-red-700 px-4 py-3 rounded-lg">{error}</div>
        )}
        {successMessage && (
          <div className="bg-green-50 border border-green-200 text-green-700 px-4 py-3 rounded-lg">{successMessage}</div>
        )}

        {/* Tabs */}
        <div className="flex space-x-1 border-b border-gray-200">
          <button className={tabClasses('active')} onClick={() => setActiveTab('active')}>Active Sessions</button>
          <button className={tabClasses('checkout')} onClick={() => setActiveTab('checkout')}>Checkout</button>
          <button className={tabClasses('break-glass')} onClick={() => setActiveTab('break-glass')}>Break-Glass</button>
          <button className={tabClasses('history')} onClick={() => setActiveTab('history')}>History</button>
          <button className={tabClasses('policies')} onClick={() => setActiveTab('policies')}>Policies</button>
        </div>

        {loading ? (
          <div className="text-center py-12 text-gray-500">Loading PAM data...</div>
        ) : (
          <>
            {/* Active Sessions Tab */}
            {activeTab === 'active' && (
              <div className="bg-white shadow rounded-lg overflow-hidden">
                <table className="min-w-full divide-y divide-gray-200">
                  <thead className="bg-gray-50">
                    <tr>
                      <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">User</th>
                      <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">Role</th>
                      <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">Status</th>
                      <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">Time Remaining</th>
                      <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">Type</th>
                      <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">Justification</th>
                      <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">Actions</th>
                    </tr>
                  </thead>
                  <tbody className="bg-white divide-y divide-gray-200">
                    {activeSessions.length === 0 ? (
                      <tr>
                        <td colSpan={7} className="px-6 py-8 text-center text-gray-500">No active privileged sessions</td>
                      </tr>
                    ) : activeSessions.map((session) => (
                      <tr key={session.id} className={session.isBreakGlass ? 'bg-red-50' : ''}>
                        <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-900">
                          {session.user ? `${session.user.firstName} ${session.user.lastName}` : session.userId.substring(0, 8)}
                        </td>
                        <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-900">
                          {session.role?.name || session.roleId.substring(0, 8)}
                        </td>
                        <td className="px-6 py-4 whitespace-nowrap">
                          <span className={`inline-flex px-2 py-1 text-xs font-semibold rounded-full ${statusColor(session.status)}`}>
                            {session.status}
                          </span>
                        </td>
                        <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-500">
                          {timeRemaining(session.expiresAt)}
                        </td>
                        <td className="px-6 py-4 whitespace-nowrap text-sm">
                          {session.isBreakGlass ? (
                            <span className="inline-flex px-2 py-1 text-xs font-semibold rounded-full bg-red-100 text-red-800">BREAK-GLASS</span>
                          ) : (
                            <span className="text-gray-500">Standard</span>
                          )}
                        </td>
                        <td className="px-6 py-4 text-sm text-gray-500 max-w-xs truncate" title={session.justification}>
                          {session.justification}
                        </td>
                        <td className="px-6 py-4 whitespace-nowrap text-sm space-x-2">
                          {session.status === 'Active' && (
                            <button
                              onClick={() => handleCheckin(session.id)}
                              disabled={processingId === session.id}
                              className="text-blue-600 hover:text-blue-900 font-medium disabled:opacity-50"
                            >
                              Check In
                            </button>
                          )}
                          {session.status === 'Pending' && (
                            <>
                              <button
                                onClick={() => handleApprove(session.id)}
                                disabled={processingId === session.id}
                                className="text-green-600 hover:text-green-900 font-medium disabled:opacity-50"
                              >
                                Approve
                              </button>
                              <button
                                onClick={() => handleDeny(session.id)}
                                disabled={processingId === session.id}
                                className="text-red-600 hover:text-red-900 font-medium disabled:opacity-50"
                              >
                                Deny
                              </button>
                            </>
                          )}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}

            {/* Checkout Tab */}
            {activeTab === 'checkout' && (
              <div className="bg-white shadow rounded-lg p-6">
                <h2 className="text-lg font-semibold text-gray-900 mb-4">Request Privileged Access</h2>
                <form onSubmit={handleCheckout} className="space-y-4 max-w-lg">
                  <div>
                    <label className="block text-sm font-medium text-gray-700">Role ID</label>
                    <input
                      type="text"
                      required
                      value={checkoutForm.roleId}
                      onChange={e => setCheckoutForm({ ...checkoutForm, roleId: e.target.value })}
                      className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                      placeholder="GUID of the privileged role"
                    />
                  </div>
                  <div>
                    <label className="block text-sm font-medium text-gray-700">Tenant ID</label>
                    <input
                      type="text"
                      required
                      value={checkoutForm.tenantId}
                      onChange={e => setCheckoutForm({ ...checkoutForm, tenantId: e.target.value })}
                      className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                      placeholder="GUID of the tenant"
                    />
                  </div>
                  <div>
                    <label className="block text-sm font-medium text-gray-700">Duration (minutes)</label>
                    <input
                      type="number"
                      min={1}
                      max={1440}
                      value={checkoutForm.durationMinutes ?? 60}
                      onChange={e => setCheckoutForm({ ...checkoutForm, durationMinutes: parseInt(e.target.value) || 60 })}
                      className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                    />
                  </div>
                  <div>
                    <label className="block text-sm font-medium text-gray-700">Justification</label>
                    <textarea
                      required
                      rows={3}
                      value={checkoutForm.justification}
                      onChange={e => setCheckoutForm({ ...checkoutForm, justification: e.target.value })}
                      className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                      placeholder="Why do you need this privileged access?"
                    />
                  </div>
                  <button
                    type="submit"
                    className="inline-flex items-center px-4 py-2 border border-transparent text-sm font-medium rounded-md text-white bg-indigo-600 hover:bg-indigo-700"
                  >
                    Request Checkout
                  </button>
                </form>
              </div>
            )}

            {/* Break-Glass Tab */}
            {activeTab === 'break-glass' && (
              <div className="bg-white shadow rounded-lg p-6">
                <div className="mb-4 p-4 bg-red-50 border border-red-200 rounded-lg">
                  <h2 className="text-lg font-semibold text-red-800">Emergency Break-Glass Access</h2>
                  <p className="text-sm text-red-600 mt-1">
                    This grants immediate privileged access with the 4-eyes principle (minimum 2 approvers required).
                    All break-glass access is logged and audited. Use only in genuine emergencies.
                  </p>
                </div>
                <form onSubmit={handleBreakGlass} className="space-y-4 max-w-lg">
                  <div>
                    <label className="block text-sm font-medium text-gray-700">Role ID</label>
                    <input
                      type="text"
                      required
                      value={breakGlassForm.roleId}
                      onChange={e => setBreakGlassForm({ ...breakGlassForm, roleId: e.target.value })}
                      className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                      placeholder="GUID of the privileged role"
                    />
                  </div>
                  <div>
                    <label className="block text-sm font-medium text-gray-700">Tenant ID</label>
                    <input
                      type="text"
                      required
                      value={breakGlassForm.tenantId}
                      onChange={e => setBreakGlassForm({ ...breakGlassForm, tenantId: e.target.value })}
                      className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                      placeholder="GUID of the tenant"
                    />
                  </div>
                  <div>
                    <label className="block text-sm font-medium text-gray-700">Duration (minutes)</label>
                    <input
                      type="number"
                      min={1}
                      max={1440}
                      value={breakGlassForm.durationMinutes ?? 60}
                      onChange={e => setBreakGlassForm({ ...breakGlassForm, durationMinutes: parseInt(e.target.value) || 60 })}
                      className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                    />
                  </div>
                  <div>
                    <label className="block text-sm font-medium text-gray-700">Justification (Emergency Reason)</label>
                    <textarea
                      required
                      rows={3}
                      value={breakGlassForm.justification}
                      onChange={e => setBreakGlassForm({ ...breakGlassForm, justification: e.target.value })}
                      className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:border-red-500 focus:ring-red-500 sm:text-sm"
                      placeholder="Describe the emergency situation requiring break-glass access"
                    />
                  </div>
                  <div>
                    <label className="block text-sm font-medium text-gray-700">
                      Approvers (min 2 - 4-eyes principle)
                    </label>
                    <div className="flex space-x-2 mt-1">
                      <input
                        type="text"
                        value={approverInput}
                        onChange={e => setApproverInput(e.target.value)}
                        className="block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                        placeholder="Approver User ID (GUID)"
                      />
                      <button
                        type="button"
                        onClick={addApprover}
                        className="inline-flex items-center px-3 py-2 border border-gray-300 text-sm rounded-md text-gray-700 bg-white hover:bg-gray-50"
                      >
                        Add
                      </button>
                    </div>
                    <div className="mt-2 space-y-1">
                      {breakGlassForm.approverUserIds.map((id, idx) => (
                        <div key={idx} className="flex items-center justify-between bg-gray-50 px-3 py-1 rounded text-sm">
                          <span className="font-mono text-gray-600">{id}</span>
                          <button
                            type="button"
                            onClick={() => removeApprover(id)}
                            className="text-red-500 hover:text-red-700 text-xs"
                          >
                            Remove
                          </button>
                        </div>
                      ))}
                    </div>
                    {breakGlassForm.approverUserIds.length < 2 && (
                      <p className="mt-1 text-xs text-red-500">At least 2 approvers required</p>
                    )}
                  </div>
                  <button
                    type="submit"
                    disabled={breakGlassForm.approverUserIds.length < 2}
                    className="inline-flex items-center px-4 py-2 border border-transparent text-sm font-medium rounded-md text-white bg-red-600 hover:bg-red-700 disabled:opacity-50 disabled:cursor-not-allowed"
                  >
                    Activate Break-Glass Access
                  </button>
                </form>
              </div>
            )}

            {/* History Tab */}
            {activeTab === 'history' && (
              <div className="bg-white shadow rounded-lg overflow-hidden">
                <table className="min-w-full divide-y divide-gray-200">
                  <thead className="bg-gray-50">
                    <tr>
                      <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">User</th>
                      <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">Role</th>
                      <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">Status</th>
                      <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">Type</th>
                      <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">Checked Out</th>
                      <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">Checked In</th>
                      <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">Justification</th>
                    </tr>
                  </thead>
                  <tbody className="bg-white divide-y divide-gray-200">
                    {historyItems.length === 0 ? (
                      <tr>
                        <td colSpan={7} className="px-6 py-8 text-center text-gray-500">No session history</td>
                      </tr>
                    ) : historyItems.map((session) => (
                      <tr key={session.id} className={session.isBreakGlass ? 'bg-red-50' : ''}>
                        <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-900">
                          {session.user ? `${session.user.firstName} ${session.user.lastName}` : session.userId.substring(0, 8)}
                        </td>
                        <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-900">
                          {session.role?.name || session.roleId.substring(0, 8)}
                        </td>
                        <td className="px-6 py-4 whitespace-nowrap">
                          <span className={`inline-flex px-2 py-1 text-xs font-semibold rounded-full ${statusColor(session.status)}`}>
                            {session.status}
                          </span>
                        </td>
                        <td className="px-6 py-4 whitespace-nowrap text-sm">
                          {session.isBreakGlass ? (
                            <span className="inline-flex px-2 py-1 text-xs font-semibold rounded-full bg-red-100 text-red-800">BREAK-GLASS</span>
                          ) : (
                            <span className="text-gray-500">Standard</span>
                          )}
                        </td>
                        <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-500">
                          {session.checkedOutAt ? new Date(session.checkedOutAt).toLocaleString() : '-'}
                        </td>
                        <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-500">
                          {session.checkedInAt ? new Date(session.checkedInAt).toLocaleString() : '-'}
                        </td>
                        <td className="px-6 py-4 text-sm text-gray-500 max-w-xs truncate" title={session.justification}>
                          {session.justification}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}

            {/* Policies Tab */}
            {activeTab === 'policies' && (
              <div className="space-y-4">
                <div className="flex justify-end">
                  <button
                    onClick={() => { resetPolicyForm(); setEditingPolicyId(null); setShowPolicyForm(true); }}
                    className="inline-flex items-center px-4 py-2 border border-transparent text-sm font-medium rounded-md text-white bg-indigo-600 hover:bg-indigo-700"
                  >
                    Create Policy
                  </button>
                </div>

                {showPolicyForm && (
                  <div className="bg-white shadow rounded-lg p-6">
                    <h3 className="text-lg font-semibold text-gray-900 mb-4">
                      {editingPolicyId ? 'Edit PAM Policy' : 'Create PAM Policy'}
                    </h3>
                    <form onSubmit={handleCreatePolicy} className="space-y-4 max-w-lg">
                      {!editingPolicyId && (
                        <>
                          <div>
                            <label className="block text-sm font-medium text-gray-700">Tenant ID</label>
                            <input
                              type="text"
                              required
                              value={policyForm.tenantId}
                              onChange={e => setPolicyForm({ ...policyForm, tenantId: e.target.value })}
                              className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                            />
                          </div>
                          <div>
                            <label className="block text-sm font-medium text-gray-700">Role ID</label>
                            <input
                              type="text"
                              required
                              value={policyForm.roleId}
                              onChange={e => setPolicyForm({ ...policyForm, roleId: e.target.value })}
                              className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                            />
                          </div>
                        </>
                      )}
                      <div>
                        <label className="block text-sm font-medium text-gray-700">Max Duration (minutes)</label>
                        <input
                          type="number"
                          min={1}
                          max={1440}
                          required
                          value={policyForm.maxDurationMinutes}
                          onChange={e => setPolicyForm({ ...policyForm, maxDurationMinutes: parseInt(e.target.value) || 480 })}
                          className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                        />
                      </div>
                      <div className="flex items-center space-x-6">
                        <label className="flex items-center">
                          <input
                            type="checkbox"
                            checked={policyForm.requireJustification}
                            onChange={e => setPolicyForm({ ...policyForm, requireJustification: e.target.checked })}
                            className="rounded border-gray-300 text-indigo-600 focus:ring-indigo-500"
                          />
                          <span className="ml-2 text-sm text-gray-700">Require Justification</span>
                        </label>
                        <label className="flex items-center">
                          <input
                            type="checkbox"
                            checked={policyForm.requireApproval}
                            onChange={e => setPolicyForm({ ...policyForm, requireApproval: e.target.checked })}
                            className="rounded border-gray-300 text-indigo-600 focus:ring-indigo-500"
                          />
                          <span className="ml-2 text-sm text-gray-700">Require Approval</span>
                        </label>
                        <label className="flex items-center">
                          <input
                            type="checkbox"
                            checked={policyForm.isActive}
                            onChange={e => setPolicyForm({ ...policyForm, isActive: e.target.checked })}
                            className="rounded border-gray-300 text-indigo-600 focus:ring-indigo-500"
                          />
                          <span className="ml-2 text-sm text-gray-700">Active</span>
                        </label>
                      </div>
                      <div className="flex items-center space-x-4">
                        <label className="flex items-center">
                          <input
                            type="checkbox"
                            checked={policyForm.breakGlassEnabled}
                            onChange={e => setPolicyForm({ ...policyForm, breakGlassEnabled: e.target.checked })}
                            className="rounded border-gray-300 text-red-600 focus:ring-red-500"
                          />
                          <span className="ml-2 text-sm text-gray-700">Enable Break-Glass</span>
                        </label>
                        {policyForm.breakGlassEnabled && (
                          <div>
                            <label className="text-sm text-gray-700">Approvers Required: </label>
                            <input
                              type="number"
                              min={2}
                              max={10}
                              value={policyForm.breakGlassApproversRequired}
                              onChange={e => setPolicyForm({ ...policyForm, breakGlassApproversRequired: parseInt(e.target.value) || 2 })}
                              className="w-16 rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                            />
                          </div>
                        )}
                      </div>
                      <div className="flex space-x-2">
                        <button
                          type="submit"
                          className="inline-flex items-center px-4 py-2 border border-transparent text-sm font-medium rounded-md text-white bg-indigo-600 hover:bg-indigo-700"
                        >
                          {editingPolicyId ? 'Update Policy' : 'Create Policy'}
                        </button>
                        <button
                          type="button"
                          onClick={() => { setShowPolicyForm(false); setEditingPolicyId(null); }}
                          className="inline-flex items-center px-4 py-2 border border-gray-300 text-sm font-medium rounded-md text-gray-700 bg-white hover:bg-gray-50"
                        >
                          Cancel
                        </button>
                      </div>
                    </form>
                  </div>
                )}

                <div className="bg-white shadow rounded-lg overflow-hidden">
                  <table className="min-w-full divide-y divide-gray-200">
                    <thead className="bg-gray-50">
                      <tr>
                        <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">Role</th>
                        <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">Max Duration</th>
                        <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">Justification</th>
                        <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">Approval</th>
                        <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">Break-Glass</th>
                        <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">Status</th>
                        <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">Actions</th>
                      </tr>
                    </thead>
                    <tbody className="bg-white divide-y divide-gray-200">
                      {policies.length === 0 ? (
                        <tr>
                          <td colSpan={7} className="px-6 py-8 text-center text-gray-500">No PAM policies configured</td>
                        </tr>
                      ) : policies.map((policy) => (
                        <tr key={policy.id}>
                          <td className="px-6 py-4 whitespace-nowrap text-sm font-medium text-gray-900">
                            {policy.role?.name || policy.roleId.substring(0, 8)}
                          </td>
                          <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-500">
                            {policy.maxDurationMinutes} min ({Math.round(policy.maxDurationMinutes / 60)}h)
                          </td>
                          <td className="px-6 py-4 whitespace-nowrap text-sm">
                            {policy.requireJustification ? (
                              <span className="text-green-600">Required</span>
                            ) : (
                              <span className="text-gray-400">Optional</span>
                            )}
                          </td>
                          <td className="px-6 py-4 whitespace-nowrap text-sm">
                            {policy.requireApproval ? (
                              <span className="text-green-600">Required</span>
                            ) : (
                              <span className="text-gray-400">Auto-approve</span>
                            )}
                          </td>
                          <td className="px-6 py-4 whitespace-nowrap text-sm">
                            {policy.breakGlassEnabled ? (
                              <span className="text-red-600">Enabled ({policy.breakGlassApproversRequired} approvers)</span>
                            ) : (
                              <span className="text-gray-400">Disabled</span>
                            )}
                          </td>
                          <td className="px-6 py-4 whitespace-nowrap">
                            <span className={`inline-flex px-2 py-1 text-xs font-semibold rounded-full ${policy.isActive ? 'bg-green-100 text-green-800' : 'bg-gray-100 text-gray-800'}`}>
                              {policy.isActive ? 'Active' : 'Inactive'}
                            </span>
                          </td>
                          <td className="px-6 py-4 whitespace-nowrap text-sm space-x-2">
                            <button
                              onClick={() => handleEditPolicy(policy)}
                              className="text-indigo-600 hover:text-indigo-900 font-medium"
                            >
                              Edit
                            </button>
                            <button
                              onClick={() => handleDeletePolicy(policy.id)}
                              className="text-red-600 hover:text-red-900 font-medium"
                            >
                              Delete
                            </button>
                          </td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              </div>
            )}
          </>
        )}
      </div>
    </DashboardLayout>
  );
}
