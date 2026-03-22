import { useState, useEffect } from 'react';
import { Link } from 'react-router-dom';
import DashboardLayout from '../../components/layout/DashboardLayout';
import { api } from '../../services/api';
import type { Tenant, TenantTypeValue } from '../../types';

// Recursive component for rendering tenant tree
interface TenantNodeProps {
  tenant: Tenant;
  allTenants: Tenant[];
  level: number;
  onDelete: (id: string, name: string) => void;
}

function TenantNode({ tenant, allTenants, level, onDelete }: TenantNodeProps) {
  const [expanded, setExpanded] = useState(level < 2); // Auto-expand first 2 levels
  const children = allTenants.filter((t) => t.parentId === tenant.id);
  const hasChildren = children.length > 0;

  // Color coding by tenant type
  const getTypeColor = (type: TenantTypeValue) => {
    switch (type) {
      case 'Organization':
        return 'bg-purple-100 text-purple-800';
      case 'Building':
        return 'bg-blue-100 text-blue-800';
      case 'Floor':
        return 'bg-green-100 text-green-800';
      case 'Room':
        return 'bg-yellow-100 text-yellow-800';
      case 'Device':
        return 'bg-gray-100 text-gray-800';
      default:
        return 'bg-gray-100 text-gray-800';
    }
  };

  // Icon by tenant type
  const getTypeIcon = (type: TenantTypeValue) => {
    switch (type) {
      case 'Organization':
        return (
          <svg className="h-5 w-5" fill="currentColor" viewBox="0 0 20 20">
            <path d="M10.707 2.293a1 1 0 00-1.414 0l-7 7a1 1 0 001.414 1.414L4 10.414V17a1 1 0 001 1h2a1 1 0 001-1v-2a1 1 0 011-1h2a1 1 0 011 1v2a1 1 0 001 1h2a1 1 0 001-1v-6.586l.293.293a1 1 0 001.414-1.414l-7-7z" />
          </svg>
        );
      case 'Building':
        return (
          <svg className="h-5 w-5" fill="currentColor" viewBox="0 0 20 20">
            <path
              fillRule="evenodd"
              d="M4 4a2 2 0 012-2h8a2 2 0 012 2v12a1 1 0 110 2h-3a1 1 0 01-1-1v-2a1 1 0 00-1-1H9a1 1 0 00-1 1v2a1 1 0 01-1 1H4a1 1 0 110-2V4zm3 1h2v2H7V5zm2 4H7v2h2V9zm2-4h2v2h-2V5zm2 4h-2v2h2V9z"
              clipRule="evenodd"
            />
          </svg>
        );
      case 'Floor':
        return (
          <svg className="h-5 w-5" fill="currentColor" viewBox="0 0 20 20">
            <path d="M3 4a1 1 0 011-1h12a1 1 0 011 1v2a1 1 0 01-1 1H4a1 1 0 01-1-1V4zM3 10a1 1 0 011-1h6a1 1 0 011 1v6a1 1 0 01-1 1H4a1 1 0 01-1-1v-6zM14 9a1 1 0 00-1 1v6a1 1 0 001 1h2a1 1 0 001-1v-6a1 1 0 00-1-1h-2z" />
          </svg>
        );
      case 'Room':
        return (
          <svg className="h-5 w-5" fill="currentColor" viewBox="0 0 20 20">
            <path d="M3 4a1 1 0 011-1h12a1 1 0 011 1v12a1 1 0 01-1 1H4a1 1 0 01-1-1V4z" />
          </svg>
        );
      case 'Device':
        return (
          <svg className="h-5 w-5" fill="currentColor" viewBox="0 0 20 20">
            <path
              fillRule="evenodd"
              d="M3 5a2 2 0 012-2h10a2 2 0 012 2v8a2 2 0 01-2 2h-2.22l.123.489.804.804A1 1 0 0113 18H7a1 1 0 01-.707-1.707l.804-.804L7.22 15H5a2 2 0 01-2-2V5zm5.771 7H5V5h10v7H8.771z"
              clipRule="evenodd"
            />
          </svg>
        );
      default:
        return null;
    }
  };

  return (
    <div className="select-none">
      <div
        className="flex items-center py-2 px-3 hover:bg-gray-50 rounded-md group"
        style={{ paddingLeft: `${level * 24 + 12}px` }}
      >
        {/* Expand/Collapse Button */}
        {hasChildren && (
          <button
            onClick={() => setExpanded(!expanded)}
            className="mr-2 text-gray-400 hover:text-gray-600"
          >
            <svg
              className={`h-4 w-4 transition-transform ${expanded ? 'rotate-90' : ''}`}
              fill="currentColor"
              viewBox="0 0 20 20"
            >
              <path
                fillRule="evenodd"
                d="M7.293 14.707a1 1 0 010-1.414L10.586 10 7.293 6.707a1 1 0 011.414-1.414l4 4a1 1 0 010 1.414l-4 4a1 1 0 01-1.414 0z"
                clipRule="evenodd"
              />
            </svg>
          </button>
        )}
        {!hasChildren && <div className="w-6" />}

        {/* Tenant Info */}
        <div className="flex items-center flex-1 min-w-0">
          <span className="text-gray-600 mr-2">{getTypeIcon(tenant.type)}</span>
          <span className="font-medium text-gray-900 truncate">{tenant.name}</span>
          <span
            className={`ml-2 inline-flex items-center px-2 py-0.5 rounded text-xs font-medium ${getTypeColor(
              tenant.type
            )}`}
          >
            {tenant.type}
          </span>
        </div>

        {/* Actions */}
        <div className="ml-4 opacity-0 group-hover:opacity-100 transition-opacity flex gap-2">
          <Link
            to={`/tenants/${tenant.id}`}
            className="text-sm text-indigo-600 hover:text-indigo-900"
          >
            Edit
          </Link>
          <button
            onClick={() => onDelete(tenant.id, tenant.name)}
            className="text-sm text-red-600 hover:text-red-900"
          >
            Delete
          </button>
        </div>
      </div>

      {/* Children */}
      {expanded && children.length > 0 && (
        <div>
          {children.map((child) => (
            <TenantNode
              key={child.id}
              tenant={child}
              allTenants={allTenants}
              level={level + 1}
              onDelete={onDelete}
            />
          ))}
        </div>
      )}
    </div>
  );
}

export default function TenantsPage() {
  const [tenants, setTenants] = useState<Tenant[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [searchTerm, setSearchTerm] = useState('');
  const [filterType, setFilterType] = useState<'all' | TenantTypeValue>('all');

  useEffect(() => {
    loadTenants();
  }, []);

  const loadTenants = async () => {
    try {
      setLoading(true);
      const data = await api.getTenants();
      setTenants(data);
    } catch (err: any) {
      setError(err.response?.data?.message || 'Failed to load tenants');
    } finally {
      setLoading(false);
    }
  };

  const handleDelete = async (tenantId: string, tenantName: string) => {
    if (!confirm(`Are you sure you want to delete "${tenantName}"? All child tenants will also be deleted.`)) {
      return;
    }

    try {
      await api.deleteTenant(tenantId);
      await loadTenants();
    } catch (err: any) {
      alert(err.response?.data?.message || 'Failed to delete tenant');
    }
  };

  // Filter tenants
  const filteredTenants = tenants.filter((tenant) => {
    const matchesSearch = tenant.name.toLowerCase().includes(searchTerm.toLowerCase());
    const matchesFilter = filterType === 'all' || tenant.type === filterType;
    return matchesSearch && matchesFilter;
  });

  // Get root tenants (no parent)
  const rootTenants = filteredTenants.filter((t) => !t.parentId);

  return (
    <DashboardLayout>
      <div className="px-4 sm:px-6 lg:px-8">
        {/* Header */}
        <div className="sm:flex sm:items-center">
          <div className="sm:flex-auto">
            <h1 className="text-2xl font-semibold text-gray-900">Tenants</h1>
            <p className="mt-2 text-sm text-gray-700">
              Hierarchical organization structure from buildings down to devices.
            </p>
          </div>
          <div className="mt-4 sm:mt-0 sm:ml-16 sm:flex-none">
            <Link
              to="/tenants/new"
              className="inline-flex items-center justify-center rounded-md border border-transparent bg-indigo-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-indigo-700 focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:ring-offset-2"
            >
              Add Tenant
            </Link>
          </div>
        </div>

        {/* Filters */}
        <div className="mt-6 flex flex-col sm:flex-row gap-4">
          <div className="flex-1">
            <input
              type="text"
              placeholder="Search tenants..."
              value={searchTerm}
              onChange={(e) => setSearchTerm(e.target.value)}
              className="block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
            />
          </div>
          <div className="flex gap-2 flex-wrap">
            <button
              onClick={() => setFilterType('all')}
              className={`px-3 py-2 text-sm font-medium rounded-md ${
                filterType === 'all'
                  ? 'bg-indigo-600 text-white'
                  : 'bg-white text-gray-700 border border-gray-300 hover:bg-gray-50'
              }`}
            >
              All
            </button>
            <button
              onClick={() => setFilterType('Organization')}
              className={`px-3 py-2 text-sm font-medium rounded-md ${
                filterType === 'Organization'
                  ? 'bg-indigo-600 text-white'
                  : 'bg-white text-gray-700 border border-gray-300 hover:bg-gray-50'
              }`}
            >
              Organization
            </button>
            <button
              onClick={() => setFilterType('Building')}
              className={`px-3 py-2 text-sm font-medium rounded-md ${
                filterType === 'Building'
                  ? 'bg-indigo-600 text-white'
                  : 'bg-white text-gray-700 border border-gray-300 hover:bg-gray-50'
              }`}
            >
              Building
            </button>
            <button
              onClick={() => setFilterType('Floor')}
              className={`px-3 py-2 text-sm font-medium rounded-md ${
                filterType === 'Floor'
                  ? 'bg-indigo-600 text-white'
                  : 'bg-white text-gray-700 border border-gray-300 hover:bg-gray-50'
              }`}
            >
              Floor
            </button>
            <button
              onClick={() => setFilterType('Room')}
              className={`px-3 py-2 text-sm font-medium rounded-md ${
                filterType === 'Room'
                  ? 'bg-indigo-600 text-white'
                  : 'bg-white text-gray-700 border border-gray-300 hover:bg-gray-50'
              }`}
            >
              Room
            </button>
            <button
              onClick={() => setFilterType('Device')}
              className={`px-3 py-2 text-sm font-medium rounded-md ${
                filterType === 'Device'
                  ? 'bg-indigo-600 text-white'
                  : 'bg-white text-gray-700 border border-gray-300 hover:bg-gray-50'
              }`}
            >
              Device
            </button>
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
            <p className="mt-2 text-sm text-gray-500">Loading tenants...</p>
          </div>
        ) : (
          /* Tenant Tree */
          <div className="mt-8 bg-white shadow rounded-lg overflow-hidden">
            <div className="p-4">
              {rootTenants.length === 0 ? (
                <div className="text-center py-12 text-gray-500">
                  No tenants found. Create your first tenant to get started.
                </div>
              ) : (
                rootTenants.map((tenant) => (
                  <TenantNode
                    key={tenant.id}
                    tenant={tenant}
                    allTenants={filteredTenants}
                    level={0}
                    onDelete={handleDelete}
                  />
                ))
              )}
            </div>
          </div>
        )}

        {/* Legend */}
        {!loading && tenants.length > 0 && (
          <div className="mt-6 bg-gray-50 rounded-lg p-4">
            <h3 className="text-sm font-medium text-gray-900 mb-2">Tenant Types:</h3>
            <div className="flex flex-wrap gap-3 text-sm">
              <div className="flex items-center">
                <span className="inline-flex items-center px-2 py-0.5 rounded bg-purple-100 text-purple-800 font-medium">
                  Organization
                </span>
                <span className="ml-2 text-gray-600">Top level</span>
              </div>
              <div className="flex items-center">
                <span className="inline-flex items-center px-2 py-0.5 rounded bg-blue-100 text-blue-800 font-medium">
                  Building
                </span>
                <span className="ml-2 text-gray-600">Physical buildings</span>
              </div>
              <div className="flex items-center">
                <span className="inline-flex items-center px-2 py-0.5 rounded bg-green-100 text-green-800 font-medium">
                  Floor
                </span>
                <span className="ml-2 text-gray-600">Building floors</span>
              </div>
              <div className="flex items-center">
                <span className="inline-flex items-center px-2 py-0.5 rounded bg-yellow-100 text-yellow-800 font-medium">
                  Room
                </span>
                <span className="ml-2 text-gray-600">Individual rooms</span>
              </div>
              <div className="flex items-center">
                <span className="inline-flex items-center px-2 py-0.5 rounded bg-gray-100 text-gray-800 font-medium">
                  Device
                </span>
                <span className="ml-2 text-gray-600">IoT devices</span>
              </div>
            </div>
          </div>
        )}

        {/* Results Count */}
        {!loading && (
          <div className="mt-4 text-sm text-gray-700">
            Showing {filteredTenants.length} of {tenants.length} tenants
          </div>
        )}
      </div>
    </DashboardLayout>
  );
}
