import { useState, useEffect } from 'react';
import { Link } from 'react-router-dom';
import DashboardLayout from '../../components/layout/DashboardLayout';
import { api } from '../../services/api';
import type { Role } from '../../types';

const CATEGORY_ORDER = ['Real Estate', 'System'];

const CATEGORY_STYLE: Record<string, { header: string; badge: string; icon: string }> = {
  'Real Estate': {
    header: 'bg-emerald-50 border-emerald-200',
    badge: 'bg-emerald-100 text-emerald-700',
    icon: 'text-emerald-600',
  },
  System: {
    header: 'bg-slate-50 border-slate-200',
    badge: 'bg-slate-100 text-slate-600',
    icon: 'text-slate-500',
  },
};

const FALLBACK_STYLE = {
  header: 'bg-indigo-50 border-indigo-200',
  badge: 'bg-indigo-100 text-indigo-700',
  icon: 'text-indigo-600',
};

export default function RolesPage() {
  const [roles, setRoles] = useState<Role[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [searchTerm, setSearchTerm] = useState('');
  const [deletingId, setDeletingId] = useState<string | null>(null);

  useEffect(() => { loadRoles(); }, []);

  const loadRoles = async () => {
    try {
      setLoading(true);
      const data = await api.getRoles();
      setRoles(Array.isArray(data) ? data : []);
    } catch (err: any) {
      setError(err.response?.data?.message || 'Failed to load roles');
    } finally {
      setLoading(false);
    }
  };

  const handleDelete = async (roleId: string, roleName: string) => {
    if (!confirm(`Delete role "${roleName}"? This cannot be undone.`)) return;
    try {
      setDeletingId(roleId);
      await api.deleteRole(roleId);
      await loadRoles();
    } catch (err: any) {
      alert(err.response?.data?.message || 'Failed to delete role');
    } finally {
      setDeletingId(null);
    }
  };

  const filtered = roles.filter((r) => {
    const q = searchTerm.toLowerCase();
    return r.name.toLowerCase().includes(q) || (r.description ?? '').toLowerCase().includes(q);
  });

  // Group: known categories in order, then "Other" for uncategorised
  const grouped = new Map<string, Role[]>();
  for (const role of filtered) {
    const cat = role.category || 'Other';
    if (!grouped.has(cat)) grouped.set(cat, []);
    grouped.get(cat)!.push(role);
  }

  const orderedKeys = [
    ...CATEGORY_ORDER.filter(c => grouped.has(c)),
    ...[...grouped.keys()].filter(c => !CATEGORY_ORDER.includes(c)),
  ];

  return (
    <DashboardLayout>
      <div>
        {/* Header */}
        <div className="flex items-center justify-between mb-6">
          <div>
            <h1 className="text-2xl font-bold text-gray-900">Roles</h1>
            <p className="text-sm text-gray-500 mt-0.5">
              {roles.length} {roles.length === 1 ? 'role' : 'roles'} in {orderedKeys.length} {orderedKeys.length === 1 ? 'category' : 'categories'}
            </p>
          </div>
          <Link
            to="/roles/new"
            className="inline-flex items-center gap-2 px-4 py-2 bg-indigo-600 text-white text-sm font-medium rounded-lg hover:bg-indigo-700 transition-colors shadow-sm"
          >
            <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 4v16m8-8H4" />
            </svg>
            New Role
          </Link>
        </div>

        {/* Search */}
        <div className="relative mb-6">
          <svg className="absolute left-3 top-1/2 -translate-y-1/2 w-4 h-4 text-gray-400" fill="none" viewBox="0 0 24 24" stroke="currentColor">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M21 21l-6-6m2-5a7 7 0 11-14 0 7 7 0 0114 0z" />
          </svg>
          <input
            type="text"
            placeholder="Search roles..."
            value={searchTerm}
            onChange={(e) => setSearchTerm(e.target.value)}
            className="block w-full pl-9 pr-4 py-2 border border-gray-300 rounded-lg text-sm focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-transparent"
          />
        </div>

        {error && (
          <div className="mb-4 p-3 bg-red-50 border border-red-200 text-red-700 rounded-lg text-sm">{error}</div>
        )}

        {loading ? (
          <div className="flex flex-col items-center justify-center py-16 text-gray-400">
            <div className="w-8 h-8 border-2 border-indigo-500 border-t-transparent rounded-full animate-spin mb-3" />
            <span className="text-sm">Loading roles...</span>
          </div>
        ) : filtered.length === 0 ? (
          <div className="flex flex-col items-center justify-center py-16 text-gray-400">
            <p className="text-sm font-medium">No roles found</p>
          </div>
        ) : (
          <div className="space-y-8">
            {orderedKeys.map((cat) => {
              const style = CATEGORY_STYLE[cat] ?? FALLBACK_STYLE;
              const catRoles = grouped.get(cat)!;
              return (
                <section key={cat}>
                  {/* Category header */}
                  <div className={`flex items-center gap-3 px-4 py-2.5 rounded-lg border mb-4 ${style.header}`}>
                    <svg className={`w-4 h-4 ${style.icon}`} fill="none" viewBox="0 0 24 24" stroke="currentColor">
                      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M7 7h.01M7 3h5c.512 0 1.024.195 1.414.586l7 7a2 2 0 010 2.828l-7 7a2 2 0 01-2.828 0l-7-7A1.994 1.994 0 013 12V7a4 4 0 014-4z" />
                    </svg>
                    <span className="text-sm font-semibold text-gray-700">{cat}</span>
                    <span className={`ml-auto inline-flex items-center px-2 py-0.5 rounded-full text-xs font-medium ${style.badge}`}>
                      {catRoles.length} {catRoles.length === 1 ? 'role' : 'roles'}
                    </span>
                  </div>

                  {/* Role cards */}
                  <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 gap-4">
                    {catRoles.map((role) => (
                      <div
                        key={role.id}
                        className="bg-white rounded-xl border border-gray-200 shadow-sm p-5 flex flex-col gap-3 hover:shadow-md transition-shadow"
                      >
                        <div className="flex items-start justify-between gap-2">
                          <div className={`w-10 h-10 rounded-lg flex items-center justify-center text-sm font-bold flex-shrink-0 ${style.badge}`}>
                            {role.name[0].toUpperCase()}
                          </div>
                          {role.isSystem && (
                            <span className="inline-flex items-center px-2 py-0.5 rounded-full text-xs font-medium bg-slate-100 text-slate-600">
                              System
                            </span>
                          )}
                        </div>

                        <div className="flex-1">
                          <h3 className="text-sm font-semibold text-gray-900">{role.name}</h3>
                          {role.description ? (
                            <p className="text-xs text-gray-500 mt-1 line-clamp-2">{role.description}</p>
                          ) : (
                            <p className="text-xs text-gray-400 mt-1 italic">No description</p>
                          )}
                        </div>

                        <div className="flex gap-2 pt-1 border-t border-gray-100">
                          <Link
                            to={`/roles/${role.id}`}
                            className="flex-1 text-center px-3 py-1.5 text-xs font-medium text-gray-700 bg-gray-50 border border-gray-200 rounded-lg hover:bg-gray-100 transition-colors"
                          >
                            Edit
                          </Link>
                          {!role.isSystem && (
                            <button
                              onClick={() => handleDelete(role.id, role.name)}
                              disabled={deletingId === role.id}
                              className="px-3 py-1.5 text-xs font-medium text-red-600 bg-red-50 border border-red-200 rounded-lg hover:bg-red-100 transition-colors disabled:opacity-50"
                            >
                              {deletingId === role.id ? '...' : 'Delete'}
                            </button>
                          )}
                        </div>
                      </div>
                    ))}
                  </div>
                </section>
              );
            })}
          </div>
        )}
      </div>
    </DashboardLayout>
  );
}
