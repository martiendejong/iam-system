import { useState, useEffect } from 'react';
import { useParams, useNavigate } from 'react-router-dom';
import { useForm } from 'react-hook-form';
import DashboardLayout from '../../components/layout/DashboardLayout';
import { api } from '../../services/api';
import { useAuth } from '../../context/AuthContext';
import {
  apiErrorMessage,
  isSuperAdmin,
  ownedTenantOptions,
  type TenantOption,
} from './roleTenantAccess';

interface RoleFormData {
  name: string;
  description: string;
  tenantId: string;
}

/** Select value for "Global (all tenants)"; sent to the API as no tenantId at all. */
const GLOBAL = '';

export default function RoleFormPage() {
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const isEditMode = !!id;
  const { user } = useAuth();
  const superAdmin = isSuperAdmin(user);

  const [loading, setLoading] = useState(isEditMode);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');
  const [isSystemRole, setIsSystemRole] = useState(false);
  const [tenants, setTenants] = useState<TenantOption[]>([]);
  const [tenantsLoading, setTenantsLoading] = useState(!isEditMode);

  const {
    register,
    handleSubmit,
    reset,
    formState: { errors },
  } = useForm<RoleFormData>({ defaultValues: { name: '', description: '', tenantId: GLOBAL } });

  useEffect(() => {
    if (isEditMode) {
      loadRole();
    }
  }, [id]);

  // Create mode: a SuperAdmin may pick any root tenant or Global; a BuildingOwner only the tenants they own.
  useEffect(() => {
    if (isEditMode || !user) return;
    let cancelled = false;
    const loadTenants = async () => {
      try {
        setTenantsLoading(true);
        const options: TenantOption[] = superAdmin
          ? (await api.getTenants()).map((t: any) => ({ id: t.id, name: t.name }))
          : ownedTenantOptions(user, await api.getMyTenants());
        if (cancelled) return;
        setTenants(options);
        // A single owned tenant is the only valid answer, so preselect it; a SuperAdmin defaults to Global.
        reset((prev) => ({ ...prev, tenantId: !superAdmin && options.length === 1 ? options[0].id : GLOBAL }));
      } catch (err: any) {
        if (!cancelled) setError(apiErrorMessage(err, 'Failed to load tenants'));
      } finally {
        if (!cancelled) setTenantsLoading(false);
      }
    };
    loadTenants();
    return () => {
      cancelled = true;
    };
  }, [isEditMode, user, superAdmin, reset]);

  const loadRole = async () => {
    try {
      setLoading(true);
      const data = await api.getRole(id!);
      setIsSystemRole(!!data.isSystem);
      reset({
        name: data.name,
        description: data.description || '',
        tenantId: data.tenantId ?? GLOBAL,
      });
    } catch (err: any) {
      setError(apiErrorMessage(err, 'Failed to load role'));
    } finally {
      setLoading(false);
    }
  };

  const onSubmit = async (data: RoleFormData) => {
    try {
      setSaving(true);
      setError('');

      if (isEditMode) {
        await api.updateRole(id!, { name: data.name, description: data.description });
      } else {
        await api.createRole({
          name: data.name,
          description: data.description,
          // Global = no tenantId; the API reads a missing tenant as a platform-wide role.
          ...(data.tenantId ? { tenantId: data.tenantId } : {}),
        });
      }

      navigate('/roles');
    } catch (err: any) {
      // Show the API's own reason (other tenant, duplicate name, platform role name, ...), not a generic line.
      setError(apiErrorMessage(err, `Failed to ${isEditMode ? 'update' : 'create'} role`));
    } finally {
      setSaving(false);
    }
  };

  // A BuildingOwner who owns no active tenant cannot create a role anywhere.
  const noOwnedTenant = !isEditMode && !superAdmin && !tenantsLoading && tenants.length === 0;

  if (loading) {
    return (
      <DashboardLayout>
        <div className="flex items-center justify-center min-h-screen">
          <div className="text-center">
            <div className="inline-block animate-spin rounded-full h-8 w-8 border-b-2 border-indigo-600"></div>
            <p className="mt-2 text-sm text-gray-500">Loading role...</p>
          </div>
        </div>
      </DashboardLayout>
    );
  }

  return (
    <DashboardLayout>
      <div className="max-w-3xl mx-auto px-4 sm:px-6 lg:px-8">
        {/* Header */}
        <div className="md:flex md:items-center md:justify-between">
          <div className="flex-1 min-w-0">
            <h2 className="text-2xl font-bold leading-7 text-gray-900 sm:text-3xl sm:truncate">
              {isEditMode ? 'Edit Role' : 'Create New Role'}
            </h2>
          </div>
          <div className="mt-4 flex md:mt-0 md:ml-4">
            <button
              type="button"
              onClick={() => navigate('/roles')}
              className="inline-flex items-center px-4 py-2 border border-gray-300 rounded-md shadow-sm text-sm font-medium text-gray-700 bg-white hover:bg-gray-50 focus:outline-none focus:ring-2 focus:ring-offset-2 focus:ring-indigo-500"
            >
              Cancel
            </button>
          </div>
        </div>

        {/* System Role Warning */}
        {isSystemRole && (
          <div className="mt-6 bg-yellow-50 border border-yellow-200 rounded-md p-4">
            <div className="flex">
              <div className="flex-shrink-0">
                <svg
                  className="h-5 w-5 text-yellow-400"
                  fill="currentColor"
                  viewBox="0 0 20 20"
                >
                  <path
                    fillRule="evenodd"
                    d="M8.257 3.099c.765-1.36 2.722-1.36 3.486 0l5.58 9.92c.75 1.334-.213 2.98-1.742 2.98H4.42c-1.53 0-2.493-1.646-1.743-2.98l5.58-9.92zM11 13a1 1 0 11-2 0 1 1 0 012 0zm-1-8a1 1 0 00-1 1v3a1 1 0 002 0V6a1 1 0 00-1-1z"
                    clipRule="evenodd"
                  />
                </svg>
              </div>
              <div className="ml-3">
                <p className="text-sm text-yellow-700">
                  This is a system role. You can modify the description, but the name cannot be
                  changed.
                </p>
              </div>
            </div>
          </div>
        )}

        {/* Role Form */}
        <div className="mt-6 bg-white shadow sm:rounded-lg">
          <div className="px-4 py-5 sm:p-6">
            {error && (
              <div className="mb-4 bg-red-50 border border-red-200 text-red-700 px-4 py-3 rounded">
                {error}
              </div>
            )}

            <form onSubmit={handleSubmit(onSubmit)} className="space-y-6">
              {/* Role Name */}
              <div>
                <label htmlFor="name" className="block text-sm font-medium text-gray-700">
                  Role Name *
                </label>
                <input
                  type="text"
                  id="name"
                  disabled={isSystemRole}
                  {...register('name', { required: 'Role name is required' })}
                  className={`mt-1 block w-full rounded-md shadow-sm sm:text-sm ${
                    isSystemRole
                      ? 'bg-gray-100 text-gray-500 cursor-not-allowed'
                      : errors.name
                      ? 'border-red-300 focus:ring-red-500 focus:border-red-500'
                      : 'border-gray-300 focus:ring-indigo-500 focus:border-indigo-500'
                  }`}
                />
                {errors.name && (
                  <p className="mt-1 text-sm text-red-600">{errors.name.message}</p>
                )}
                <p className="mt-1 text-xs text-gray-500">
                  A unique name for this role (e.g., Admin, Editor, Viewer)
                </p>
              </div>

              {/* Tenant (create only: a role's tenant is fixed once it exists) */}
              {!isEditMode && (
                <div>
                  <label htmlFor="tenantId" className="block text-sm font-medium text-gray-700">
                    Tenant {superAdmin ? '' : '*'}
                  </label>
                  {noOwnedTenant ? (
                    <p
                      role="alert"
                      className="mt-1 text-sm text-amber-700 bg-amber-50 border border-amber-200 rounded-md px-3 py-2"
                    >
                      You do not own an active tenant, so you cannot create roles. Ask a SuperAdmin to
                      make you a building owner of a tenant first.
                    </p>
                  ) : (
                    <select
                      id="tenantId"
                      disabled={tenantsLoading}
                      {...register('tenantId', {
                        validate: (v) => superAdmin || !!v || 'Pick one of your tenants',
                      })}
                      className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:ring-indigo-500 focus:border-indigo-500 sm:text-sm"
                    >
                      {superAdmin ? (
                        <option value={GLOBAL}>Global (all tenants)</option>
                      ) : (
                        <option value="">Select a tenant...</option>
                      )}
                      {tenants.map((t) => (
                        <option key={t.id} value={t.id}>
                          {t.name}
                        </option>
                      ))}
                    </select>
                  )}
                  {errors.tenantId && (
                    <p className="mt-1 text-sm text-red-600">{errors.tenantId.message}</p>
                  )}
                  <p className="mt-1 text-xs text-gray-500">
                    {superAdmin
                      ? 'Global roles apply to every tenant; pick a tenant to limit the role to it.'
                      : 'The role is created inside this tenant. Only tenants you own are listed.'}
                  </p>
                </div>
              )}

              {/* Description */}
              <div>
                <label htmlFor="description" className="block text-sm font-medium text-gray-700">
                  Description
                </label>
                <textarea
                  id="description"
                  rows={4}
                  {...register('description')}
                  className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:ring-indigo-500 focus:border-indigo-500 sm:text-sm"
                  placeholder="Describe what this role does and what permissions it has..."
                />
                <p className="mt-1 text-xs text-gray-500">
                  Optional description of the role's purpose and permissions
                </p>
              </div>

              {/* Info Box */}
              {!isEditMode && (
                <div className="bg-blue-50 border border-blue-200 rounded-md p-4">
                  <div className="flex">
                    <div className="flex-shrink-0">
                      <svg
                        className="h-5 w-5 text-blue-400"
                        fill="currentColor"
                        viewBox="0 0 20 20"
                      >
                        <path
                          fillRule="evenodd"
                          d="M18 10a8 8 0 11-16 0 8 8 0 0116 0zm-7-4a1 1 0 11-2 0 1 1 0 012 0zM9 9a1 1 0 000 2v3a1 1 0 001 1h1a1 1 0 100-2v-3a1 1 0 00-1-1H9z"
                          clipRule="evenodd"
                        />
                      </svg>
                    </div>
                    <div className="ml-3">
                      <p className="text-sm text-blue-700">
                        After creating this role, you can assign it to users from the Users page.
                      </p>
                    </div>
                  </div>
                </div>
              )}

              {/* Submit Button */}
              <div className="flex justify-end space-x-3">
                <button
                  type="button"
                  onClick={() => navigate('/roles')}
                  className="px-4 py-2 border border-gray-300 rounded-md shadow-sm text-sm font-medium text-gray-700 bg-white hover:bg-gray-50 focus:outline-none focus:ring-2 focus:ring-offset-2 focus:ring-indigo-500"
                >
                  Cancel
                </button>
                <button
                  type="submit"
                  disabled={saving || noOwnedTenant || (!isEditMode && tenantsLoading)}
                  className="inline-flex justify-center py-2 px-4 border border-transparent shadow-sm text-sm font-medium rounded-md text-white bg-indigo-600 hover:bg-indigo-700 focus:outline-none focus:ring-2 focus:ring-offset-2 focus:ring-indigo-500 disabled:opacity-50"
                >
                  {saving ? 'Saving...' : isEditMode ? 'Update Role' : 'Create Role'}
                </button>
              </div>
            </form>
          </div>
        </div>
      </div>
    </DashboardLayout>
  );
}
