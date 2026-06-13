import { useState, useEffect } from 'react';
import { useParams, useNavigate } from 'react-router-dom';
import { useForm } from 'react-hook-form';
import DashboardLayout from '../../components/layout/DashboardLayout';
import { api } from '../../services/api';
import type { User, Role } from '../../types';

interface UserFormData {
  firstName: string;
  lastName: string;
  email: string;
  phoneNumber?: string;
}

const JENGO_SERVERS = [
  { id: '10000000-0000-0000-0000-000000000001', label: 'Development' },
  { id: '10000000-0000-0000-0000-000000000002', label: 'Production' },
] as const;

const JENGO_ROLE_ORDER = [
  'JengoViewer',
  'JengoOperator',
  'JengoDeveloper',
  'JengoAdmin',
  'JengoMcpAccess',
];

const JENGO_ROLE_LABELS: Record<string, { label: string; desc: string }> = {
  JengoViewer:    { label: 'Viewer',    desc: 'Logs, status, dashboards' },
  JengoOperator:  { label: 'Operator',  desc: 'Start/stop agents, tasks' },
  JengoDeveloper: { label: 'Developer', desc: 'Code, worktrees, repos' },
  JengoAdmin:     { label: 'Admin',     desc: 'Full access + settings' },
  JengoMcpAccess: { label: 'MCP',       desc: 'MCP server / ChatGPT app' },
};

export default function UserEditPage() {
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');
  const [success, setSuccess] = useState('');
  const [user, setUser] = useState<User | null>(null);
  const [userRoles, setUserRoles] = useState<any[]>([]);
  const [allRoles, setAllRoles] = useState<Role[]>([]);
  const [togglingKey, setTogglingKey] = useState<string | null>(null);
  const [assigningRole, setAssigningRole] = useState<string | null>(null);

  const { register, handleSubmit, reset, formState: { errors } } = useForm<UserFormData>();

  useEffect(() => {
    if (id) Promise.all([loadUser(), loadRoles()]);
  }, [id]);

  const loadUser = async () => {
    try {
      setLoading(true);
      const data = await api.getUser(id!);
      setUser(data);
      reset({ firstName: data.firstName, lastName: data.lastName, email: data.email, phoneNumber: data.phoneNumber || '' });
      const roles = await api.getUserRoles(id!);
      setUserRoles(Array.isArray(roles) ? roles : []);
    } catch (err: any) {
      setError(err.response?.data?.message || 'Failed to load user');
    } finally {
      setLoading(false);
    }
  };

  const loadRoles = async () => {
    try {
      const roles = await api.getRoles();
      setAllRoles(Array.isArray(roles) ? roles : []);
    } catch { /* ignore */ }
  };

  const reloadUserRoles = async () => {
    const roles = await api.getUserRoles(id!);
    setUserRoles(Array.isArray(roles) ? roles : []);
  };

  const onSubmit = async (data: UserFormData) => {
    try {
      setSaving(true); setError(''); setSuccess('');
      await api.updateUser(id!, data);
      setSuccess('User updated successfully');
    } catch (err: any) {
      setError(err.response?.data?.message || 'Failed to update user');
    } finally {
      setSaving(false);
    }
  };

  // Standard role assign/revoke (no tenant)
  const handleAssignRole = async (roleId: string) => {
    try {
      setAssigningRole(roleId);
      await api.assignRole(roleId, id!);
      await reloadUserRoles();
    } catch (err: any) {
      alert(err.response?.data?.message || 'Failed to assign role');
    } finally {
      setAssigningRole(null);
    }
  };

  const handleRevokeRole = async (roleId: string) => {
    try {
      setAssigningRole(roleId);
      await api.revokeRole(roleId, id!);
      await reloadUserRoles();
    } catch (err: any) {
      alert(err.response?.data?.message || 'Failed to revoke role');
    } finally {
      setAssigningRole(null);
    }
  };

  // Jengo matrix toggle (tenant-scoped)
  const handleJengoToggle = async (roleId: string, tenantId: string, currentlyOn: boolean) => {
    const key = `${roleId}:${tenantId}`;
    try {
      setTogglingKey(key);
      if (currentlyOn) {
        await api.revokeRole(roleId, id!, tenantId);
      } else {
        await api.assignRole(roleId, id!, tenantId);
      }
      await reloadUserRoles();
    } catch (err: any) {
      alert(err.response?.data?.message || 'Failed to update access');
    } finally {
      setTogglingKey(null);
    }
  };

  // Separate Jengo roles from standard roles
  const jengoRoles = allRoles.filter(r => r.category === 'Jengo');
  const standardRoles = allRoles.filter(r => r.category !== 'Jengo');
  const assignedStandardRoleIds = new Set(
    userRoles.filter((ur: any) => {
      const roleName = ur.role?.name ?? ur.name ?? '';
      return !JENGO_ROLE_ORDER.includes(roleName);
    }).map((ur: any) => ur.roleId ?? ur.role?.id ?? ur.id)
  );
  const unassignedStandardRoles = standardRoles.filter(r => !assignedStandardRoleIds.has(r.id));

  // Check if user has a specific Jengo role on a specific server
  const hasJengoAccess = (roleId: string, tenantId: string) =>
    userRoles.some((ur: any) => (ur.roleId ?? ur.role?.id ?? ur.id) === roleId && ur.tenantId === tenantId);

  if (loading) return (
    <DashboardLayout>
      <div className="flex flex-col items-center justify-center py-24 text-gray-400">
        <div className="w-8 h-8 border-2 border-indigo-500 border-t-transparent rounded-full animate-spin mb-3" />
        <span className="text-sm">Loading user...</span>
      </div>
    </DashboardLayout>
  );

  if (!user) return (
    <DashboardLayout>
      <div className="text-center py-16">
        <p className="text-gray-500 mb-4">User not found</p>
        <button onClick={() => navigate('/users')} className="text-indigo-600 hover:underline text-sm">Back to users</button>
      </div>
    </DashboardLayout>
  );

  const initials = `${user.firstName?.[0] ?? ''}${user.lastName?.[0] ?? ''}`.toUpperCase();

  return (
    <DashboardLayout>
      <div className="max-w-3xl mx-auto">
        {/* Back link */}
        <button onClick={() => navigate('/users')} className="flex items-center gap-1 text-sm text-gray-500 hover:text-gray-700 mb-6 transition-colors">
          <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M15 19l-7-7 7-7" />
          </svg>
          Back to users
        </button>

        {/* User header */}
        <div className="flex items-center gap-4 mb-6">
          <div className="w-14 h-14 bg-indigo-100 text-indigo-700 rounded-full flex items-center justify-center text-lg font-bold">{initials}</div>
          <div>
            <h1 className="text-xl font-bold text-gray-900">{user.firstName} {user.lastName}</h1>
            <p className="text-sm text-gray-500">{user.email}</p>
          </div>
          <div className="ml-auto flex gap-2">
            <span className={`px-3 py-1 rounded-full text-xs font-semibold ${user.isActive ? 'bg-green-100 text-green-700' : 'bg-gray-100 text-gray-500'}`}>
              {user.isActive ? 'Active' : 'Inactive'}
            </span>
            <span className={`px-3 py-1 rounded-full text-xs font-semibold ${user.emailConfirmed ? 'bg-blue-100 text-blue-700' : 'bg-amber-100 text-amber-700'}`}>
              {user.emailConfirmed ? 'Verified' : 'Unverified'}
            </span>
          </div>
        </div>

        {/* Edit form */}
        <div className="bg-white rounded-xl border border-gray-200 shadow-sm p-6 mb-6">
          <h2 className="text-sm font-semibold text-gray-700 uppercase tracking-wide mb-4">Edit Details</h2>
          {error && <div className="mb-4 p-3 bg-red-50 border border-red-200 text-red-700 rounded-lg text-sm">{error}</div>}
          {success && <div className="mb-4 p-3 bg-green-50 border border-green-200 text-green-700 rounded-lg text-sm">{success}</div>}
          <form onSubmit={handleSubmit(onSubmit)} className="space-y-4">
            <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
              <div>
                <label className="block text-sm font-medium text-gray-700 mb-1">First Name *</label>
                <input type="text" {...register('firstName', { required: 'Required' })}
                  className={`block w-full border rounded-lg px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-transparent ${errors.firstName ? 'border-red-300' : 'border-gray-300'}`} />
                {errors.firstName && <p className="mt-1 text-xs text-red-600">{errors.firstName.message}</p>}
              </div>
              <div>
                <label className="block text-sm font-medium text-gray-700 mb-1">Last Name *</label>
                <input type="text" {...register('lastName', { required: 'Required' })}
                  className={`block w-full border rounded-lg px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-transparent ${errors.lastName ? 'border-red-300' : 'border-gray-300'}`} />
                {errors.lastName && <p className="mt-1 text-xs text-red-600">{errors.lastName.message}</p>}
              </div>
            </div>
            <div>
              <label className="block text-sm font-medium text-gray-700 mb-1">Email *</label>
              <input type="email" {...register('email', { required: 'Required' })}
                className={`block w-full border rounded-lg px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-transparent ${errors.email ? 'border-red-300' : 'border-gray-300'}`} />
              {errors.email && <p className="mt-1 text-xs text-red-600">{errors.email.message}</p>}
            </div>
            <div>
              <label className="block text-sm font-medium text-gray-700 mb-1">Phone Number</label>
              <input type="tel" {...register('phoneNumber')}
                className="block w-full border border-gray-300 rounded-lg px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-transparent" />
            </div>
            <div className="flex justify-end pt-2">
              <button type="submit" disabled={saving}
                className="px-5 py-2 bg-indigo-600 text-white text-sm font-medium rounded-lg hover:bg-indigo-700 transition-colors disabled:opacity-50 shadow-sm">
                {saving ? 'Saving...' : 'Save Changes'}
              </button>
            </div>
          </form>
        </div>

        {/* Jengo Access Matrix */}
        {jengoRoles.length > 0 && (
          <div className="bg-white rounded-xl border border-gray-200 shadow-sm p-6 mb-6">
            <div className="flex items-center gap-2 mb-1">
              <svg className="w-4 h-4 text-violet-600" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9.75 17L9 20l-1 1h8l-1-1-.75-3M3 13h18M5 17h14a2 2 0 002-2V5a2 2 0 00-2-2H5a2 2 0 00-2 2v10a2 2 0 002 2z" />
              </svg>
              <h2 className="text-sm font-semibold text-gray-700 uppercase tracking-wide">Jengo Access</h2>
            </div>
            <p className="text-xs text-gray-400 mb-5">Per-server access control for Jengo applications</p>

            <div className="overflow-x-auto">
              <table className="w-full text-sm">
                <thead>
                  <tr>
                    <th className="text-left text-xs font-medium text-gray-500 pb-3 pr-4 w-48">Role</th>
                    {JENGO_SERVERS.map(env => (
                      <th key={env.id} className="text-center text-xs font-semibold text-gray-700 pb-3 px-6">
                        <span className={`inline-flex items-center gap-1.5 px-2.5 py-1 rounded-full border text-xs font-medium ${
                          env.label === 'Production'
                            ? 'bg-rose-50 border-rose-200 text-rose-700'
                            : 'bg-sky-50 border-sky-200 text-sky-700'
                        }`}>
                          <span className={`w-1.5 h-1.5 rounded-full ${env.label === 'Production' ? 'bg-rose-400' : 'bg-sky-400'}`} />
                          {env.label}
                        </span>
                      </th>
                    ))}
                  </tr>
                </thead>
                <tbody className="divide-y divide-gray-100">
                  {JENGO_ROLE_ORDER.map(roleName => {
                    const role = jengoRoles.find(r => r.name === roleName);
                    if (!role) return null;
                    const meta = JENGO_ROLE_LABELS[roleName];
                    return (
                      <tr key={role.id} className="hover:bg-gray-50 transition-colors">
                        <td className="py-3 pr-4">
                          <div className="font-medium text-gray-800 text-sm">{meta?.label ?? roleName}</div>
                          <div className="text-xs text-gray-400 mt-0.5">{meta?.desc}</div>
                        </td>
                        {JENGO_SERVERS.map(env => {
                          const on = hasJengoAccess(role.id, env.id);
                          const key = `${role.id}:${env.id}`;
                          const busy = togglingKey === key;
                          return (
                            <td key={env.id} className="py-3 px-6 text-center">
                              <button
                                onClick={() => handleJengoToggle(role.id, env.id, on)}
                                disabled={busy}
                                className={`relative inline-flex h-6 w-11 items-center rounded-full transition-colors focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:ring-offset-1 disabled:opacity-50 ${
                                  on ? 'bg-indigo-600' : 'bg-gray-200'
                                }`}
                                role="switch"
                                aria-checked={on}
                              >
                                <span className={`inline-block h-4 w-4 transform rounded-full bg-white shadow transition-transform ${on ? 'translate-x-6' : 'translate-x-1'}`} />
                              </button>
                            </td>
                          );
                        })}
                      </tr>
                    );
                  })}
                </tbody>
              </table>
            </div>
          </div>
        )}

        {/* Standard Roles section */}
        <div className="bg-white rounded-xl border border-gray-200 shadow-sm p-6 mb-6">
          <h2 className="text-sm font-semibold text-gray-700 uppercase tracking-wide mb-4">Assigned Roles</h2>

          {/* Currently assigned standard roles */}
          {(() => {
            const standardAssigned = userRoles.filter((ur: any) => {
              const roleName = ur.role?.name ?? ur.name ?? '';
              return !JENGO_ROLE_ORDER.includes(roleName);
            });
            return standardAssigned.length === 0
              ? <p className="text-sm text-gray-400 italic mb-4">No roles assigned</p>
              : (
                <div className="flex flex-wrap gap-2 mb-4">
                  {standardAssigned.map((ur: any) => {
                    const roleName = ur.role?.name ?? ur.name ?? ur.roleId ?? 'Unknown';
                    const roleId = ur.roleId ?? ur.role?.id ?? ur.id;
                    return (
                      <span key={`${roleId}-${ur.tenantId ?? 'none'}`}
                        className="inline-flex items-center gap-1.5 px-3 py-1 bg-indigo-50 text-indigo-700 text-xs font-medium rounded-full border border-indigo-100">
                        {roleName}
                        <button onClick={() => handleRevokeRole(roleId)} disabled={assigningRole === roleId}
                          className="text-indigo-400 hover:text-red-500 transition-colors ml-0.5" title="Remove role">
                          <svg className="w-3.5 h-3.5" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
                          </svg>
                        </button>
                      </span>
                    );
                  })}
                </div>
              );
          })()}

          {/* Add standard roles grouped by category */}
          {unassignedStandardRoles.length > 0 && (() => {
            const grouped = new Map<string, typeof unassignedStandardRoles>();
            for (const r of unassignedStandardRoles) {
              const cat = r.category || 'Other';
              if (!grouped.has(cat)) grouped.set(cat, []);
              grouped.get(cat)!.push(r);
            }
            return (
              <div className="space-y-3">
                <p className="text-xs text-gray-500">Add a role:</p>
                {[...grouped.entries()].map(([cat, catRoles]) => (
                  <div key={cat}>
                    <p className="text-xs font-semibold text-gray-400 uppercase tracking-wide mb-1.5">{cat}</p>
                    <div className="flex flex-wrap gap-2">
                      {catRoles.map((role) => (
                        <button key={role.id} onClick={() => handleAssignRole(role.id)} disabled={assigningRole === role.id}
                          className="inline-flex items-center gap-1.5 px-3 py-1 bg-gray-50 text-gray-600 text-xs font-medium rounded-full border border-gray-200 hover:bg-indigo-50 hover:text-indigo-700 hover:border-indigo-200 transition-colors disabled:opacity-50">
                          <svg className="w-3.5 h-3.5" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 4v16m8-8H4" />
                          </svg>
                          {role.name}
                        </button>
                      ))}
                    </div>
                  </div>
                ))}
              </div>
            );
          })()}
        </div>

        {/* Meta info */}
        <div className="bg-white rounded-xl border border-gray-200 shadow-sm p-6">
          <h2 className="text-sm font-semibold text-gray-700 uppercase tracking-wide mb-4">Account Info</h2>
          <dl className="grid grid-cols-2 gap-4">
            <div>
              <dt className="text-xs text-gray-500">Created</dt>
              <dd className="text-sm font-medium text-gray-900 mt-0.5">{new Date(user.createdAt).toLocaleString('nl-NL')}</dd>
            </div>
            <div>
              <dt className="text-xs text-gray-500">Last updated</dt>
              <dd className="text-sm font-medium text-gray-900 mt-0.5">{new Date(user.updatedAt).toLocaleString('nl-NL')}</dd>
            </div>
          </dl>
        </div>
      </div>
    </DashboardLayout>
  );
}
