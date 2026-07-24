import { useState, useEffect, useRef } from 'react';
import DashboardLayout from '../../components/layout/DashboardLayout';
import { api } from '../../services/api';

interface Invitation {
  id: string;
  email: string;
  tenantId: string;
  roleId: string;
  roleName: string;
  status: string;
  invitedBy: string;
  expiresAt: string;
  acceptedAt: string | null;
  createdAt: string;
}

interface Member {
  userId: string;
  email: string;
  firstName: string;
  lastName: string;
  isActive: boolean;
  joinedAt: string;
  roles: { roleId: string; roleName: string; grantedAt: string; expiresAt: string | null }[];
}

interface OrgSettings {
  id?: string;
  tenantId: string;
  allowedEmailDomains: string[];
  requireMfa: boolean;
  defaultRoleId: string | null;
  defaultRoleName: string | null;
  maxMembers: number;
  welcomeMessage: string | null;
}

type TabId = 'invite' | 'pending' | 'all' | 'members' | 'settings' | 'bulk';

export default function InvitationsPage() {
  // State
  const [activeTab, setActiveTab] = useState<TabId>('invite');
  const [tenants, setTenants] = useState<any[]>([]);
  const [roles, setRoles] = useState<any[]>([]);
  const [selectedTenantId, setSelectedTenantId] = useState('');
  const [invitations, setInvitations] = useState<Invitation[]>([]);
  const [pendingInvitations, setPendingInvitations] = useState<Invitation[]>([]);
  const [members, setMembers] = useState<Member[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');
  const [success, setSuccess] = useState('');

  // Invite form
  const [inviteEmail, setInviteEmail] = useState('');
  const [inviteRoleId, setInviteRoleId] = useState('');
  const [inviteExpiryDays, setInviteExpiryDays] = useState(7);

  // Bulk invite
  const fileInputRef = useRef<HTMLInputElement>(null);
  const [bulkResult, setBulkResult] = useState<any>(null);

  // Org settings
  const [, setOrgSettings] = useState<OrgSettings | null>(null);
  const [settingsForm, setSettingsForm] = useState({
    allowedEmailDomains: '',
    requireMfa: false,
    defaultRoleId: '',
    maxMembers: 0,
    welcomeMessage: '',
  });

  // Load tenants and roles on mount
  useEffect(() => {
    loadTenants();
    loadRoles();
  }, []);

  // Load invitations and settings when tenant changes
  useEffect(() => {
    if (selectedTenantId) {
      loadInvitations();
      loadPendingInvitations();
      loadOrgSettings();
      loadMembers();
    }
  }, [selectedTenantId]);

  const loadTenants = async () => {
    try {
      const data = await api.getTenants();
      setTenants(data);
      if (data.length > 0 && !selectedTenantId) {
        setSelectedTenantId(data[0].id);
      }
    } catch {
      setError('Failed to load tenants');
    }
  };

  const loadRoles = async () => {
    try {
      const data = await api.getRoles();
      setRoles(data);
    } catch {
      setError('Failed to load roles');
    }
  };

  const loadInvitations = async () => {
    try {
      const data = await api.getInvitations(selectedTenantId);
      setInvitations(data);
    } catch {
      setError('Failed to load invitations');
    }
  };

  const loadPendingInvitations = async () => {
    try {
      const data = await api.getPendingInvitations(selectedTenantId);
      setPendingInvitations(data);
    } catch {
      setError('Failed to load pending invitations');
    }
  };

  const loadOrgSettings = async () => {
    try {
      const data = await api.getOrganizationSettings(selectedTenantId);
      setOrgSettings(data);
      setSettingsForm({
        allowedEmailDomains: (data.allowedEmailDomains || []).join(', '),
        requireMfa: data.requireMfa || false,
        defaultRoleId: data.defaultRoleId || '',
        maxMembers: data.maxMembers || 0,
        welcomeMessage: data.welcomeMessage || '',
      });
    } catch {
      // Settings might not exist yet, that's OK
    }
  };

  const loadMembers = async () => {
    try {
      const data = await api.getTenantMembers(selectedTenantId);
      setMembers(data);
    } catch {
      setError('Failed to load members');
    }
  };

  const handleChangeMemberRole = async (userId: string, roleId: string) => {
    if (!roleId) return;
    try {
      await api.changeMemberRole(selectedTenantId, userId, roleId);
      setSuccess('Member role updated');
      loadMembers();
    } catch (err: any) {
      setError(err.response?.data?.error || 'Failed to update member role');
    }
  };

  const handleRemoveMember = async (userId: string, email: string) => {
    if (!confirm(`Remove ${email} from this organization?`)) return;

    try {
      await api.removeMember(selectedTenantId, userId);
      setSuccess('Member removed');
      loadMembers();
    } catch (err: any) {
      setError(err.response?.data?.error || 'Failed to remove member');
    }
  };

  const handleSendInvitation = async (e: React.FormEvent) => {
    e.preventDefault();
    setError('');
    setSuccess('');
    setLoading(true);

    try {
      await api.sendInvitation({
        email: inviteEmail,
        tenantId: selectedTenantId,
        roleId: inviteRoleId,
        expiryDays: inviteExpiryDays,
      });
      setSuccess(`Invitation sent to ${inviteEmail}`);
      setInviteEmail('');
      loadInvitations();
      loadPendingInvitations();
    } catch (err: any) {
      setError(err.response?.data?.error || 'Failed to send invitation');
    } finally {
      setLoading(false);
    }
  };

  const handleBulkUpload = async (e: React.FormEvent) => {
    e.preventDefault();
    setError('');
    setSuccess('');
    setBulkResult(null);

    const file = fileInputRef.current?.files?.[0];
    if (!file) {
      setError('Please select a CSV file');
      return;
    }

    setLoading(true);
    try {
      const result = await api.sendBulkInvitations(selectedTenantId, file);
      setBulkResult(result);
      setSuccess(`Bulk invite complete: ${result.succeeded}/${result.totalProcessed} succeeded`);
      loadInvitations();
      loadPendingInvitations();
    } catch (err: any) {
      setError(err.response?.data?.error || 'Failed to process bulk invitations');
    } finally {
      setLoading(false);
    }
  };

  const handleRevokeInvitation = async (id: string) => {
    if (!confirm('Are you sure you want to revoke this invitation?')) return;

    try {
      await api.revokeInvitation(id);
      setSuccess('Invitation revoked');
      loadInvitations();
      loadPendingInvitations();
    } catch (err: any) {
      setError(err.response?.data?.error || 'Failed to revoke invitation');
    }
  };

  const handleSaveSettings = async (e: React.FormEvent) => {
    e.preventDefault();
    setError('');
    setSuccess('');
    setLoading(true);

    try {
      const domains = settingsForm.allowedEmailDomains
        .split(',')
        .map((d) => d.trim())
        .filter((d) => d.length > 0);

      await api.updateOrganizationSettings(selectedTenantId, {
        allowedEmailDomains: domains,
        requireMfa: settingsForm.requireMfa,
        defaultRoleId: settingsForm.defaultRoleId || null,
        maxMembers: settingsForm.maxMembers,
        welcomeMessage: settingsForm.welcomeMessage || null,
      });
      setSuccess('Organization settings saved');
      loadOrgSettings();
    } catch (err: any) {
      setError(err.response?.data?.error || 'Failed to save settings');
    } finally {
      setLoading(false);
    }
  };

  const getStatusBadge = (status: string) => {
    const styles: Record<string, string> = {
      Pending: 'bg-yellow-100 text-yellow-800',
      Accepted: 'bg-green-100 text-green-800',
      Expired: 'bg-gray-100 text-gray-800',
      Revoked: 'bg-red-100 text-red-800',
    };
    return (
      <span className={`inline-flex items-center px-2.5 py-0.5 rounded-full text-xs font-medium ${styles[status] || 'bg-gray-100 text-gray-800'}`}>
        {status}
      </span>
    );
  };

  const tabs: { id: TabId; label: string }[] = [
    { id: 'invite', label: 'Send Invite' },
    { id: 'pending', label: `Pending (${pendingInvitations.length})` },
    { id: 'all', label: 'All Invitations' },
    { id: 'members', label: `Members (${members.length})` },
    { id: 'bulk', label: 'Bulk CSV Upload' },
    { id: 'settings', label: 'Organization Settings' },
  ];

  return (
    <DashboardLayout>
      <div className="space-y-6">
        <div className="flex items-center justify-between">
          <h1 className="text-2xl font-semibold text-gray-900">Invitations & Organization</h1>
          <div className="flex items-center space-x-3">
            <label className="text-sm font-medium text-gray-700">Tenant:</label>
            <select
              value={selectedTenantId}
              onChange={(e) => setSelectedTenantId(e.target.value)}
              className="block rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
            >
              {tenants.map((t) => (
                <option key={t.id} value={t.id}>
                  {t.name}
                </option>
              ))}
            </select>
          </div>
        </div>

        {/* Alerts */}
        {error && (
          <div className="rounded-md bg-red-50 p-4">
            <div className="flex">
              <div className="ml-3">
                <p className="text-sm font-medium text-red-800">{error}</p>
              </div>
              <div className="ml-auto pl-3">
                <button onClick={() => setError('')} className="text-red-500 hover:text-red-600">
                  &times;
                </button>
              </div>
            </div>
          </div>
        )}
        {success && (
          <div className="rounded-md bg-green-50 p-4">
            <div className="flex">
              <div className="ml-3">
                <p className="text-sm font-medium text-green-800">{success}</p>
              </div>
              <div className="ml-auto pl-3">
                <button onClick={() => setSuccess('')} className="text-green-500 hover:text-green-600">
                  &times;
                </button>
              </div>
            </div>
          </div>
        )}

        {/* Tabs */}
        <div className="border-b border-gray-200">
          <nav className="-mb-px flex space-x-8">
            {tabs.map((tab) => (
              <button
                key={tab.id}
                onClick={() => setActiveTab(tab.id)}
                className={`whitespace-nowrap py-4 px-1 border-b-2 font-medium text-sm ${
                  activeTab === tab.id
                    ? 'border-indigo-500 text-indigo-600'
                    : 'border-transparent text-gray-500 hover:text-gray-700 hover:border-gray-300'
                }`}
              >
                {tab.label}
              </button>
            ))}
          </nav>
        </div>

        {/* Tab Content */}
        <div className="bg-white shadow rounded-lg">
          {/* Send Invite Tab */}
          {activeTab === 'invite' && (
            <div className="p-6">
              <h2 className="text-lg font-medium text-gray-900 mb-4">Send Invitation</h2>
              <form onSubmit={handleSendInvitation} className="space-y-4 max-w-lg">
                <div>
                  <label className="block text-sm font-medium text-gray-700">Email Address</label>
                  <input
                    type="email"
                    required
                    value={inviteEmail}
                    onChange={(e) => setInviteEmail(e.target.value)}
                    className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                    placeholder="user@example.com"
                  />
                </div>
                <div>
                  <label className="block text-sm font-medium text-gray-700">Role</label>
                  <select
                    required
                    value={inviteRoleId}
                    onChange={(e) => setInviteRoleId(e.target.value)}
                    className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                  >
                    <option value="">Select a role...</option>
                    {roles.map((r) => (
                      <option key={r.id} value={r.id}>
                        {r.name}
                      </option>
                    ))}
                  </select>
                </div>
                <div>
                  <label className="block text-sm font-medium text-gray-700">Expiry (days)</label>
                  <input
                    type="number"
                    min={1}
                    max={90}
                    value={inviteExpiryDays}
                    onChange={(e) => setInviteExpiryDays(parseInt(e.target.value) || 7)}
                    className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                  />
                </div>
                <button
                  type="submit"
                  disabled={loading || !selectedTenantId}
                  className="inline-flex justify-center py-2 px-4 border border-transparent shadow-sm text-sm font-medium rounded-md text-white bg-indigo-600 hover:bg-indigo-700 focus:outline-none focus:ring-2 focus:ring-offset-2 focus:ring-indigo-500 disabled:opacity-50"
                >
                  {loading ? 'Sending...' : 'Send Invitation'}
                </button>
              </form>
            </div>
          )}

          {/* Pending Invitations Tab */}
          {activeTab === 'pending' && (
            <div className="p-6">
              <h2 className="text-lg font-medium text-gray-900 mb-4">Pending Invitations</h2>
              {pendingInvitations.length === 0 ? (
                <p className="text-gray-500">No pending invitations</p>
              ) : (
                <table className="min-w-full divide-y divide-gray-200">
                  <thead className="bg-gray-50">
                    <tr>
                      <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">Email</th>
                      <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">Role</th>
                      <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">Invited By</th>
                      <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">Expires</th>
                      <th className="px-6 py-3 text-right text-xs font-medium text-gray-500 uppercase tracking-wider">Actions</th>
                    </tr>
                  </thead>
                  <tbody className="bg-white divide-y divide-gray-200">
                    {pendingInvitations.map((inv) => (
                      <tr key={inv.id}>
                        <td className="px-6 py-4 whitespace-nowrap text-sm font-medium text-gray-900">{inv.email}</td>
                        <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-500">{inv.roleName}</td>
                        <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-500">{inv.invitedBy}</td>
                        <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-500">
                          {new Date(inv.expiresAt).toLocaleDateString()}
                        </td>
                        <td className="px-6 py-4 whitespace-nowrap text-right text-sm font-medium">
                          <button
                            onClick={() => handleRevokeInvitation(inv.id)}
                            className="text-red-600 hover:text-red-900"
                          >
                            Revoke
                          </button>
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              )}
            </div>
          )}

          {/* All Invitations Tab */}
          {activeTab === 'all' && (
            <div className="p-6">
              <h2 className="text-lg font-medium text-gray-900 mb-4">All Invitations</h2>
              {invitations.length === 0 ? (
                <p className="text-gray-500">No invitations found</p>
              ) : (
                <table className="min-w-full divide-y divide-gray-200">
                  <thead className="bg-gray-50">
                    <tr>
                      <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">Email</th>
                      <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">Role</th>
                      <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">Status</th>
                      <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">Invited By</th>
                      <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">Created</th>
                      <th className="px-6 py-3 text-right text-xs font-medium text-gray-500 uppercase tracking-wider">Actions</th>
                    </tr>
                  </thead>
                  <tbody className="bg-white divide-y divide-gray-200">
                    {invitations.map((inv) => (
                      <tr key={inv.id}>
                        <td className="px-6 py-4 whitespace-nowrap text-sm font-medium text-gray-900">{inv.email}</td>
                        <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-500">{inv.roleName}</td>
                        <td className="px-6 py-4 whitespace-nowrap text-sm">{getStatusBadge(inv.status)}</td>
                        <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-500">{inv.invitedBy}</td>
                        <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-500">
                          {new Date(inv.createdAt).toLocaleDateString()}
                        </td>
                        <td className="px-6 py-4 whitespace-nowrap text-right text-sm font-medium">
                          {inv.status === 'Pending' && (
                            <button
                              onClick={() => handleRevokeInvitation(inv.id)}
                              className="text-red-600 hover:text-red-900"
                            >
                              Revoke
                            </button>
                          )}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              )}
            </div>
          )}

          {/* Members Tab */}
          {activeTab === 'members' && (
            <div className="p-6">
              <h2 className="text-lg font-medium text-gray-900 mb-4">Members</h2>
              {members.length === 0 ? (
                <p className="text-gray-500">No members in this organization yet</p>
              ) : (
                <table className="min-w-full divide-y divide-gray-200">
                  <thead className="bg-gray-50">
                    <tr>
                      <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">Name</th>
                      <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">Email</th>
                      <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">Role</th>
                      <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">Joined</th>
                      <th className="px-6 py-3 text-right text-xs font-medium text-gray-500 uppercase tracking-wider">Actions</th>
                    </tr>
                  </thead>
                  <tbody className="bg-white divide-y divide-gray-200">
                    {members.map((m) => (
                      <tr key={m.userId}>
                        <td className="px-6 py-4 whitespace-nowrap text-sm font-medium text-gray-900">
                          {`${m.firstName} ${m.lastName}`.trim() || '—'}
                        </td>
                        <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-500">{m.email}</td>
                        <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-500">
                          <select
                            defaultValue=""
                            onChange={(e) => handleChangeMemberRole(m.userId, e.target.value)}
                            className="block rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                          >
                            <option value="" disabled>
                              {m.roles.map((r) => r.roleName).join(', ') || 'No role'}
                            </option>
                            {roles.map((r) => (
                              <option key={r.id} value={r.id}>
                                {r.name}
                              </option>
                            ))}
                          </select>
                        </td>
                        <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-500">
                          {new Date(m.joinedAt).toLocaleDateString()}
                        </td>
                        <td className="px-6 py-4 whitespace-nowrap text-right text-sm font-medium">
                          <button
                            onClick={() => handleRemoveMember(m.userId, m.email)}
                            className="text-red-600 hover:text-red-900"
                          >
                            Remove
                          </button>
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              )}
            </div>
          )}

          {/* Bulk CSV Upload Tab */}
          {activeTab === 'bulk' && (
            <div className="p-6">
              <h2 className="text-lg font-medium text-gray-900 mb-4">Bulk Invite via CSV</h2>
              <p className="text-sm text-gray-500 mb-4">
                Upload a CSV file with columns: <strong>name</strong>, <strong>email</strong>, <strong>role</strong> (optional).
                The first row must be a header row.
              </p>
              <div className="mb-4 p-4 bg-gray-50 rounded-md">
                <p className="text-xs font-mono text-gray-600">
                  name,email,role<br />
                  John Doe,john@example.com,BuildingManager<br />
                  Jane Smith,jane@example.com,Resident
                </p>
              </div>
              <form onSubmit={handleBulkUpload} className="space-y-4 max-w-lg">
                <div>
                  <label className="block text-sm font-medium text-gray-700">CSV File</label>
                  <input
                    type="file"
                    accept=".csv"
                    ref={fileInputRef}
                    className="mt-1 block w-full text-sm text-gray-500 file:mr-4 file:py-2 file:px-4 file:rounded-md file:border-0 file:text-sm file:font-semibold file:bg-indigo-50 file:text-indigo-700 hover:file:bg-indigo-100"
                  />
                </div>
                <button
                  type="submit"
                  disabled={loading || !selectedTenantId}
                  className="inline-flex justify-center py-2 px-4 border border-transparent shadow-sm text-sm font-medium rounded-md text-white bg-indigo-600 hover:bg-indigo-700 focus:outline-none focus:ring-2 focus:ring-offset-2 focus:ring-indigo-500 disabled:opacity-50"
                >
                  {loading ? 'Processing...' : 'Upload & Send Invitations'}
                </button>
              </form>

              {bulkResult && (
                <div className="mt-6 p-4 bg-gray-50 rounded-md">
                  <h3 className="text-sm font-medium text-gray-900 mb-2">Results</h3>
                  <p className="text-sm text-gray-600">
                    Total: {bulkResult.totalProcessed} | Succeeded: {bulkResult.succeeded} | Failed: {bulkResult.failed}
                  </p>
                  {bulkResult.errors && bulkResult.errors.length > 0 && (
                    <div className="mt-2">
                      <p className="text-sm font-medium text-red-700">Errors:</p>
                      <ul className="mt-1 text-sm text-red-600 list-disc list-inside">
                        {bulkResult.errors.map((err: any, idx: number) => (
                          <li key={idx}>
                            Row {err.row} ({err.email}): {err.error}
                          </li>
                        ))}
                      </ul>
                    </div>
                  )}
                </div>
              )}
            </div>
          )}

          {/* Organization Settings Tab */}
          {activeTab === 'settings' && (
            <div className="p-6">
              <h2 className="text-lg font-medium text-gray-900 mb-4">Organization Settings</h2>
              <form onSubmit={handleSaveSettings} className="space-y-4 max-w-lg">
                <div>
                  <label className="block text-sm font-medium text-gray-700">Allowed Email Domains</label>
                  <input
                    type="text"
                    value={settingsForm.allowedEmailDomains}
                    onChange={(e) => setSettingsForm({ ...settingsForm, allowedEmailDomains: e.target.value })}
                    className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                    placeholder="acme.com, example.org (leave empty to allow all)"
                  />
                  <p className="mt-1 text-xs text-gray-500">Comma-separated list of allowed email domains. Leave empty to allow all domains.</p>
                </div>
                <div className="flex items-center">
                  <input
                    type="checkbox"
                    id="requireMfa"
                    checked={settingsForm.requireMfa}
                    onChange={(e) => setSettingsForm({ ...settingsForm, requireMfa: e.target.checked })}
                    className="h-4 w-4 rounded border-gray-300 text-indigo-600 focus:ring-indigo-500"
                  />
                  <label htmlFor="requireMfa" className="ml-2 block text-sm text-gray-900">
                    Require MFA for all members
                  </label>
                </div>
                <div>
                  <label className="block text-sm font-medium text-gray-700">Default Role</label>
                  <select
                    value={settingsForm.defaultRoleId}
                    onChange={(e) => setSettingsForm({ ...settingsForm, defaultRoleId: e.target.value })}
                    className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                  >
                    <option value="">None (role must be specified per invite)</option>
                    {roles.map((r) => (
                      <option key={r.id} value={r.id}>
                        {r.name}
                      </option>
                    ))}
                  </select>
                </div>
                <div>
                  <label className="block text-sm font-medium text-gray-700">Max Members</label>
                  <input
                    type="number"
                    min={0}
                    value={settingsForm.maxMembers}
                    onChange={(e) => setSettingsForm({ ...settingsForm, maxMembers: parseInt(e.target.value) || 0 })}
                    className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                  />
                  <p className="mt-1 text-xs text-gray-500">0 = unlimited</p>
                </div>
                <div>
                  <label className="block text-sm font-medium text-gray-700">Welcome Message</label>
                  <textarea
                    value={settingsForm.welcomeMessage}
                    onChange={(e) => setSettingsForm({ ...settingsForm, welcomeMessage: e.target.value })}
                    rows={3}
                    className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                    placeholder="Welcome to our organization! Here's how to get started..."
                  />
                </div>
                <button
                  type="submit"
                  disabled={loading || !selectedTenantId}
                  className="inline-flex justify-center py-2 px-4 border border-transparent shadow-sm text-sm font-medium rounded-md text-white bg-indigo-600 hover:bg-indigo-700 focus:outline-none focus:ring-2 focus:ring-offset-2 focus:ring-indigo-500 disabled:opacity-50"
                >
                  {loading ? 'Saving...' : 'Save Settings'}
                </button>
              </form>
            </div>
          )}
        </div>
      </div>
    </DashboardLayout>
  );
}
