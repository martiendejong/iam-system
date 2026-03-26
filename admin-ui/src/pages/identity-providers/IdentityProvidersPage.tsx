import { useState, useEffect } from 'react';
import { Link } from 'react-router-dom';
import DashboardLayout from '../../components/layout/DashboardLayout';
import { api } from '../../services/api';
import type { IdentityProvider } from '../../types';

export default function IdentityProvidersPage() {
  const [providers, setProviders] = useState<IdentityProvider[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [searchTerm, setSearchTerm] = useState('');
  const [filterType, setFilterType] = useState<string>('all');

  useEffect(() => {
    loadProviders();
  }, []);

  const loadProviders = async () => {
    try {
      setLoading(true);
      const data = await api.getIdentityProviders();
      setProviders(data);
    } catch (err: any) {
      setError(err.response?.data?.message || 'Failed to load identity providers');
    } finally {
      setLoading(false);
    }
  };

  const handleDelete = async (id: string, name: string) => {
    if (!confirm(`Are you sure you want to delete the identity provider "${name}"?`)) {
      return;
    }

    try {
      await api.deleteIdentityProvider(id);
      await loadProviders();
    } catch (err: any) {
      alert(err.response?.data?.message || 'Failed to delete identity provider');
    }
  };

  const filteredProviders = providers.filter((provider) => {
    const matchesSearch =
      provider.name.toLowerCase().includes(searchTerm.toLowerCase()) ||
      provider.displayName.toLowerCase().includes(searchTerm.toLowerCase());

    const matchesFilter =
      filterType === 'all' || provider.type === filterType;

    return matchesSearch && matchesFilter;
  });

  const providerTypeColors: Record<string, string> = {
    Google: 'bg-red-100 text-red-800',
    Microsoft: 'bg-blue-100 text-blue-800',
    GitHub: 'bg-gray-100 text-gray-800',
    Apple: 'bg-black text-white',
    SAML: 'bg-purple-100 text-purple-800',
    OIDC: 'bg-green-100 text-green-800',
  };

  return (
    <DashboardLayout>
      <div className="px-4 sm:px-6 lg:px-8">
        {/* Header */}
        <div className="sm:flex sm:items-center">
          <div className="sm:flex-auto">
            <h1 className="text-2xl font-semibold text-gray-900">Identity Providers</h1>
            <p className="mt-2 text-sm text-gray-700">
              Manage external identity providers for social and enterprise SSO login.
            </p>
          </div>
          <div className="mt-4 sm:mt-0 sm:ml-16 sm:flex-none">
            <Link
              to="/identity-providers/new"
              className="inline-flex items-center justify-center rounded-md border border-transparent bg-indigo-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-indigo-700 focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:ring-offset-2"
            >
              Add Provider
            </Link>
          </div>
        </div>

        {/* Filters */}
        <div className="mt-6 flex flex-col sm:flex-row gap-4">
          <div className="flex-1">
            <input
              type="text"
              placeholder="Search providers..."
              value={searchTerm}
              onChange={(e) => setSearchTerm(e.target.value)}
              className="block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
            />
          </div>
          <div className="flex gap-2 flex-wrap">
            {['all', 'Google', 'Microsoft', 'GitHub', 'Apple', 'SAML', 'OIDC'].map((type) => (
              <button
                key={type}
                onClick={() => setFilterType(type)}
                className={`px-4 py-2 text-sm font-medium rounded-md ${
                  filterType === type
                    ? 'bg-indigo-600 text-white'
                    : 'bg-white text-gray-700 border border-gray-300 hover:bg-gray-50'
                }`}
              >
                {type === 'all' ? 'All' : type}
              </button>
            ))}
          </div>
        </div>

        {/* Error Message */}
        {error && (
          <div className="mt-4 bg-red-50 border border-red-200 text-red-700 px-4 py-3 rounded">
            {error}
          </div>
        )}

        {/* Loading State */}
        {loading ? (
          <div className="mt-8 text-center">
            <div className="inline-block animate-spin rounded-full h-8 w-8 border-b-2 border-indigo-600"></div>
            <p className="mt-2 text-sm text-gray-500">Loading identity providers...</p>
          </div>
        ) : (
          /* Providers Grid */
          <div className="mt-8 grid grid-cols-1 gap-4 sm:grid-cols-2 lg:grid-cols-3">
            {filteredProviders.length === 0 ? (
              <div className="col-span-full text-center py-12 text-gray-500">
                No identity providers found
              </div>
            ) : (
              filteredProviders.map((provider) => (
                <div
                  key={provider.id}
                  className="relative bg-white rounded-lg border border-gray-200 p-6 shadow-sm hover:shadow-md transition-shadow"
                >
                  <div className="flex items-start justify-between">
                    <div className="flex-1">
                      <div className="flex items-center gap-2">
                        <h3 className="text-lg font-medium text-gray-900">{provider.displayName}</h3>
                        <span className={`inline-flex items-center px-2 py-0.5 rounded text-xs font-medium ${providerTypeColors[provider.type] || 'bg-gray-100 text-gray-800'}`}>
                          {provider.type}
                        </span>
                      </div>
                      <p className="mt-1 text-sm text-gray-500">{provider.name}</p>
                      {provider.tenantName && (
                        <p className="mt-1 text-xs text-gray-400">Tenant: {provider.tenantName}</p>
                      )}
                    </div>
                    <div className="ml-2">
                      <span
                        className={`inline-flex items-center px-2 py-0.5 rounded text-xs font-medium ${
                          provider.isActive
                            ? 'bg-green-100 text-green-800'
                            : 'bg-red-100 text-red-800'
                        }`}
                      >
                        {provider.isActive ? 'Active' : 'Inactive'}
                      </span>
                    </div>
                  </div>

                  <div className="mt-4 text-sm text-gray-500">
                    <p>Auto-create users: {provider.autoCreateUsers ? 'Yes' : 'No'}</p>
                    {provider.defaultRoleName && (
                      <p>Default role: {provider.defaultRoleName}</p>
                    )}
                  </div>

                  {/* Actions */}
                  <div className="mt-6 flex gap-3">
                    <Link
                      to={`/identity-providers/${provider.id}`}
                      className="flex-1 text-center px-3 py-2 border border-gray-300 rounded-md text-sm font-medium text-gray-700 bg-white hover:bg-gray-50"
                    >
                      Edit
                    </Link>
                    <button
                      onClick={() => handleDelete(provider.id, provider.displayName)}
                      className="px-3 py-2 border border-red-300 rounded-md text-sm font-medium text-red-700 bg-white hover:bg-red-50"
                    >
                      Delete
                    </button>
                  </div>
                </div>
              ))
            )}
          </div>
        )}

        {/* Results Count */}
        {!loading && (
          <div className="mt-6 text-sm text-gray-700">
            Showing {filteredProviders.length} of {providers.length} identity providers
          </div>
        )}
      </div>
    </DashboardLayout>
  );
}
