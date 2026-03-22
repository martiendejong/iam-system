import { useState, useEffect } from 'react';
import { useParams, useNavigate } from 'react-router-dom';
import { useForm } from 'react-hook-form';
import DashboardLayout from '../../components/layout/DashboardLayout';
import { api } from '../../services/api';

interface ClientFormData {
  displayName: string;
  type: 'confidential' | 'public';
  redirectUris: string;
  postLogoutRedirectUris: string;
  scopes: string[];
  grantTypes: string[];
}

const AVAILABLE_SCOPES = [
  { value: 'openid', label: 'OpenID Connect' },
  { value: 'profile', label: 'Profile' },
  { value: 'email', label: 'Email' },
  { value: 'offline_access', label: 'Offline Access (Refresh Tokens)' },
];

const AVAILABLE_GRANT_TYPES = [
  { value: 'authorization_code', label: 'Authorization Code' },
  { value: 'client_credentials', label: 'Client Credentials' },
  { value: 'refresh_token', label: 'Refresh Token' },
];

export default function ClientFormPage() {
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const isEditMode = !!id;

  const [loading, setLoading] = useState(isEditMode);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');
  const [clientSecret, setClientSecret] = useState<string | null>(null);
  const [showSecretCopied, setShowSecretCopied] = useState(false);

  const {
    register,
    handleSubmit,
    reset,
    watch,
    setValue,
    formState: { errors },
  } = useForm<ClientFormData>({
    defaultValues: {
      type: 'confidential',
      scopes: ['openid', 'profile', 'email'],
      grantTypes: ['authorization_code', 'refresh_token'],
    },
  });

  const selectedType = watch('type');
  const selectedScopes = watch('scopes');
  const selectedGrantTypes = watch('grantTypes');

  useEffect(() => {
    if (isEditMode) {
      loadClient();
    }
  }, [id]);

  const loadClient = async () => {
    try {
      setLoading(true);
      const data = await api.getOAuth2Client(id!);
      reset({
        displayName: data.displayName,
        type: data.type,
        redirectUris: data.redirectUris.join('\n'),
        postLogoutRedirectUris: data.postLogoutRedirectUris.join('\n'),
        scopes: data.permissions.map((p: string) => p.replace('scp:', '')),
        grantTypes: data.grantTypes || [],
      });
    } catch (err: any) {
      setError(err.response?.data?.message || 'Failed to load client');
    } finally {
      setLoading(false);
    }
  };

  const onSubmit = async (data: ClientFormData) => {
    try {
      setSaving(true);
      setError('');

      const payload = {
        displayName: data.displayName,
        type: data.type,
        redirectUris: data.redirectUris
          .split('\n')
          .map((uri) => uri.trim())
          .filter((uri) => uri),
        postLogoutRedirectUris: data.postLogoutRedirectUris
          .split('\n')
          .map((uri) => uri.trim())
          .filter((uri) => uri),
        permissions: data.scopes.map((scope) => `scp:${scope}`),
        grantTypes: data.grantTypes,
      };

      if (isEditMode) {
        await api.updateOAuth2Client(id!, payload);
        navigate('/oauth/clients');
      } else {
        const response = await api.createOAuth2Client(payload);
        if (response.clientSecret) {
          setClientSecret(response.clientSecret);
        } else {
          navigate('/oauth/clients');
        }
      }
    } catch (err: any) {
      setError(err.response?.data?.message || `Failed to ${isEditMode ? 'update' : 'create'} client`);
    } finally {
      setSaving(false);
    }
  };

  const handleCopySecret = () => {
    if (clientSecret) {
      navigator.clipboard.writeText(clientSecret);
      setShowSecretCopied(true);
      setTimeout(() => setShowSecretCopied(false), 2000);
    }
  };

  const toggleScope = (scope: string) => {
    const current = selectedScopes || [];
    if (current.includes(scope)) {
      setValue('scopes', current.filter((s) => s !== scope));
    } else {
      setValue('scopes', [...current, scope]);
    }
  };

  const toggleGrantType = (grantType: string) => {
    const current = selectedGrantTypes || [];
    if (current.includes(grantType)) {
      setValue('grantTypes', current.filter((g) => g !== grantType));
    } else {
      setValue('grantTypes', [...current, grantType]);
    }
  };

  // Show client secret after creation
  if (clientSecret) {
    return (
      <DashboardLayout>
        <div className="max-w-3xl mx-auto px-4 sm:px-6 lg:px-8">
          <div className="bg-white shadow sm:rounded-lg">
            <div className="px-4 py-5 sm:p-6">
              <h3 className="text-lg leading-6 font-medium text-gray-900 mb-4">
                Client Created Successfully!
              </h3>

              <div className="bg-yellow-50 border border-yellow-200 rounded-md p-4 mb-4">
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
                      <strong>Important:</strong> Save the client secret below. You won't be able to see
                      it again!
                    </p>
                  </div>
                </div>
              </div>

              <div className="space-y-4">
                <div>
                  <label className="block text-sm font-medium text-gray-700">Client Secret</label>
                  <div className="mt-1 flex rounded-md shadow-sm">
                    <input
                      type="text"
                      value={clientSecret}
                      readOnly
                      className="flex-1 min-w-0 block w-full px-3 py-2 rounded-l-md border-gray-300 bg-gray-50 font-mono text-sm"
                    />
                    <button
                      type="button"
                      onClick={handleCopySecret}
                      className="inline-flex items-center px-4 py-2 border border-l-0 border-gray-300 rounded-r-md bg-gray-50 text-sm font-medium text-gray-700 hover:bg-gray-100"
                    >
                      {showSecretCopied ? 'Copied!' : 'Copy'}
                    </button>
                  </div>
                </div>

                <button
                  onClick={() => navigate('/oauth/clients')}
                  className="w-full inline-flex justify-center py-2 px-4 border border-transparent shadow-sm text-sm font-medium rounded-md text-white bg-indigo-600 hover:bg-indigo-700"
                >
                  Done
                </button>
              </div>
            </div>
          </div>
        </div>
      </DashboardLayout>
    );
  }

  if (loading) {
    return (
      <DashboardLayout>
        <div className="flex items-center justify-center min-h-screen">
          <div className="text-center">
            <div className="inline-block animate-spin rounded-full h-8 w-8 border-b-2 border-indigo-600"></div>
            <p className="mt-2 text-sm text-gray-500">Loading client...</p>
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
              {isEditMode ? 'Edit OAuth2 Client' : 'Register New Client'}
            </h2>
          </div>
          <div className="mt-4 flex md:mt-0 md:ml-4">
            <button
              type="button"
              onClick={() => navigate('/oauth/clients')}
              className="inline-flex items-center px-4 py-2 border border-gray-300 rounded-md shadow-sm text-sm font-medium text-gray-700 bg-white hover:bg-gray-50"
            >
              Cancel
            </button>
          </div>
        </div>

        {/* Client Form */}
        <div className="mt-6 bg-white shadow sm:rounded-lg">
          <div className="px-4 py-5 sm:p-6">
            {error && (
              <div className="mb-4 bg-red-50 border border-red-200 text-red-700 px-4 py-3 rounded">
                {error}
              </div>
            )}

            <form onSubmit={handleSubmit(onSubmit)} className="space-y-6">
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
                  placeholder="My Application"
                />
                {errors.displayName && (
                  <p className="mt-1 text-sm text-red-600">{errors.displayName.message}</p>
                )}
              </div>

              {/* Client Type */}
              <div>
                <label htmlFor="type" className="block text-sm font-medium text-gray-700">
                  Client Type *
                </label>
                <select
                  id="type"
                  {...register('type', { required: 'Client type is required' })}
                  className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:ring-indigo-500 focus:border-indigo-500 sm:text-sm"
                >
                  <option value="confidential">Confidential (Server-side apps)</option>
                  <option value="public">Public (SPAs, mobile apps)</option>
                </select>
                <p className="mt-1 text-xs text-gray-500">
                  {selectedType === 'confidential'
                    ? 'Can securely store client secrets (e.g., backend web apps)'
                    : 'Cannot store secrets securely (e.g., JavaScript SPAs, mobile apps)'}
                </p>
              </div>

              {/* Grant Types */}
              <div>
                <label className="block text-sm font-medium text-gray-700 mb-2">
                  Grant Types *
                </label>
                <div className="space-y-2">
                  {AVAILABLE_GRANT_TYPES.map((grantType) => (
                    <div key={grantType.value} className="flex items-start">
                      <div className="flex items-center h-5">
                        <input
                          type="checkbox"
                          checked={selectedGrantTypes?.includes(grantType.value)}
                          onChange={() => toggleGrantType(grantType.value)}
                          className="h-4 w-4 text-indigo-600 border-gray-300 rounded focus:ring-indigo-500"
                        />
                      </div>
                      <div className="ml-3 text-sm">
                        <label className="font-medium text-gray-700">{grantType.label}</label>
                      </div>
                    </div>
                  ))}
                </div>
              </div>

              {/* Scopes */}
              <div>
                <label className="block text-sm font-medium text-gray-700 mb-2">Scopes *</label>
                <div className="space-y-2">
                  {AVAILABLE_SCOPES.map((scope) => (
                    <div key={scope.value} className="flex items-start">
                      <div className="flex items-center h-5">
                        <input
                          type="checkbox"
                          checked={selectedScopes?.includes(scope.value)}
                          onChange={() => toggleScope(scope.value)}
                          className="h-4 w-4 text-indigo-600 border-gray-300 rounded focus:ring-indigo-500"
                        />
                      </div>
                      <div className="ml-3 text-sm">
                        <label className="font-medium text-gray-700">{scope.label}</label>
                      </div>
                    </div>
                  ))}
                </div>
              </div>

              {/* Redirect URIs */}
              <div>
                <label htmlFor="redirectUris" className="block text-sm font-medium text-gray-700">
                  Redirect URIs *
                </label>
                <textarea
                  id="redirectUris"
                  rows={4}
                  {...register('redirectUris', { required: 'At least one redirect URI is required' })}
                  className={`mt-1 block w-full rounded-md shadow-sm font-mono text-sm ${
                    errors.redirectUris
                      ? 'border-red-300 focus:ring-red-500 focus:border-red-500'
                      : 'border-gray-300 focus:ring-indigo-500 focus:border-indigo-500'
                  }`}
                  placeholder="https://myapp.com/callback&#10;https://localhost:3000/callback"
                />
                {errors.redirectUris && (
                  <p className="mt-1 text-sm text-red-600">{errors.redirectUris.message}</p>
                )}
                <p className="mt-1 text-xs text-gray-500">One URI per line</p>
              </div>

              {/* Post-Logout Redirect URIs */}
              <div>
                <label
                  htmlFor="postLogoutRedirectUris"
                  className="block text-sm font-medium text-gray-700"
                >
                  Post-Logout Redirect URIs
                </label>
                <textarea
                  id="postLogoutRedirectUris"
                  rows={3}
                  {...register('postLogoutRedirectUris')}
                  className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:ring-indigo-500 focus:border-indigo-500 font-mono text-sm"
                  placeholder="https://myapp.com/logged-out"
                />
                <p className="mt-1 text-xs text-gray-500">One URI per line (optional)</p>
              </div>

              {/* Info Box */}
              {!isEditMode && (
                <div className="bg-blue-50 border border-blue-200 rounded-md p-4">
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
                        {selectedType === 'confidential'
                          ? 'A client secret will be generated for you. Save it securely!'
                          : 'Public clients use PKCE for security instead of client secrets.'}
                      </p>
                    </div>
                  </div>
                </div>
              )}

              {/* Submit Button */}
              <div className="flex justify-end space-x-3">
                <button
                  type="button"
                  onClick={() => navigate('/oauth/clients')}
                  className="px-4 py-2 border border-gray-300 rounded-md shadow-sm text-sm font-medium text-gray-700 bg-white hover:bg-gray-50"
                >
                  Cancel
                </button>
                <button
                  type="submit"
                  disabled={saving}
                  className="inline-flex justify-center py-2 px-4 border border-transparent shadow-sm text-sm font-medium rounded-md text-white bg-indigo-600 hover:bg-indigo-700 disabled:opacity-50"
                >
                  {saving ? 'Saving...' : isEditMode ? 'Update Client' : 'Register Client'}
                </button>
              </div>
            </form>
          </div>
        </div>
      </div>
    </DashboardLayout>
  );
}
