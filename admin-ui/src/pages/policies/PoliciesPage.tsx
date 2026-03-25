import { useState, useEffect } from 'react';
import { Link } from 'react-router-dom';
import DashboardLayout from '../../components/layout/DashboardLayout';
import { policyApi } from '../../services/policyApi';
import { api } from '../../services/api';
import type { Policy } from '../../types/policies';

export default function PoliciesPage() {
  const [policies, setPolicies] = useState<Policy[]>([]);
  const [tenants, setTenants] = useState<any[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [searchTerm, setSearchTerm] = useState('');
  const [filterEffect, setFilterEffect] = useState<'all' | 'Allow' | 'Deny'>('all');
  const [filterActive, setFilterActive] = useState<'all' | 'active' | 'inactive'>('all');
  const [filterTenantId, setFilterTenantId] = useState<string>('');
  const [deletingId, setDeletingId] = useState<string | null>(null);

  useEffect(() => {
    loadData();
  }, []);

  const loadData = async () => {
    try {
      setLoading(true);
      const [policiesData, tenantsData] = await Promise.all([
        policyApi.getPolicies(),
        api.getTenants(),
      ]);
      setPolicies(policiesData);
      setTenants(tenantsData);
    } catch (err: any) {
      setError(err.response?.data?.message || 'Failed to load policies');
    } finally {
      setLoading(false);
    }
  };

  const handleDelete = async (id: string) => {
    if (!window.confirm('Are you sure you want to delete this policy? This action cannot be undone.')) {
      return;
    }
    try {
      setDeletingId(id);
      await policyApi.deletePolicy(id);
      setPolicies((prev) => prev.filter((p) => p.id !== id));
    } catch (err: any) {
      alert(err.response?.data?.message || 'Failed to delete policy');
    } finally {
      setDeletingId(null);
    }
  };

  const getTenantName = (tenantId: string) => {
    const tenant = tenants.find((t) => t.id === tenantId);
    return tenant ? `${tenant.name} (${tenant.type})` : tenantId;
  };

  const filteredPolicies = policies.filter((policy) => {
    const matchesSearch =
      policy.name.toLowerCase().includes(searchTerm.toLowerCase()) ||
      policy.resource.toLowerCase().includes(searchTerm.toLowerCase()) ||
      policy.action.toLowerCase().includes(searchTerm.toLowerCase());

    const matchesEffect =
      filterEffect === 'all' || policy.effect === filterEffect;

    const matchesActive =
      filterActive === 'all' ||
      (filterActive === 'active' && policy.isActive) ||
      (filterActive === 'inactive' && !policy.isActive);

    const matchesTenant =
      !filterTenantId || policy.tenantId === filterTenantId;

    return matchesSearch && matchesEffect && matchesActive && matchesTenant;
  });

  const inheritanceScopeLabel = (scope: string) => {
    switch (scope) {
      case 'Self':
        return 'Self Only';
      case 'Children':
        return 'Direct Children';
      case 'Descendants':
        return 'All Descendants';
      default:
        return scope;
    }
  };

  const inheritanceScopeBadgeColor = (scope: string) => {
    switch (scope) {
      case 'Self':
        return 'bg-gray-100 text-gray-700';
      case 'Children':
        return 'bg-blue-100 text-blue-700';
      case 'Descendants':
        return 'bg-purple-100 text-purple-700';
      default:
        return 'bg-gray-100 text-gray-700';
    }
  };

  return (
    <DashboardLayout>
      <div className="px-4 sm:px-6 lg:px-8">
        {/* Header */}
        <div className="sm:flex sm:items-center">
          <div className="sm:flex-auto">
            <h1 className="text-2xl font-semibold text-gray-900">Policies</h1>
            <p className="mt-2 text-sm text-gray-700">
              Manage access control policies with inheritance across the tenant hierarchy.
            </p>
          </div>
          <div className="mt-4 sm:mt-0 sm:ml-16 sm:flex-none flex gap-2">
            <Link
              to="/policies/test"
              className="inline-flex items-center justify-center rounded-md border border-gray-300 bg-white px-4 py-2 text-sm font-medium text-gray-700 shadow-sm hover:bg-gray-50 focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:ring-offset-2"
            >
              <svg className="h-4 w-4 mr-2" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 12l2 2 4-4m6 2a9 9 0 11-18 0 9 9 0 0118 0z" />
              </svg>
              Test Policies
            </Link>
            <Link
              to="/policies/inheritance"
              className="inline-flex items-center justify-center rounded-md border border-gray-300 bg-white px-4 py-2 text-sm font-medium text-gray-700 shadow-sm hover:bg-gray-50 focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:ring-offset-2"
            >
              <svg className="h-4 w-4 mr-2" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M4 6h16M4 10h16M4 14h16M4 18h16" />
              </svg>
              Inheritance View
            </Link>
            <Link
              to="/policies/new"
              className="inline-flex items-center justify-center rounded-md border border-transparent bg-indigo-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-indigo-700 focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:ring-offset-2"
            >
              Create Policy
            </Link>
          </div>
        </div>

        {/* Filters */}
        <div className="mt-6 grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-4 gap-4">
          {/* Search */}
          <div>
            <input
              type="text"
              placeholder="Search by name, resource, or action..."
              value={searchTerm}
              onChange={(e) => setSearchTerm(e.target.value)}
              className="block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
            />
          </div>

          {/* Tenant Filter */}
          <div>
            <select
              value={filterTenantId}
              onChange={(e) => setFilterTenantId(e.target.value)}
              className="block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
            >
              <option value="">All Tenants</option>
              {tenants.map((tenant) => (
                <option key={tenant.id} value={tenant.id}>
                  {tenant.name} ({tenant.type})
                </option>
              ))}
            </select>
          </div>

          {/* Effect Filter */}
          <div className="flex gap-2">
            <button
              onClick={() => setFilterEffect('all')}
              className={`flex-1 px-3 py-2 text-sm font-medium rounded-md ${
                filterEffect === 'all'
                  ? 'bg-indigo-600 text-white'
                  : 'bg-white text-gray-700 border border-gray-300 hover:bg-gray-50'
              }`}
            >
              All
            </button>
            <button
              onClick={() => setFilterEffect('Allow')}
              className={`flex-1 px-3 py-2 text-sm font-medium rounded-md ${
                filterEffect === 'Allow'
                  ? 'bg-green-600 text-white'
                  : 'bg-white text-gray-700 border border-gray-300 hover:bg-gray-50'
              }`}
            >
              Allow
            </button>
            <button
              onClick={() => setFilterEffect('Deny')}
              className={`flex-1 px-3 py-2 text-sm font-medium rounded-md ${
                filterEffect === 'Deny'
                  ? 'bg-red-600 text-white'
                  : 'bg-white text-gray-700 border border-gray-300 hover:bg-gray-50'
              }`}
            >
              Deny
            </button>
          </div>

          {/* Active Filter */}
          <div className="flex gap-2">
            <button
              onClick={() => setFilterActive('all')}
              className={`flex-1 px-3 py-2 text-sm font-medium rounded-md ${
                filterActive === 'all'
                  ? 'bg-indigo-600 text-white'
                  : 'bg-white text-gray-700 border border-gray-300 hover:bg-gray-50'
              }`}
            >
              All
            </button>
            <button
              onClick={() => setFilterActive('active')}
              className={`flex-1 px-3 py-2 text-sm font-medium rounded-md ${
                filterActive === 'active'
                  ? 'bg-indigo-600 text-white'
                  : 'bg-white text-gray-700 border border-gray-300 hover:bg-gray-50'
              }`}
            >
              Active
            </button>
            <button
              onClick={() => setFilterActive('inactive')}
              className={`flex-1 px-3 py-2 text-sm font-medium rounded-md ${
                filterActive === 'inactive'
                  ? 'bg-indigo-600 text-white'
                  : 'bg-white text-gray-700 border border-gray-300 hover:bg-gray-50'
              }`}
            >
              Inactive
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
            <p className="mt-2 text-sm text-gray-500">Loading policies...</p>
          </div>
        ) : (
          /* Policies Table */
          <div className="mt-8 flex flex-col">
            <div className="-my-2 -mx-4 overflow-x-auto sm:-mx-6 lg:-mx-8">
              <div className="inline-block min-w-full py-2 align-middle md:px-6 lg:px-8">
                <div className="overflow-hidden shadow ring-1 ring-black ring-opacity-5 md:rounded-lg">
                  <table className="min-w-full divide-y divide-gray-300">
                    <thead className="bg-gray-50">
                      <tr>
                        <th className="px-3 py-3.5 text-left text-sm font-semibold text-gray-900">
                          Name
                        </th>
                        <th className="px-3 py-3.5 text-left text-sm font-semibold text-gray-900">
                          Effect
                        </th>
                        <th className="px-3 py-3.5 text-left text-sm font-semibold text-gray-900">
                          Resource / Action
                        </th>
                        <th className="px-3 py-3.5 text-left text-sm font-semibold text-gray-900">
                          Tenant
                        </th>
                        <th className="px-3 py-3.5 text-left text-sm font-semibold text-gray-900">
                          Inheritance
                        </th>
                        <th className="px-3 py-3.5 text-left text-sm font-semibold text-gray-900">
                          Priority
                        </th>
                        <th className="px-3 py-3.5 text-left text-sm font-semibold text-gray-900">
                          Status
                        </th>
                        <th className="relative py-3.5 pl-3 pr-4 sm:pr-6">
                          <span className="sr-only">Actions</span>
                        </th>
                      </tr>
                    </thead>
                    <tbody className="divide-y divide-gray-200 bg-white">
                      {filteredPolicies.length === 0 ? (
                        <tr>
                          <td colSpan={8} className="px-3 py-8 text-sm text-gray-500 text-center">
                            {policies.length === 0
                              ? 'No policies found. Create your first policy to get started.'
                              : 'No policies match your current filters.'}
                          </td>
                        </tr>
                      ) : (
                        filteredPolicies.map((policy) => (
                          <tr key={policy.id} className={!policy.isActive ? 'bg-gray-50 opacity-60' : ''}>
                            <td className="whitespace-nowrap px-3 py-4 text-sm">
                              <div className="font-medium text-gray-900">{policy.name}</div>
                              {policy.description && (
                                <div className="text-gray-500 truncate max-w-xs">{policy.description}</div>
                              )}
                            </td>
                            <td className="whitespace-nowrap px-3 py-4 text-sm">
                              <span
                                className={`inline-flex rounded-full px-2.5 py-0.5 text-xs font-semibold ${
                                  policy.effect === 'Allow'
                                    ? 'bg-green-100 text-green-800'
                                    : 'bg-red-100 text-red-800'
                                }`}
                              >
                                {policy.effect}
                              </span>
                            </td>
                            <td className="px-3 py-4 text-sm">
                              <div className="text-gray-900 font-mono text-xs">{policy.resource}</div>
                              <div className="text-gray-500 text-xs mt-0.5">{policy.action}</div>
                            </td>
                            <td className="whitespace-nowrap px-3 py-4 text-sm text-gray-500">
                              {getTenantName(policy.tenantId)}
                            </td>
                            <td className="whitespace-nowrap px-3 py-4 text-sm">
                              <span
                                className={`inline-flex rounded-full px-2.5 py-0.5 text-xs font-medium ${inheritanceScopeBadgeColor(
                                  policy.inheritanceScope
                                )}`}
                              >
                                {inheritanceScopeLabel(policy.inheritanceScope)}
                              </span>
                            </td>
                            <td className="whitespace-nowrap px-3 py-4 text-sm text-gray-500">
                              <span className="inline-flex items-center px-2 py-0.5 rounded text-xs font-medium bg-gray-100 text-gray-800">
                                {policy.priority}
                              </span>
                            </td>
                            <td className="whitespace-nowrap px-3 py-4 text-sm">
                              <span
                                className={`inline-flex rounded-full px-2 text-xs font-semibold leading-5 ${
                                  policy.isActive
                                    ? 'bg-green-100 text-green-800'
                                    : 'bg-red-100 text-red-800'
                                }`}
                              >
                                {policy.isActive ? 'Active' : 'Inactive'}
                              </span>
                            </td>
                            <td className="relative whitespace-nowrap py-4 pl-3 pr-4 text-right text-sm font-medium sm:pr-6">
                              <Link
                                to={`/policies/${policy.id}`}
                                className="text-indigo-600 hover:text-indigo-900 mr-3"
                              >
                                Edit
                              </Link>
                              <button
                                onClick={() => handleDelete(policy.id)}
                                disabled={deletingId === policy.id}
                                className="text-red-600 hover:text-red-900 disabled:opacity-50"
                              >
                                {deletingId === policy.id ? 'Deleting...' : 'Delete'}
                              </button>
                            </td>
                          </tr>
                        ))
                      )}
                    </tbody>
                  </table>
                </div>
              </div>
            </div>
          </div>
        )}

        {/* Results Count */}
        {!loading && (
          <div className="mt-4 text-sm text-gray-700">
            Showing {filteredPolicies.length} of {policies.length} policies
          </div>
        )}
      </div>
    </DashboardLayout>
  );
}
