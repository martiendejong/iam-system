import { useCallback, useEffect, useRef, useState } from 'react';
import DashboardLayout from '../../components/layout/DashboardLayout';
import {
  accessMatrixApi,
  type MatrixApplication,
  type MatrixUser,
} from '../../services/accessMatrixApi';

/**
 * Access matrix (SuperAdmin-only): rows = users, columns = registered
 * applications. The cell checkbox toggles the app's base access role; the "..."
 * button opens the app's declared fine-grained permissions.
 */
export default function AccessMatrixPage() {
  const [applications, setApplications] = useState<MatrixApplication[]>([]);
  const [users, setUsers] = useState<MatrixUser[]>([]);
  const [total, setTotal] = useState(0);
  const [skip, setSkip] = useState(0);
  const [loading, setLoading] = useState(true);
  const [forbidden, setForbidden] = useState(false);
  const [error, setError] = useState('');
  const [searchTerm, setSearchTerm] = useState('');
  const [pendingCell, setPendingCell] = useState<string | null>(null); // `${userId}|${role}`
  const [openMenu, setOpenMenu] = useState<string | null>(null); // `${userId}|${clientId}`
  const [notice, setNotice] = useState('');
  const searchDebounce = useRef<ReturnType<typeof setTimeout> | null>(null);

  const TAKE = 50;

  const loadMatrix = useCallback(async (search: string, skipValue: number) => {
    try {
      setLoading(true);
      setError('');
      const data = await accessMatrixApi.getMatrix(search, skipValue, TAKE);
      setApplications(data.applications);
      setUsers(data.users);
      setTotal(data.total);
      setForbidden(false);
    } catch (err: any) {
      if (err.response?.status === 403) {
        setForbidden(true);
      } else {
        setError(err.response?.data?.error || 'Failed to load the access matrix');
      }
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    loadMatrix('', 0);
  }, [loadMatrix]);

  const handleSearchChange = (value: string) => {
    setSearchTerm(value);
    setSkip(0);
    if (searchDebounce.current) clearTimeout(searchDebounce.current);
    searchDebounce.current = setTimeout(() => loadMatrix(value, 0), 300);
  };

  const changePage = (newSkip: number) => {
    setSkip(newSkip);
    loadMatrix(searchTerm, newSkip);
  };

  const flashNotice = (message: string) => {
    setNotice(message);
    setTimeout(() => setNotice(''), 4000);
  };

  const toggleRole = async (user: MatrixUser, role: string, currentlyGranted: boolean) => {
    const cellKey = `${user.id}|${role}`;
    try {
      setPendingCell(cellKey);
      if (currentlyGranted) {
        await accessMatrixApi.revoke(user.id, role);
      } else {
        await accessMatrixApi.grant(user.id, role);
      }
      // Optimistically update local state; state is re-read live on next load
      setUsers((prev) =>
        prev.map((u) =>
          u.id === user.id
            ? {
                ...u,
                roles: currentlyGranted
                  ? u.roles.filter((r) => r !== role)
                  : [...u.roles, role],
              }
            : u
        )
      );
      flashNotice(
        `${currentlyGranted ? 'Revoked' : 'Granted'} ${role} for ${user.email}. Effective on next token refresh.`
      );
    } catch (err: any) {
      alert(err.response?.data?.error || 'Change failed');
    } finally {
      setPendingCell(null);
    }
  };

  if (forbidden) {
    return (
      <DashboardLayout>
        <div className="max-w-lg mx-auto mt-16 bg-white rounded-xl shadow p-8 text-center">
          <div className="text-5xl mb-4">403</div>
          <h2 className="text-xl font-semibold text-gray-900 mb-2">Access denied</h2>
          <p className="text-gray-600">
            The access matrix is only available to SuperAdmins.
          </p>
        </div>
      </DashboardLayout>
    );
  }

  return (
    <DashboardLayout>
      <div className="space-y-4">
        <div className="flex items-center justify-between gap-4 flex-wrap">
          <div>
            <h1 className="text-2xl font-bold text-gray-900">Access Matrix</h1>
            <p className="text-sm text-gray-500 mt-1">
              Toggle per-user application access. Use the &quot;&hellip;&quot; menu for
              app-specific permissions. Changes take effect on the user&apos;s next token refresh.
            </p>
          </div>
          <input
            type="text"
            placeholder="Search users by email or name..."
            value={searchTerm}
            onChange={(e) => handleSearchChange(e.target.value)}
            className="w-72 px-3 py-2 border border-gray-300 rounded-lg text-sm focus:outline-none focus:ring-2 focus:ring-indigo-500"
          />
        </div>

        {notice && (
          <div className="bg-green-50 border border-green-200 text-green-800 text-sm rounded-lg px-4 py-2">
            {notice}
          </div>
        )}
        {error && (
          <div className="bg-red-50 border border-red-200 text-red-800 text-sm rounded-lg px-4 py-2">
            {error}
          </div>
        )}

        {loading ? (
          <div className="text-gray-500 py-12 text-center">Loading access matrix...</div>
        ) : (
          <div className="bg-white rounded-xl shadow overflow-x-auto">
            <table className="min-w-full text-sm">
              <thead>
                <tr className="border-b border-gray-200 bg-gray-50">
                  <th className="sticky left-0 bg-gray-50 z-10 text-left px-4 py-3 font-semibold text-gray-700 min-w-[220px]">
                    User
                  </th>
                  {applications.map((app) => (
                    <th
                      key={app.clientId}
                      className="px-3 py-3 font-semibold text-gray-700 text-center whitespace-nowrap"
                      title={`Base role: ${app.baseRole}${app.source === 'registered' ? ' (auto from OAuth client)' : ''}`}
                    >
                      {app.displayName}
                    </th>
                  ))}
                </tr>
              </thead>
              <tbody>
                {users.map((user) => (
                  <tr key={user.id} className="border-b border-gray-100 hover:bg-gray-50">
                    <td className="sticky left-0 bg-white z-10 px-4 py-2">
                      <div className="font-medium text-gray-900">
                        {user.firstName || user.lastName
                          ? `${user.firstName} ${user.lastName}`.trim()
                          : user.email}
                      </div>
                      <div className="text-xs text-gray-500">
                        {user.email}
                        {!user.isActive && (
                          <span className="ml-2 text-red-600 font-medium">inactive</span>
                        )}
                      </div>
                    </td>
                    {applications.map((app) => {
                      const hasBase = user.roles.includes(app.baseRole);
                      const cellKey = `${user.id}|${app.baseRole}`;
                      const menuKey = `${user.id}|${app.clientId}`;
                      const grantedPerms = app.permissions.filter((p) =>
                        user.roles.includes(p.role)
                      ).length;
                      return (
                        <td key={app.clientId} className="px-3 py-2 text-center relative">
                          <div className="inline-flex items-center gap-1">
                            <input
                              type="checkbox"
                              checked={hasBase}
                              disabled={pendingCell === cellKey}
                              onChange={() => toggleRole(user, app.baseRole, hasBase)}
                              className="w-4 h-4 text-indigo-600 border-gray-300 rounded focus:ring-indigo-500 disabled:opacity-40 cursor-pointer"
                              aria-label={`${app.displayName} access for ${user.email}`}
                            />
                            {app.permissions.length > 0 && (
                              <button
                                type="button"
                                onClick={() =>
                                  setOpenMenu(openMenu === menuKey ? null : menuKey)
                                }
                                className={`px-1.5 py-0.5 rounded text-xs font-bold leading-none ${
                                  grantedPerms > 0
                                    ? 'text-indigo-700 bg-indigo-50 hover:bg-indigo-100'
                                    : 'text-gray-400 hover:text-gray-700 hover:bg-gray-100'
                                }`}
                                title={`${app.displayName} permissions${grantedPerms > 0 ? ` (${grantedPerms} granted)` : ''}`}
                                aria-label={`${app.displayName} permissions for ${user.email}`}
                              >
                                &hellip;
                              </button>
                            )}
                          </div>

                          {openMenu === menuKey && (
                            <>
                              {/* click-away backdrop */}
                              <div
                                className="fixed inset-0 z-20"
                                onClick={() => setOpenMenu(null)}
                              />
                              <div className="absolute z-30 right-0 top-full mt-1 w-64 bg-white border border-gray-200 rounded-lg shadow-lg text-left">
                                <div className="px-3 py-2 border-b border-gray-100 text-xs font-semibold text-gray-500 uppercase">
                                  {app.displayName} permissions
                                </div>
                                <div className="py-1 max-h-64 overflow-y-auto">
                                  {app.permissions.map((perm) => {
                                    const granted = user.roles.includes(perm.role);
                                    const permCellKey = `${user.id}|${perm.role}`;
                                    return (
                                      <label
                                        key={perm.role}
                                        className="flex items-start gap-2 px-3 py-2 hover:bg-gray-50 cursor-pointer"
                                        title={perm.role}
                                      >
                                        <input
                                          type="checkbox"
                                          checked={granted}
                                          disabled={pendingCell === permCellKey}
                                          onChange={() =>
                                            toggleRole(user, perm.role, granted)
                                          }
                                          className="mt-0.5 w-4 h-4 text-indigo-600 border-gray-300 rounded focus:ring-indigo-500 disabled:opacity-40"
                                        />
                                        <span>
                                          <span className="block text-sm text-gray-900">
                                            {perm.label}
                                          </span>
                                          {perm.description && (
                                            <span className="block text-xs text-gray-500">
                                              {perm.description}
                                            </span>
                                          )}
                                        </span>
                                      </label>
                                    );
                                  })}
                                </div>
                              </div>
                            </>
                          )}
                        </td>
                      );
                    })}
                  </tr>
                ))}
                {users.length === 0 && (
                  <tr>
                    <td
                      colSpan={applications.length + 1}
                      className="px-4 py-8 text-center text-gray-500"
                    >
                      No users found.
                    </td>
                  </tr>
                )}
              </tbody>
            </table>
          </div>
        )}

        {total > TAKE && (
          <div className="flex items-center justify-between text-sm text-gray-600">
            <span>
              Showing {skip + 1}&ndash;{Math.min(skip + TAKE, total)} of {total} users
            </span>
            <div className="flex gap-2">
              <button
                type="button"
                disabled={skip === 0}
                onClick={() => changePage(Math.max(0, skip - TAKE))}
                className="px-3 py-1.5 border border-gray-300 rounded-lg disabled:opacity-40 hover:bg-gray-50"
              >
                Previous
              </button>
              <button
                type="button"
                disabled={skip + TAKE >= total}
                onClick={() => changePage(skip + TAKE)}
                className="px-3 py-1.5 border border-gray-300 rounded-lg disabled:opacity-40 hover:bg-gray-50"
              >
                Next
              </button>
            </div>
          </div>
        )}
      </div>
    </DashboardLayout>
  );
}
