import { useState, useEffect } from 'react';
import DashboardLayout from '../../components/layout/DashboardLayout';
import { delegationApi } from '../../services/delegationApi';
import { api } from '../../services/api';
import type {
  Delegation,
  SodConstraint,
  SodViolation,
  CreateDelegationRequest,
  CreateSodConstraintRequest,
} from '../../services/delegationApi';

type TabType = 'delegations' | 'constraints' | 'violations';

export default function DelegationPage() {
  const [activeTab, setActiveTab] = useState<TabType>('delegations');
  const [tenants, setTenants] = useState<any[]>([]);
  const [roles, setRoles] = useState<any[]>([]);
  const [users, setUsers] = useState<any[]>([]);
  const [selectedTenantId, setSelectedTenantId] = useState<string>('');
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [successMessage, setSuccessMessage] = useState<string | null>(null);

  // Delegation state
  const [delegations, setDelegations] = useState<Delegation[]>([]);
  const [showCreateDelegation, setShowCreateDelegation] = useState(false);

  // SoD Constraint state
  const [constraints, setConstraints] = useState<SodConstraint[]>([]);
  const [showCreateConstraint, setShowCreateConstraint] = useState(false);

  // SoD Violation state
  const [violations, setViolations] = useState<SodViolation[]>([]);
  const [checkUserId, setCheckUserId] = useState('');

  // Form state - Delegation
  const [delegationForm, setDelegationForm] = useState<CreateDelegationRequest>({
    delegateUserId: '',
    tenantId: '',
    permissions: [],
    validFrom: new Date().toISOString().slice(0, 16),
    validUntil: new Date(Date.now() + 7 * 24 * 60 * 60 * 1000).toISOString().slice(0, 16),
    reason: '',
    requiresApproval: false,
  });
  const [permissionInput, setPermissionInput] = useState('');

  // Form state - Constraint
  const [constraintForm, setConstraintForm] = useState<CreateSodConstraintRequest>({
    name: '',
    tenantId: '',
    conflictingRoleA: '',
    conflictingRoleB: '',
    description: '',
    severity: 'Warning',
  });

  // Load initial data
  useEffect(() => {
    loadInitialData();
  }, []);

  // Load tenant-specific data when tenant changes
  useEffect(() => {
    if (selectedTenantId) {
      loadTenantData();
    }
  }, [selectedTenantId, activeTab]);

  const loadInitialData = async () => {
    try {
      const [tenantsData, rolesData, usersData] = await Promise.all([
        api.getTenants(),
        api.getRoles(),
        api.getUsers(),
      ]);
      setTenants(tenantsData);
      setRoles(rolesData);
      setUsers(usersData);
      if (tenantsData.length > 0) {
        setSelectedTenantId(tenantsData[0].id);
      }
    } catch (err: any) {
      setError('Failed to load initial data');
    }
  };

  const loadTenantData = async () => {
    setLoading(true);
    setError(null);
    try {
      if (activeTab === 'delegations') {
        const data = await delegationApi.getDelegations(selectedTenantId);
        setDelegations(data);
      } else if (activeTab === 'constraints') {
        const data = await delegationApi.getConstraints(selectedTenantId);
        setConstraints(data);
      } else if (activeTab === 'violations') {
        const data = await delegationApi.getViolations(selectedTenantId);
        setViolations(data);
      }
    } catch (err: any) {
      setError(err.response?.data?.error || 'Failed to load data');
    } finally {
      setLoading(false);
    }
  };

  const showSuccess = (msg: string) => {
    setSuccessMessage(msg);
    setTimeout(() => setSuccessMessage(null), 3000);
  };

  // ─── Delegation Actions ─────────────────────────────────

  const handleCreateDelegation = async (e: React.FormEvent) => {
    e.preventDefault();
    setError(null);
    try {
      await delegationApi.createDelegation({
        ...delegationForm,
        tenantId: selectedTenantId,
      });
      showSuccess('Delegation created successfully');
      setShowCreateDelegation(false);
      setDelegationForm({
        delegateUserId: '',
        tenantId: '',
        permissions: [],
        validFrom: new Date().toISOString().slice(0, 16),
        validUntil: new Date(Date.now() + 7 * 24 * 60 * 60 * 1000).toISOString().slice(0, 16),
        reason: '',
        requiresApproval: false,
      });
      setPermissionInput('');
      loadTenantData();
    } catch (err: any) {
      setError(err.response?.data?.error || 'Failed to create delegation');
    }
  };

  const handleApproveDelegation = async (id: string) => {
    try {
      await delegationApi.approveDelegation(id);
      showSuccess('Delegation approved');
      loadTenantData();
    } catch (err: any) {
      setError(err.response?.data?.error || 'Failed to approve delegation');
    }
  };

  const handleRevokeDelegation = async (id: string) => {
    if (!confirm('Are you sure you want to revoke this delegation?')) return;
    try {
      await delegationApi.revokeDelegation(id);
      showSuccess('Delegation revoked');
      loadTenantData();
    } catch (err: any) {
      setError(err.response?.data?.error || 'Failed to revoke delegation');
    }
  };

  const addPermission = () => {
    if (permissionInput.trim() && !delegationForm.permissions.includes(permissionInput.trim())) {
      setDelegationForm({
        ...delegationForm,
        permissions: [...delegationForm.permissions, permissionInput.trim()],
      });
      setPermissionInput('');
    }
  };

  const removePermission = (perm: string) => {
    setDelegationForm({
      ...delegationForm,
      permissions: delegationForm.permissions.filter((p) => p !== perm),
    });
  };

  // ─── SoD Constraint Actions ─────────────────────────────

  const handleCreateConstraint = async (e: React.FormEvent) => {
    e.preventDefault();
    setError(null);
    try {
      await delegationApi.createConstraint({
        ...constraintForm,
        tenantId: selectedTenantId,
      });
      showSuccess('SoD constraint created successfully');
      setShowCreateConstraint(false);
      setConstraintForm({
        name: '',
        tenantId: '',
        conflictingRoleA: '',
        conflictingRoleB: '',
        description: '',
        severity: 'Warning',
      });
      loadTenantData();
    } catch (err: any) {
      setError(err.response?.data?.error || 'Failed to create constraint');
    }
  };

  const handleDeleteConstraint = async (id: string) => {
    if (!confirm('Are you sure you want to delete this SoD constraint?')) return;
    try {
      await delegationApi.deleteConstraint(id);
      showSuccess('SoD constraint deleted');
      loadTenantData();
    } catch (err: any) {
      setError(err.response?.data?.error || 'Failed to delete constraint');
    }
  };

  // ─── SoD Violation Actions ─────────────────────────────

  const handleCheckViolations = async () => {
    if (!checkUserId) return;
    setError(null);
    try {
      const result = await delegationApi.checkViolations(checkUserId);
      showSuccess(`Check complete: ${result.violationsFound} violation(s) found`);
      loadTenantData();
    } catch (err: any) {
      setError(err.response?.data?.error || 'Failed to check violations');
    }
  };

  const handleResolveViolation = async (id: string) => {
    const resolution = prompt('Enter resolution description:');
    if (!resolution) return;
    try {
      await delegationApi.resolveViolation(id, resolution);
      showSuccess('Violation resolved');
      loadTenantData();
    } catch (err: any) {
      setError(err.response?.data?.error || 'Failed to resolve violation');
    }
  };

  // ─── Helper: parse permissions JSON ─────────────────────

  const parsePermissions = (json: string): string[] => {
    try {
      return JSON.parse(json);
    } catch {
      return [];
    }
  };

  // ─── Status Badge ───────────────────────────────────────

  const statusBadge = (status: string) => {
    const colors: Record<string, string> = {
      Active: 'bg-green-100 text-green-800',
      PendingApproval: 'bg-yellow-100 text-yellow-800',
      Expired: 'bg-gray-100 text-gray-800',
      Revoked: 'bg-red-100 text-red-800',
      Denied: 'bg-red-100 text-red-800',
    };
    return (
      <span className={`inline-flex items-center px-2.5 py-0.5 rounded-full text-xs font-medium ${colors[status] || 'bg-gray-100 text-gray-800'}`}>
        {status}
      </span>
    );
  };

  const severityBadge = (severity: string) => {
    const color = severity === 'Block' ? 'bg-red-100 text-red-800' : 'bg-yellow-100 text-yellow-800';
    return (
      <span className={`inline-flex items-center px-2.5 py-0.5 rounded-full text-xs font-medium ${color}`}>
        {severity}
      </span>
    );
  };

  const tabClass = (tab: TabType) =>
    `px-4 py-2 text-sm font-medium rounded-t-lg ${
      activeTab === tab
        ? 'bg-white text-indigo-600 border-b-2 border-indigo-600'
        : 'text-gray-500 hover:text-gray-700 hover:bg-gray-50'
    }`;

  return (
    <DashboardLayout>
      <div className="space-y-6">
        <div className="flex justify-between items-center">
          <h1 className="text-2xl font-bold text-gray-900">Delegation & Segregation of Duties</h1>
          <select
            value={selectedTenantId}
            onChange={(e) => setSelectedTenantId(e.target.value)}
            className="rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 text-sm"
          >
            {tenants.map((t) => (
              <option key={t.id} value={t.id}>{t.name}</option>
            ))}
          </select>
        </div>

        {/* Messages */}
        {error && (
          <div className="bg-red-50 border border-red-200 text-red-700 px-4 py-3 rounded-md">
            {error}
          </div>
        )}
        {successMessage && (
          <div className="bg-green-50 border border-green-200 text-green-700 px-4 py-3 rounded-md">
            {successMessage}
          </div>
        )}

        {/* Tabs */}
        <div className="border-b border-gray-200">
          <nav className="flex space-x-2">
            <button className={tabClass('delegations')} onClick={() => setActiveTab('delegations')}>
              Delegations
            </button>
            <button className={tabClass('constraints')} onClick={() => setActiveTab('constraints')}>
              SoD Constraints
            </button>
            <button className={tabClass('violations')} onClick={() => setActiveTab('violations')}>
              SoD Violations
            </button>
          </nav>
        </div>

        {/* ─── Delegations Tab ───────────────────────── */}
        {activeTab === 'delegations' && (
          <div className="space-y-4">
            <div className="flex justify-end">
              <button
                onClick={() => setShowCreateDelegation(!showCreateDelegation)}
                className="inline-flex items-center px-4 py-2 border border-transparent text-sm font-medium rounded-md shadow-sm text-white bg-indigo-600 hover:bg-indigo-700"
              >
                {showCreateDelegation ? 'Cancel' : 'Create Delegation'}
              </button>
            </div>

            {/* Create Delegation Form */}
            {showCreateDelegation && (
              <div className="bg-white shadow rounded-lg p-6">
                <h3 className="text-lg font-medium text-gray-900 mb-4">Create New Delegation</h3>
                <form onSubmit={handleCreateDelegation} className="space-y-4">
                  <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
                    <div>
                      <label className="block text-sm font-medium text-gray-700">Delegate User</label>
                      <select
                        value={delegationForm.delegateUserId}
                        onChange={(e) => setDelegationForm({ ...delegationForm, delegateUserId: e.target.value })}
                        required
                        className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                      >
                        <option value="">Select user...</option>
                        {users.map((u) => (
                          <option key={u.id} value={u.id}>{u.firstName} {u.lastName} ({u.email})</option>
                        ))}
                      </select>
                    </div>
                    <div>
                      <label className="block text-sm font-medium text-gray-700">Reason</label>
                      <input
                        type="text"
                        value={delegationForm.reason}
                        onChange={(e) => setDelegationForm({ ...delegationForm, reason: e.target.value })}
                        required
                        className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                        placeholder="e.g., Vacation coverage"
                      />
                    </div>
                    <div>
                      <label className="block text-sm font-medium text-gray-700">Valid From</label>
                      <input
                        type="datetime-local"
                        value={delegationForm.validFrom}
                        onChange={(e) => setDelegationForm({ ...delegationForm, validFrom: e.target.value })}
                        required
                        className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                      />
                    </div>
                    <div>
                      <label className="block text-sm font-medium text-gray-700">Valid Until</label>
                      <input
                        type="datetime-local"
                        value={delegationForm.validUntil}
                        onChange={(e) => setDelegationForm({ ...delegationForm, validUntil: e.target.value })}
                        required
                        className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                      />
                    </div>
                  </div>

                  {/* Permissions */}
                  <div>
                    <label className="block text-sm font-medium text-gray-700">Permissions</label>
                    <div className="flex mt-1 space-x-2">
                      <input
                        type="text"
                        value={permissionInput}
                        onChange={(e) => setPermissionInput(e.target.value)}
                        onKeyDown={(e) => { if (e.key === 'Enter') { e.preventDefault(); addPermission(); } }}
                        className="block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                        placeholder="e.g., Building.View"
                      />
                      <button
                        type="button"
                        onClick={addPermission}
                        className="inline-flex items-center px-3 py-2 border border-gray-300 shadow-sm text-sm font-medium rounded-md text-gray-700 bg-white hover:bg-gray-50"
                      >
                        Add
                      </button>
                    </div>
                    <div className="flex flex-wrap gap-2 mt-2">
                      {delegationForm.permissions.map((perm) => (
                        <span key={perm} className="inline-flex items-center px-2.5 py-0.5 rounded-full text-xs font-medium bg-indigo-100 text-indigo-800">
                          {perm}
                          <button
                            type="button"
                            onClick={() => removePermission(perm)}
                            className="ml-1 text-indigo-600 hover:text-indigo-800"
                          >
                            x
                          </button>
                        </span>
                      ))}
                    </div>
                  </div>

                  <div className="flex items-center">
                    <input
                      type="checkbox"
                      checked={delegationForm.requiresApproval}
                      onChange={(e) => setDelegationForm({ ...delegationForm, requiresApproval: e.target.checked })}
                      className="h-4 w-4 text-indigo-600 focus:ring-indigo-500 border-gray-300 rounded"
                    />
                    <label className="ml-2 block text-sm text-gray-700">Requires approval</label>
                  </div>

                  <div className="flex justify-end">
                    <button
                      type="submit"
                      className="inline-flex items-center px-4 py-2 border border-transparent text-sm font-medium rounded-md shadow-sm text-white bg-indigo-600 hover:bg-indigo-700"
                    >
                      Create Delegation
                    </button>
                  </div>
                </form>
              </div>
            )}

            {/* Delegations Table */}
            <div className="bg-white shadow rounded-lg overflow-hidden">
              <table className="min-w-full divide-y divide-gray-200">
                <thead className="bg-gray-50">
                  <tr>
                    <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">Delegator</th>
                    <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">Delegate</th>
                    <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">Permissions</th>
                    <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">Valid Period</th>
                    <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">Status</th>
                    <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">Actions</th>
                  </tr>
                </thead>
                <tbody className="bg-white divide-y divide-gray-200">
                  {loading ? (
                    <tr><td colSpan={6} className="px-6 py-4 text-center text-gray-500">Loading...</td></tr>
                  ) : delegations.length === 0 ? (
                    <tr><td colSpan={6} className="px-6 py-4 text-center text-gray-500">No delegations found</td></tr>
                  ) : (
                    delegations.map((d) => (
                      <tr key={d.id}>
                        <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-900">{d.delegatorUserName || d.delegatorUserId.slice(0, 8)}</td>
                        <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-900">{d.delegateUserName || d.delegateUserId.slice(0, 8)}</td>
                        <td className="px-6 py-4 text-sm text-gray-500">
                          <div className="flex flex-wrap gap-1">
                            {parsePermissions(d.permissions).slice(0, 3).map((p) => (
                              <span key={p} className="inline-flex items-center px-2 py-0.5 rounded text-xs font-medium bg-gray-100 text-gray-800">{p}</span>
                            ))}
                            {parsePermissions(d.permissions).length > 3 && (
                              <span className="text-xs text-gray-400">+{parsePermissions(d.permissions).length - 3} more</span>
                            )}
                          </div>
                        </td>
                        <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-500">
                          {new Date(d.validFrom).toLocaleDateString()} - {new Date(d.validUntil).toLocaleDateString()}
                        </td>
                        <td className="px-6 py-4 whitespace-nowrap">{statusBadge(d.status)}</td>
                        <td className="px-6 py-4 whitespace-nowrap text-sm space-x-2">
                          {d.status === 'PendingApproval' && (
                            <button
                              onClick={() => handleApproveDelegation(d.id)}
                              className="text-green-600 hover:text-green-800 font-medium"
                            >
                              Approve
                            </button>
                          )}
                          {(d.status === 'Active' || d.status === 'PendingApproval') && (
                            <button
                              onClick={() => handleRevokeDelegation(d.id)}
                              className="text-red-600 hover:text-red-800 font-medium"
                            >
                              Revoke
                            </button>
                          )}
                        </td>
                      </tr>
                    ))
                  )}
                </tbody>
              </table>
            </div>
          </div>
        )}

        {/* ─── SoD Constraints Tab ───────────────────── */}
        {activeTab === 'constraints' && (
          <div className="space-y-4">
            <div className="flex justify-end">
              <button
                onClick={() => setShowCreateConstraint(!showCreateConstraint)}
                className="inline-flex items-center px-4 py-2 border border-transparent text-sm font-medium rounded-md shadow-sm text-white bg-indigo-600 hover:bg-indigo-700"
              >
                {showCreateConstraint ? 'Cancel' : 'Create SoD Constraint'}
              </button>
            </div>

            {/* Create Constraint Form */}
            {showCreateConstraint && (
              <div className="bg-white shadow rounded-lg p-6">
                <h3 className="text-lg font-medium text-gray-900 mb-4">Create SoD Constraint</h3>
                <form onSubmit={handleCreateConstraint} className="space-y-4">
                  <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
                    <div>
                      <label className="block text-sm font-medium text-gray-700">Constraint Name</label>
                      <input
                        type="text"
                        value={constraintForm.name}
                        onChange={(e) => setConstraintForm({ ...constraintForm, name: e.target.value })}
                        required
                        className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                        placeholder="e.g., Finance Approver vs Requester"
                      />
                    </div>
                    <div>
                      <label className="block text-sm font-medium text-gray-700">Severity</label>
                      <select
                        value={constraintForm.severity}
                        onChange={(e) => setConstraintForm({ ...constraintForm, severity: e.target.value })}
                        className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                      >
                        <option value="Warning">Warning (log only)</option>
                        <option value="Block">Block (prevent assignment)</option>
                      </select>
                    </div>
                    <div>
                      <label className="block text-sm font-medium text-gray-700">Conflicting Role A</label>
                      <select
                        value={constraintForm.conflictingRoleA}
                        onChange={(e) => setConstraintForm({ ...constraintForm, conflictingRoleA: e.target.value })}
                        required
                        className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                      >
                        <option value="">Select role...</option>
                        {roles.map((r) => (
                          <option key={r.id} value={r.id}>{r.name}</option>
                        ))}
                      </select>
                    </div>
                    <div>
                      <label className="block text-sm font-medium text-gray-700">Conflicting Role B</label>
                      <select
                        value={constraintForm.conflictingRoleB}
                        onChange={(e) => setConstraintForm({ ...constraintForm, conflictingRoleB: e.target.value })}
                        required
                        className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                      >
                        <option value="">Select role...</option>
                        {roles.map((r) => (
                          <option key={r.id} value={r.id}>{r.name}</option>
                        ))}
                      </select>
                    </div>
                  </div>
                  <div>
                    <label className="block text-sm font-medium text-gray-700">Description</label>
                    <textarea
                      value={constraintForm.description}
                      onChange={(e) => setConstraintForm({ ...constraintForm, description: e.target.value })}
                      rows={2}
                      className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                      placeholder="Why these roles conflict..."
                    />
                  </div>
                  <div className="flex justify-end">
                    <button
                      type="submit"
                      className="inline-flex items-center px-4 py-2 border border-transparent text-sm font-medium rounded-md shadow-sm text-white bg-indigo-600 hover:bg-indigo-700"
                    >
                      Create Constraint
                    </button>
                  </div>
                </form>
              </div>
            )}

            {/* Constraints Table */}
            <div className="bg-white shadow rounded-lg overflow-hidden">
              <table className="min-w-full divide-y divide-gray-200">
                <thead className="bg-gray-50">
                  <tr>
                    <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">Name</th>
                    <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">Role A</th>
                    <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">Role B</th>
                    <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">Severity</th>
                    <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">Active</th>
                    <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">Actions</th>
                  </tr>
                </thead>
                <tbody className="bg-white divide-y divide-gray-200">
                  {loading ? (
                    <tr><td colSpan={6} className="px-6 py-4 text-center text-gray-500">Loading...</td></tr>
                  ) : constraints.length === 0 ? (
                    <tr><td colSpan={6} className="px-6 py-4 text-center text-gray-500">No SoD constraints found</td></tr>
                  ) : (
                    constraints.map((c) => (
                      <tr key={c.id}>
                        <td className="px-6 py-4 whitespace-nowrap text-sm font-medium text-gray-900">{c.name}</td>
                        <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-500">{c.roleAName || c.conflictingRoleA.slice(0, 8)}</td>
                        <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-500">{c.roleBName || c.conflictingRoleB.slice(0, 8)}</td>
                        <td className="px-6 py-4 whitespace-nowrap">{severityBadge(c.severity)}</td>
                        <td className="px-6 py-4 whitespace-nowrap text-sm">
                          <span className={c.isActive ? 'text-green-600' : 'text-gray-400'}>{c.isActive ? 'Yes' : 'No'}</span>
                        </td>
                        <td className="px-6 py-4 whitespace-nowrap text-sm">
                          <button
                            onClick={() => handleDeleteConstraint(c.id)}
                            className="text-red-600 hover:text-red-800 font-medium"
                          >
                            Delete
                          </button>
                        </td>
                      </tr>
                    ))
                  )}
                </tbody>
              </table>
            </div>
          </div>
        )}

        {/* ─── SoD Violations Tab ────────────────────── */}
        {activeTab === 'violations' && (
          <div className="space-y-4">
            {/* Check Violations */}
            <div className="bg-white shadow rounded-lg p-4">
              <h3 className="text-sm font-medium text-gray-700 mb-2">Check User for SoD Violations</h3>
              <div className="flex space-x-2">
                <select
                  value={checkUserId}
                  onChange={(e) => setCheckUserId(e.target.value)}
                  className="block w-64 rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                >
                  <option value="">Select user...</option>
                  {users.map((u) => (
                    <option key={u.id} value={u.id}>{u.firstName} {u.lastName}</option>
                  ))}
                </select>
                <button
                  onClick={handleCheckViolations}
                  disabled={!checkUserId}
                  className="inline-flex items-center px-4 py-2 border border-transparent text-sm font-medium rounded-md shadow-sm text-white bg-indigo-600 hover:bg-indigo-700 disabled:bg-gray-400"
                >
                  Check Violations
                </button>
              </div>
            </div>

            {/* Violations Table */}
            <div className="bg-white shadow rounded-lg overflow-hidden">
              <table className="min-w-full divide-y divide-gray-200">
                <thead className="bg-gray-50">
                  <tr>
                    <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">Constraint</th>
                    <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">User</th>
                    <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">Detected At</th>
                    <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">Resolution</th>
                    <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">Actions</th>
                  </tr>
                </thead>
                <tbody className="bg-white divide-y divide-gray-200">
                  {loading ? (
                    <tr><td colSpan={5} className="px-6 py-4 text-center text-gray-500">Loading...</td></tr>
                  ) : violations.length === 0 ? (
                    <tr><td colSpan={5} className="px-6 py-4 text-center text-gray-500">No violations found</td></tr>
                  ) : (
                    violations.map((v) => (
                      <tr key={v.id}>
                        <td className="px-6 py-4 whitespace-nowrap text-sm font-medium text-gray-900">{v.constraintName || v.constraintId.slice(0, 8)}</td>
                        <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-500">{v.userName || v.userId.slice(0, 8)}</td>
                        <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-500">{new Date(v.detectedAt).toLocaleString()}</td>
                        <td className="px-6 py-4 text-sm text-gray-500">
                          {v.resolvedAt ? (
                            <span className="text-green-600">{v.resolution}</span>
                          ) : (
                            <span className="text-yellow-600">Unresolved</span>
                          )}
                        </td>
                        <td className="px-6 py-4 whitespace-nowrap text-sm">
                          {!v.resolvedAt && (
                            <button
                              onClick={() => handleResolveViolation(v.id)}
                              className="text-indigo-600 hover:text-indigo-800 font-medium"
                            >
                              Resolve
                            </button>
                          )}
                        </td>
                      </tr>
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
