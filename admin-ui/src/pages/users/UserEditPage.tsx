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
  const [assigningRole, setAssigningRole] = useState<string | null>(null);

  const {
    register,
    handleSubmit,
    reset,
    formState: { errors },
  } = useForm<UserFormData>();

  useEffect(() => {
    if (id) {
      Promise.all([loadUser(), loadRoles()]);
    }
  }, [id]);

  const loadUser = async () => {
    try {
      setLoading(true);
      const data = await api.getUser(id!);
      setUser(data);
      reset({
        firstName: data.firstName,
        lastName: data.lastName,
        email: data.email,
        phoneNumber: data.phoneNumber || '',
      });
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
    } catch {
      // ignore
    }
  };

  const onSubmit = async (data: UserFormData) => {
    try {
      setSaving(true);
      setError('');
      setSuccess('');
      await api.updateUser(id!, data);
      setSuccess('User updated successfully');
    } catch (err: any) {
      setError(err.response?.data?.message || 'Failed to update user');
    } finally {
      setSaving(false);
    }
  };

  const handleAssignRole = async (roleId: string) => {
    try {
      setAssigningRole(roleId);
      await api.assignRole(roleId, id!);
      const roles = await api.getUserRoles(id!);
      setUserRoles(Array.isArray(roles) ? roles : []);
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
      const roles = await api.getUserRoles(id!);
      setUserRoles(Array.isArray(roles) ? roles : []);
    } catch (err: any) {
      alert(err.response?.data?.message || 'Failed to revoke role');
    } finally {
      setAssigningRole(null);
    }
  };

  const assignedRoleIds = new Set(userRoles.map((ur: any) => ur.roleId ?? ur.role?.id ?? ur.id));
  const unassignedRoles = allRoles.filter(r => !assignedRoleIds.has(r.id));

  if (loading) {
    return (
      <DashboardLayout>
        <div className="flex flex-col items-center justify-center py-24 text-gray-400">
          <div className="w-8 h-8 border-2 border-indigo-500 border-t-transparent rounded-full animate-spin mb-3" />
          <span className="text-sm">Loading user...</span>
        </div>
      </DashboardLayout>
    );
  }

  if (!user) {
    return (
      <DashboardLayout>
        <div className="text-center py-16">
          <p className="text-gray-500 mb-4">User not found</p>
          <button onClick={() => navigate('/users')} className="text-indigo-600 hover:underline text-sm">
            Back to users
          </button>
        </div>
      </DashboardLayout>
    );
  }

  const initials = `${user.firstName?.[0] ?? ''}${user.lastName?.[0] ?? ''}`.toUpperCase();

  return (
    <DashboardLayout>
      <div className="max-w-3xl mx-auto">
        {/* Back link */}
        <button
          onClick={() => navigate('/users')}
          className="flex items-center gap-1 text-sm text-gray-500 hover:text-gray-700 mb-6 transition-colors"
        >
          <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M15 19l-7-7 7-7" />
          </svg>
          Back to users
        </button>

        {/* User header */}
        <div className="flex items-center gap-4 mb-6">
          <div className="w-14 h-14 bg-indigo-100 text-indigo-700 rounded-full flex items-center justify-center text-lg font-bold">
            {initials}
          </div>
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

          {error && (
            <div className="mb-4 p-3 bg-red-50 border border-red-200 text-red-700 rounded-lg text-sm">{error}</div>
          )}
          {success && (
            <div className="mb-4 p-3 bg-green-50 border border-green-200 text-green-700 rounded-lg text-sm">{success}</div>
          )}

          <form onSubmit={handleSubmit(onSubmit)} className="space-y-4">
            <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
              <div>
                <label className="block text-sm font-medium text-gray-700 mb-1">First Name *</label>
                <input
                  type="text"
                  {...register('firstName', { required: 'Required' })}
                  className={`block w-full border rounded-lg px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-transparent ${errors.firstName ? 'border-red-300' : 'border-gray-300'}`}
                />
                {errors.firstName && <p className="mt-1 text-xs text-red-600">{errors.firstName.message}</p>}
              </div>
              <div>
                <label className="block text-sm font-medium text-gray-700 mb-1">Last Name *</label>
                <input
                  type="text"
                  {...register('lastName', { required: 'Required' })}
                  className={`block w-full border rounded-lg px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-transparent ${errors.lastName ? 'border-red-300' : 'border-gray-300'}`}
                />
                {errors.lastName && <p className="mt-1 text-xs text-red-600">{errors.lastName.message}</p>}
              </div>
            </div>

            <div>
              <label className="block text-sm font-medium text-gray-700 mb-1">Email *</label>
              <input
                type="email"
                {...register('email', { required: 'Required' })}
                className={`block w-full border rounded-lg px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-transparent ${errors.email ? 'border-red-300' : 'border-gray-300'}`}
              />
              {errors.email && <p className="mt-1 text-xs text-red-600">{errors.email.message}</p>}
            </div>

            <div>
              <label className="block text-sm font-medium text-gray-700 mb-1">Phone Number</label>
              <input
                type="tel"
                {...register('phoneNumber')}
                className="block w-full border border-gray-300 rounded-lg px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-transparent"
              />
            </div>

            <div className="flex justify-end pt-2">
              <button
                type="submit"
                disabled={saving}
                className="px-5 py-2 bg-indigo-600 text-white text-sm font-medium rounded-lg hover:bg-indigo-700 transition-colors disabled:opacity-50 shadow-sm"
              >
                {saving ? 'Saving...' : 'Save Changes'}
              </button>
            </div>
          </form>
        </div>

        {/* Roles section */}
        <div className="bg-white rounded-xl border border-gray-200 shadow-sm p-6 mb-6">
          <h2 className="text-sm font-semibold text-gray-700 uppercase tracking-wide mb-4">Assigned Roles</h2>
          {userRoles.length === 0 ? (
            <p className="text-sm text-gray-400 italic">No roles assigned</p>
          ) : (
            <div className="flex flex-wrap gap-2 mb-4">
              {userRoles.map((ur: any) => {
                const roleName = ur.role?.name ?? ur.name ?? ur.roleId ?? 'Unknown';
                const roleId = ur.roleId ?? ur.role?.id ?? ur.id;
                return (
                  <span key={roleId} className="inline-flex items-center gap-1.5 px-3 py-1 bg-indigo-50 text-indigo-700 text-xs font-medium rounded-full border border-indigo-100">
                    {roleName}
                    <button
                      onClick={() => handleRevokeRole(roleId)}
                      disabled={assigningRole === roleId}
                      className="text-indigo-400 hover:text-red-500 transition-colors ml-0.5"
                      title="Remove role"
                    >
                      <svg className="w-3.5 h-3.5" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                        <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
                      </svg>
                    </button>
                  </span>
                );
              })}
            </div>
          )}

          {unassignedRoles.length > 0 && (
            <div>
              <p className="text-xs text-gray-500 mb-2">Add a role:</p>
              <div className="flex flex-wrap gap-2">
                {unassignedRoles.map((role) => (
                  <button
                    key={role.id}
                    onClick={() => handleAssignRole(role.id)}
                    disabled={assigningRole === role.id}
                    className="inline-flex items-center gap-1.5 px-3 py-1 bg-gray-50 text-gray-600 text-xs font-medium rounded-full border border-gray-200 hover:bg-indigo-50 hover:text-indigo-700 hover:border-indigo-200 transition-colors disabled:opacity-50"
                  >
                    <svg className="w-3.5 h-3.5" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 4v16m8-8H4" />
                    </svg>
                    {role.name}
                  </button>
                ))}
              </div>
            </div>
          )}
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
