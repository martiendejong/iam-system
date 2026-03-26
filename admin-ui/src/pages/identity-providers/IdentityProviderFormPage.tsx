import { useState, useEffect } from 'react';
import { useParams, useNavigate } from 'react-router-dom';
import { useForm } from 'react-hook-form';
import DashboardLayout from '../../components/layout/DashboardLayout';
import { api } from '../../services/api';

interface IdentityProviderFormData {
  name: string;
  displayName: string;
  type: string;
  tenantId: string;
  clientId: string;
  clientSecret: string;
  metadataUrl: string;
  autoCreateUsers: boolean;
  defaultRoleId: string;
  attributeMapping: string;
  isActive: boolean;
}

export default function IdentityProviderFormPage() {
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const isEditMode = !!id;

  const [loading, setLoading] = useState(isEditMode);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');
  const [tenants, setTenants] = useState<any[]>([]);
  const [roles, setRoles] = useState<any[]>([]);

  const {
    register,
    handleSubmit,
    reset,
    watch,
    formState: { errors },
  } = useForm<IdentityProviderFormData>({
    defaultValues: {
      autoCreateUsers: true,
      isActive: true,
      type: 'Google',
      attributeMapping: '{}',
    },
  });

  const selectedType = watch('type');

  useEffect(() => {
    loadDependencies();
    if (isEditMode) {
      loadProvider();
    }
  }, [id]);

  const loadDependencies = async () => {
    try {
      const [tenantsData, rolesData] = await Promise.all([
        api.getTenants(),
        api.getRoles(),
      ]);
      setTenants(tenantsData);
      setRoles(rolesData);
    } catch {
      // Dependencies are optional, continue without them
    }
  };

  const loadProvider = async () => {
    try {
      setLoading(true);
      const data = await api.getIdentityProvider(id!);
      reset({
        name: data.name,
        displayName: data.displayName,
        type: data.type,
        tenantId: data.tenantId || '',
        clientId: data.clientId,
        clientSecret: '',
        metadataUrl: data.metadataUrl || '',
        autoCreateUsers: data.autoCreateUsers,
        defaultRoleId: data.defaultRoleId || '',
        attributeMapping: data.attributeMapping || '{}',
        isActive: data.isActive,
      });
    } catch (err: any) {
      setError(err.response?.data?.message || 'Failed to load identity provider');
    } finally {
      setLoading(false);
    }
  };

  const onSubmit = async (data: IdentityProviderFormData) => {
    try {
      setSaving(true);
      setError('');

      // Validate attribute mapping JSON
      if (data.attributeMapping) {
        try {
          JSON.parse(data.attributeMapping);
        } catch {
          setError('Attribute mapping must be valid JSON');
          setSaving(false);
          return;
        }
      }

      const payload = {
        name: data.name,
        displayName: data.displayName,
        type: data.type,
        tenantId: data.tenantId || null,
        clientId: data.clientId,
        clientSecret: data.clientSecret || undefined,
        metadataUrl: data.metadataUrl || null,
        autoCreateUsers: data.autoCreateUsers,
        defaultRoleId: data.defaultRoleId || null,
        attributeMapping: data.attributeMapping || null,
        isActive: data.isActive,
      };

      if (isEditMode) {
        await api.updateIdentityProvider(id!, payload);
      } else {
        await api.createIdentityProvider(payload);
      }

      navigate('/identity-providers');
    } catch (err: any) {
      setError(err.response?.data?.error || `Failed to ${isEditMode ? 'update' : 'create'} identity provider`);
    } finally {
      setSaving(false);
    }
  };

  if (loading) {
    return (
      <DashboardLayout>
        <div className="flex items-center justify-center min-h-screen">
          <div className="text-center">
            <div className="inline-block animate-spin rounded-full h-8 w-8 border-b-2 border-indigo-600"></div>
            <p className="mt-2 text-sm text-gray-500">Loading identity provider...</p>
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
              {isEditMode ? 'Edit Identity Provider' : 'Add Identity Provider'}
            </h2>
          </div>
          <div className="mt-4 flex md:mt-0 md:ml-4">
            <button
              type="button"
              onClick={() => navigate('/identity-providers')}
              className="inline-flex items-center px-4 py-2 border border-gray-300 rounded-md shadow-sm text-sm font-medium text-gray-700 bg-white hover:bg-gray-50 focus:outline-none focus:ring-2 focus:ring-offset-2 focus:ring-indigo-500"
            >
              Cancel
            </button>
          </div>
        </div>

        {/* Form */}
        <div className="mt-6 bg-white shadow sm:rounded-lg">
          <div className="px-4 py-5 sm:p-6">
            {error && (
              <div className="mb-4 bg-red-50 border border-red-200 text-red-700 px-4 py-3 rounded">
                {error}
              </div>
            )}

            <form onSubmit={handleSubmit(onSubmit)} className="space-y-6">
              {/* Name */}
              <div>
                <label htmlFor="name" className="block text-sm font-medium text-gray-700">
                  Name *
                </label>
                <input
                  type="text"
                  id="name"
                  {...register('name', { required: 'Name is required' })}
                  className={`mt-1 block w-full rounded-md shadow-sm sm:text-sm ${
                    errors.name
                      ? 'border-red-300 focus:ring-red-500 focus:border-red-500'
                      : 'border-gray-300 focus:ring-indigo-500 focus:border-indigo-500'
                  }`}
                  placeholder="e.g., google, azure-ad-contoso"
                />
                {errors.name && (
                  <p className="mt-1 text-sm text-red-600">{errors.name.message}</p>
                )}
                <p className="mt-1 text-xs text-gray-500">Internal identifier for this provider</p>
              </div>

              {/* Display Name */}
              <div>
                <label htmlFor="displayName" className="block text-sm font-medium text-gray-700">
                  Display Name *
                </label>
                <input
                  type="text"
                  id="displayName"
                  {...register('displayName', { required: 'Display name is required' })}
                  className={`mt-1 block w-full rounded-md shadow-sm sm:text-sm ${
                    errors.displayName
                      ? 'border-red-300 focus:ring-red-500 focus:border-red-500'
                      : 'border-gray-300 focus:ring-indigo-500 focus:border-indigo-500'
                  }`}
                  placeholder="e.g., Sign in with Google"
                />
                {errors.displayName && (
                  <p className="mt-1 text-sm text-red-600">{errors.displayName.message}</p>
                )}
                <p className="mt-1 text-xs text-gray-500">Name shown to users on the login page</p>
              </div>

              {/* Type */}
              <div>
                <label htmlFor="type" className="block text-sm font-medium text-gray-700">
                  Provider Type *
                </label>
                <select
                  id="type"
                  {...register('type', { required: 'Provider type is required' })}
                  className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:ring-indigo-500 focus:border-indigo-500 sm:text-sm"
                >
                  <option value="Google">Google</option>
                  <option value="Microsoft">Microsoft</option>
                  <option value="GitHub">GitHub</option>
                  <option value="Apple">Apple</option>
                  <option value="SAML">SAML 2.0</option>
                  <option value="OIDC">OpenID Connect</option>
                </select>
              </div>

              {/* Tenant */}
              <div>
                <label htmlFor="tenantId" className="block text-sm font-medium text-gray-700">
                  Tenant
                </label>
                <select
                  id="tenantId"
                  {...register('tenantId')}
                  className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:ring-indigo-500 focus:border-indigo-500 sm:text-sm"
                >
                  <option value="">Global (all tenants)</option>
                  {tenants.map((tenant) => (
                    <option key={tenant.id} value={tenant.id}>
                      {tenant.name}
                    </option>
                  ))}
                </select>
                <p className="mt-1 text-xs text-gray-500">
                  Leave empty to make this provider available to all tenants
                </p>
              </div>

              {/* Client ID */}
              <div>
                <label htmlFor="clientId" className="block text-sm font-medium text-gray-700">
                  Client ID *
                </label>
                <input
                  type="text"
                  id="clientId"
                  {...register('clientId', { required: 'Client ID is required' })}
                  className={`mt-1 block w-full rounded-md shadow-sm sm:text-sm ${
                    errors.clientId
                      ? 'border-red-300 focus:ring-red-500 focus:border-red-500'
                      : 'border-gray-300 focus:ring-indigo-500 focus:border-indigo-500'
                  }`}
                  placeholder="OAuth2/OIDC Client ID"
                />
                {errors.clientId && (
                  <p className="mt-1 text-sm text-red-600">{errors.clientId.message}</p>
                )}
              </div>

              {/* Client Secret */}
              <div>
                <label htmlFor="clientSecret" className="block text-sm font-medium text-gray-700">
                  Client Secret {!isEditMode && '*'}
                </label>
                <input
                  type="password"
                  id="clientSecret"
                  {...register('clientSecret', {
                    required: !isEditMode ? 'Client secret is required' : false,
                  })}
                  className={`mt-1 block w-full rounded-md shadow-sm sm:text-sm ${
                    errors.clientSecret
                      ? 'border-red-300 focus:ring-red-500 focus:border-red-500'
                      : 'border-gray-300 focus:ring-indigo-500 focus:border-indigo-500'
                  }`}
                  placeholder={isEditMode ? 'Leave blank to keep current secret' : 'OAuth2/OIDC Client Secret'}
                />
                {errors.clientSecret && (
                  <p className="mt-1 text-sm text-red-600">{errors.clientSecret.message}</p>
                )}
              </div>

              {/* Metadata URL - shown for SAML/OIDC */}
              {(selectedType === 'SAML' || selectedType === 'OIDC') && (
                <div>
                  <label htmlFor="metadataUrl" className="block text-sm font-medium text-gray-700">
                    Metadata URL
                  </label>
                  <input
                    type="url"
                    id="metadataUrl"
                    {...register('metadataUrl')}
                    className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:ring-indigo-500 focus:border-indigo-500 sm:text-sm"
                    placeholder="https://idp.example.com/.well-known/openid-configuration"
                  />
                  <p className="mt-1 text-xs text-gray-500">
                    {selectedType === 'SAML'
                      ? 'SAML metadata endpoint URL'
                      : 'OpenID Connect discovery document URL'}
                  </p>
                </div>
              )}

              {/* Auto Create Users */}
              <div className="flex items-center">
                <input
                  type="checkbox"
                  id="autoCreateUsers"
                  {...register('autoCreateUsers')}
                  className="h-4 w-4 rounded border-gray-300 text-indigo-600 focus:ring-indigo-500"
                />
                <label htmlFor="autoCreateUsers" className="ml-2 block text-sm text-gray-900">
                  Automatically create users on first login
                </label>
              </div>

              {/* Default Role */}
              <div>
                <label htmlFor="defaultRoleId" className="block text-sm font-medium text-gray-700">
                  Default Role
                </label>
                <select
                  id="defaultRoleId"
                  {...register('defaultRoleId')}
                  className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:ring-indigo-500 focus:border-indigo-500 sm:text-sm"
                >
                  <option value="">No default role</option>
                  {roles.map((role) => (
                    <option key={role.id} value={role.id}>
                      {role.name}
                    </option>
                  ))}
                </select>
                <p className="mt-1 text-xs text-gray-500">
                  Role automatically assigned to users created via this provider
                </p>
              </div>

              {/* Attribute Mapping */}
              <div>
                <label htmlFor="attributeMapping" className="block text-sm font-medium text-gray-700">
                  Attribute Mapping (JSON)
                </label>
                <textarea
                  id="attributeMapping"
                  rows={4}
                  {...register('attributeMapping')}
                  className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:ring-indigo-500 focus:border-indigo-500 sm:text-sm font-mono text-sm"
                  placeholder='{"email": "mail", "firstName": "given_name", "lastName": "family_name"}'
                />
                <p className="mt-1 text-xs text-gray-500">
                  Map external provider attributes to IAM user fields. Use JSON format.
                </p>
              </div>

              {/* Active */}
              <div className="flex items-center">
                <input
                  type="checkbox"
                  id="isActive"
                  {...register('isActive')}
                  className="h-4 w-4 rounded border-gray-300 text-indigo-600 focus:ring-indigo-500"
                />
                <label htmlFor="isActive" className="ml-2 block text-sm text-gray-900">
                  Provider is active
                </label>
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
                        After creating this provider, users will see a social login button on the login page.
                        Make sure to configure the OAuth2 redirect URI in the external provider's settings.
                      </p>
                    </div>
                  </div>
                </div>
              )}

              {/* Submit Button */}
              <div className="flex justify-end space-x-3">
                <button
                  type="button"
                  onClick={() => navigate('/identity-providers')}
                  className="px-4 py-2 border border-gray-300 rounded-md shadow-sm text-sm font-medium text-gray-700 bg-white hover:bg-gray-50 focus:outline-none focus:ring-2 focus:ring-offset-2 focus:ring-indigo-500"
                >
                  Cancel
                </button>
                <button
                  type="submit"
                  disabled={saving}
                  className="inline-flex justify-center py-2 px-4 border border-transparent shadow-sm text-sm font-medium rounded-md text-white bg-indigo-600 hover:bg-indigo-700 focus:outline-none focus:ring-2 focus:ring-offset-2 focus:ring-indigo-500 disabled:opacity-50"
                >
                  {saving ? 'Saving...' : isEditMode ? 'Update Provider' : 'Create Provider'}
                </button>
              </div>
            </form>
          </div>
        </div>
      </div>
    </DashboardLayout>
  );
}
