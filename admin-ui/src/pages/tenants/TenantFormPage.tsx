import { useState, useEffect } from 'react';
import { useParams, useNavigate } from 'react-router-dom';
import { useForm } from 'react-hook-form';
import DashboardLayout from '../../components/layout/DashboardLayout';
import { api } from '../../services/api';
import type { TenantTypeValue } from '../../types';

interface TenantFormData {
  name: string;
  type: TenantTypeValue;
  parentId: string;
}

export default function TenantFormPage() {
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const isEditMode = !!id;

  const [loading, setLoading] = useState(isEditMode);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');
  const [tenants, setTenants] = useState<any[]>([]);

  const {
    register,
    handleSubmit,
    reset,
    watch,
    formState: { errors },
  } = useForm<TenantFormData>({
    defaultValues: {
      parentId: '',
    },
  });

  const selectedType = watch('type');

  useEffect(() => {
    loadTenants();
    if (isEditMode) {
      loadTenant();
    }
  }, [id]);

  const loadTenants = async () => {
    try {
      const data = await api.getTenants();
      setTenants(data);
    } catch (err) {
      console.error('Failed to load tenants for parent selection:', err);
    }
  };

  const loadTenant = async () => {
    try {
      setLoading(true);
      const data = await api.getTenant(id!);
      reset({
        name: data.name,
        type: data.type,
        parentId: data.parentId || '',
      });
    } catch (err: any) {
      setError(err.response?.data?.message || 'Failed to load tenant');
    } finally {
      setLoading(false);
    }
  };

  const onSubmit = async (data: TenantFormData) => {
    try {
      setSaving(true);
      setError('');

      const payload = {
        name: data.name,
        type: data.type,
        parentId: data.parentId || null,
      };

      if (isEditMode) {
        await api.updateTenant(id!, payload);
      } else {
        await api.createTenant(payload);
      }

      navigate('/tenants');
    } catch (err: any) {
      setError(err.response?.data?.message || `Failed to ${isEditMode ? 'update' : 'create'} tenant`);
    } finally {
      setSaving(false);
    }
  };

  // Filter valid parent tenants based on hierarchy
  const getValidParents = () => {
    if (!selectedType) return [];

    // Define hierarchy rules
    const hierarchyRules: Record<TenantTypeValue, TenantTypeValue[]> = {
      Organization: [],
      Server: [],
      Building: ['Organization'],
      Floor: ['Building'],
      Room: ['Floor'],
      Device: ['Room'],
    };

    const allowedParentTypes = hierarchyRules[selectedType];
    if (allowedParentTypes.length === 0) return [];

    return tenants.filter((t) =>
      allowedParentTypes.includes(t.type) && t.id !== id // Exclude self
    );
  };

  const validParents = getValidParents();

  if (loading) {
    return (
      <DashboardLayout>
        <div className="flex items-center justify-center min-h-screen">
          <div className="text-center">
            <div className="inline-block animate-spin rounded-full h-8 w-8 border-b-2 border-indigo-600"></div>
            <p className="mt-2 text-sm text-gray-500">Loading tenant...</p>
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
              {isEditMode ? 'Edit Tenant' : 'Create New Tenant'}
            </h2>
          </div>
          <div className="mt-4 flex md:mt-0 md:ml-4">
            <button
              type="button"
              onClick={() => navigate('/tenants')}
              className="inline-flex items-center px-4 py-2 border border-gray-300 rounded-md shadow-sm text-sm font-medium text-gray-700 bg-white hover:bg-gray-50 focus:outline-none focus:ring-2 focus:ring-offset-2 focus:ring-indigo-500"
            >
              Cancel
            </button>
          </div>
        </div>

        {/* Info Box */}
        <div className="mt-6 bg-blue-50 border border-blue-200 rounded-md p-4">
          <div className="flex">
            <div className="flex-shrink-0">
              <svg className="h-5 w-5 text-blue-400" fill="currentColor" viewBox="0 0 20 20">
                <path
                  fillRule="evenodd"
                  d="M18 10a8 8 0 11-16 0 8 8 0 0116 0zm-7-4a1 1 0 11-2 0 1 1 0 012 0zM9 9a1 1 0 000 2v3a1 1 0 001 1h1a1 1 0 100-2v-3a1 1 0 00-1-1H9z"
                  clipRule="evenodd"
                />
              </svg>
            </div>
            <div className="ml-3">
              <p className="text-sm text-blue-700">
                <strong>Hierarchy:</strong> Organization → Building → Floor → Room → Device
              </p>
            </div>
          </div>
        </div>

        {/* Tenant Form */}
        <div className="mt-6 bg-white shadow sm:rounded-lg">
          <div className="px-4 py-5 sm:p-6">
            {error && (
              <div className="mb-4 bg-red-50 border border-red-200 text-red-700 px-4 py-3 rounded">
                {error}
              </div>
            )}

            <form onSubmit={handleSubmit(onSubmit)} className="space-y-6">
              {/* Tenant Name */}
              <div>
                <label htmlFor="name" className="block text-sm font-medium text-gray-700">
                  Tenant Name *
                </label>
                <input
                  type="text"
                  id="name"
                  {...register('name', { required: 'Tenant name is required' })}
                  className={`mt-1 block w-full rounded-md shadow-sm sm:text-sm ${
                    errors.name
                      ? 'border-red-300 focus:ring-red-500 focus:border-red-500'
                      : 'border-gray-300 focus:ring-indigo-500 focus:border-indigo-500'
                  }`}
                  placeholder="e.g., Main Office, Building A, Floor 1, Room 101"
                />
                {errors.name && (
                  <p className="mt-1 text-sm text-red-600">{errors.name.message}</p>
                )}
              </div>

              {/* Tenant Type */}
              <div>
                <label htmlFor="type" className="block text-sm font-medium text-gray-700">
                  Tenant Type *
                </label>
                <select
                  id="type"
                  {...register('type', { required: 'Tenant type is required' })}
                  className={`mt-1 block w-full rounded-md shadow-sm sm:text-sm ${
                    errors.type
                      ? 'border-red-300 focus:ring-red-500 focus:border-red-500'
                      : 'border-gray-300 focus:ring-indigo-500 focus:border-indigo-500'
                  }`}
                >
                  <option value="">Select a type</option>
                  <option value="Organization">Organization</option>
                  <option value="Building">Building</option>
                  <option value="Floor">Floor</option>
                  <option value="Room">Room</option>
                  <option value="Device">Device</option>
                </select>
                {errors.type && (
                  <p className="mt-1 text-sm text-red-600">{errors.type.message}</p>
                )}
                <p className="mt-1 text-xs text-gray-500">
                  Select the type based on the hierarchy level
                </p>
              </div>

              {/* Parent Tenant */}
              {selectedType && selectedType !== 'Organization' && (
                <div>
                  <label htmlFor="parentId" className="block text-sm font-medium text-gray-700">
                    Parent Tenant *
                  </label>
                  <select
                    id="parentId"
                    {...register('parentId', {
                      required: 'Parent tenant is required',
                    })}
                    className={`mt-1 block w-full rounded-md shadow-sm sm:text-sm ${
                      errors.parentId
                        ? 'border-red-300 focus:ring-red-500 focus:border-red-500'
                        : 'border-gray-300 focus:ring-indigo-500 focus:border-indigo-500'
                    }`}
                  >
                    <option value="">Select a parent</option>
                    {validParents.map((tenant) => (
                      <option key={tenant.id} value={tenant.id}>
                        {tenant.name} ({tenant.type})
                      </option>
                    ))}
                  </select>
                  {errors.parentId && (
                    <p className="mt-1 text-sm text-red-600">{errors.parentId.message}</p>
                  )}
                  {validParents.length === 0 && (
                    <p className="mt-1 text-xs text-yellow-600">
                      No valid parent tenants found. Please create a parent tenant first.
                    </p>
                  )}
                  <p className="mt-1 text-xs text-gray-500">
                    {selectedType === 'Building' && 'Must be under an Organization'}
                    {selectedType === 'Floor' && 'Must be under a Building'}
                    {selectedType === 'Room' && 'Must be under a Floor'}
                    {selectedType === 'Device' && 'Must be under a Room'}
                  </p>
                </div>
              )}

              {/* Type Descriptions */}
              <div className="bg-gray-50 rounded-md p-4 text-sm">
                <h4 className="font-medium text-gray-900 mb-2">Tenant Type Descriptions:</h4>
                <ul className="space-y-1 text-gray-600">
                  <li><strong>Organization:</strong> Top-level entity (e.g., company, institution)</li>
                  <li><strong>Building:</strong> Physical buildings within an organization</li>
                  <li><strong>Floor:</strong> Individual floors within a building</li>
                  <li><strong>Room:</strong> Rooms or units within a floor</li>
                  <li><strong>Device:</strong> IoT devices or equipment within a room</li>
                </ul>
              </div>

              {/* Submit Button */}
              <div className="flex justify-end space-x-3">
                <button
                  type="button"
                  onClick={() => navigate('/tenants')}
                  className="px-4 py-2 border border-gray-300 rounded-md shadow-sm text-sm font-medium text-gray-700 bg-white hover:bg-gray-50 focus:outline-none focus:ring-2 focus:ring-offset-2 focus:ring-indigo-500"
                >
                  Cancel
                </button>
                <button
                  type="submit"
                  disabled={saving || (selectedType !== 'Organization' && validParents.length === 0)}
                  className="inline-flex justify-center py-2 px-4 border border-transparent shadow-sm text-sm font-medium rounded-md text-white bg-indigo-600 hover:bg-indigo-700 focus:outline-none focus:ring-2 focus:ring-offset-2 focus:ring-indigo-500 disabled:opacity-50"
                >
                  {saving ? 'Saving...' : isEditMode ? 'Update Tenant' : 'Create Tenant'}
                </button>
              </div>
            </form>
          </div>
        </div>
      </div>
    </DashboardLayout>
  );
}
