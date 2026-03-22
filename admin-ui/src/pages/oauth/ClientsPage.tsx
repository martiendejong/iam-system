import { useState, useEffect } from 'react';
import { Link } from 'react-router-dom';
import DashboardLayout from '../../components/layout/DashboardLayout';
import { api } from '../../services/api';

interface OAuth2Client {
  id: string;
  clientId: string;
  displayName: string;
  type: string;
  redirectUris: string[];
  postLogoutRedirectUris: string[];
  permissions: string[];
}

export default function ClientsPage() {
  const [clients, setClients] = useState<OAuth2Client[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [searchTerm, setSearchTerm] = useState('');

  useEffect(() => {
    loadClients();
  }, []);

  const loadClients = async () => {
    try {
      setLoading(true);
      const data = await api.getOAuth2Clients();
      setClients(data);
    } catch (err: any) {
      setError(err.response?.data?.message || 'Failed to load OAuth2 clients');
    } finally {
      setLoading(false);
    }
  };

  const handleDelete = async (clientId: string, displayName: string) => {
    if (!confirm(`Are you sure you want to delete client "${displayName}"?`)) {
      return;
    }

    try {
      await api.deleteOAuth2Client(clientId);
      await loadClients();
    } catch (err: any) {
      alert(err.response?.data?.message || 'Failed to delete client');
    }
  };

  const filteredClients = clients.filter((client) =>
    client.displayName.toLowerCase().includes(searchTerm.toLowerCase()) ||
    client.clientId.toLowerCase().includes(searchTerm.toLowerCase())
  );

  const getClientTypeColor = (type: string) => {
    switch (type) {
      case 'confidential':
        return 'bg-blue-100 text-blue-800';
      case 'public':
        return 'bg-green-100 text-green-800';
      default:
        return 'bg-gray-100 text-gray-800';
    }
  };

  return (
    <DashboardLayout>
      <div className="px-4 sm:px-6 lg:px-8">
        {/* Header */}
        <div className="sm:flex sm:items-center">
          <div className="sm:flex-auto">
            <h1 className="text-2xl font-semibold text-gray-900">OAuth2 Clients</h1>
            <p className="mt-2 text-sm text-gray-700">
              Manage OAuth2 applications that can authenticate with this IAM system.
            </p>
          </div>
          <div className="mt-4 sm:mt-0 sm:ml-16 sm:flex-none">
            <Link
              to="/oauth/clients/new"
              className="inline-flex items-center justify-center rounded-md border border-transparent bg-indigo-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-indigo-700 focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:ring-offset-2"
            >
              Register Client
            </Link>
          </div>
        </div>

        {/* Search */}
        <div className="mt-6">
          <input
            type="text"
            placeholder="Search clients..."
            value={searchTerm}
            onChange={(e) => setSearchTerm(e.target.value)}
            className="block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
          />
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
            <p className="mt-2 text-sm text-gray-500">Loading OAuth2 clients...</p>
          </div>
        ) : (
          /* Client List */
          <div className="mt-8 grid grid-cols-1 gap-6 sm:grid-cols-2 lg:grid-cols-3">
            {filteredClients.length === 0 ? (
              <div className="col-span-full text-center py-12 text-gray-500">
                No OAuth2 clients found. Register your first client to get started.
              </div>
            ) : (
              filteredClients.map((client) => (
                <div
                  key={client.id}
                  className="bg-white overflow-hidden shadow rounded-lg divide-y divide-gray-200"
                >
                  <div className="p-6">
                    {/* Client Header */}
                    <div className="flex items-center justify-between">
                      <div className="flex-1 min-w-0">
                        <h3 className="text-lg font-medium text-gray-900 truncate">
                          {client.displayName}
                        </h3>
                        <p className="mt-1 text-sm text-gray-500 truncate font-mono">
                          {client.clientId}
                        </p>
                      </div>
                      <span
                        className={`ml-2 inline-flex items-center px-2.5 py-0.5 rounded-full text-xs font-medium ${getClientTypeColor(
                          client.type
                        )}`}
                      >
                        {client.type}
                      </span>
                    </div>

                    {/* Redirect URIs */}
                    <div className="mt-4">
                      <h4 className="text-xs font-medium text-gray-500 uppercase">
                        Redirect URIs
                      </h4>
                      <ul className="mt-2 space-y-1">
                        {client.redirectUris.length === 0 ? (
                          <li className="text-sm text-gray-400">None configured</li>
                        ) : (
                          client.redirectUris.slice(0, 2).map((uri, idx) => (
                            <li key={idx} className="text-sm text-gray-700 truncate">
                              {uri}
                            </li>
                          ))
                        )}
                        {client.redirectUris.length > 2 && (
                          <li className="text-xs text-gray-500">
                            +{client.redirectUris.length - 2} more
                          </li>
                        )}
                      </ul>
                    </div>

                    {/* Scopes */}
                    <div className="mt-4">
                      <h4 className="text-xs font-medium text-gray-500 uppercase">Scopes</h4>
                      <div className="mt-2 flex flex-wrap gap-1">
                        {client.permissions.length === 0 ? (
                          <span className="text-sm text-gray-400">None</span>
                        ) : (
                          client.permissions.slice(0, 3).map((perm, idx) => (
                            <span
                              key={idx}
                              className="inline-flex items-center px-2 py-0.5 rounded text-xs font-medium bg-gray-100 text-gray-800"
                            >
                              {perm.replace('scp:', '')}
                            </span>
                          ))
                        )}
                        {client.permissions.length > 3 && (
                          <span className="text-xs text-gray-500">
                            +{client.permissions.length - 3}
                          </span>
                        )}
                      </div>
                    </div>
                  </div>

                  {/* Actions */}
                  <div className="px-6 py-3 bg-gray-50 flex justify-between">
                    <Link
                      to={`/oauth/clients/${client.id}`}
                      className="text-sm font-medium text-indigo-600 hover:text-indigo-900"
                    >
                      Edit
                    </Link>
                    <button
                      onClick={() => handleDelete(client.id, client.displayName)}
                      className="text-sm font-medium text-red-600 hover:text-red-900"
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
          <div className="mt-4 text-sm text-gray-700">
            Showing {filteredClients.length} of {clients.length} clients
          </div>
        )}
      </div>
    </DashboardLayout>
  );
}
