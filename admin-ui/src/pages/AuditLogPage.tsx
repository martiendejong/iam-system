import { useState, useEffect, useRef, useCallback } from 'react';
import DashboardLayout from '../components/layout/DashboardLayout';
import { auditApi } from '../services/auditApi';
import type { AuditEvent, AuditFilter } from '../types/audit';

const EVENT_TYPES = [
  '', 'Login', 'Logout', 'UserCreated', 'UserUpdated', 'UserDeleted',
  'RoleAssigned', 'RoleRevoked', 'PermissionGranted', 'PermissionRevoked',
  'TenantCreated', 'TenantUpdated', 'PasswordChanged', 'TokenRefreshed',
];

const PAGE_SIZE = 50;

export default function AuditLogPage() {
  const [events, setEvents] = useState<AuditEvent[]>([]);
  const [total, setTotal] = useState(0);
  const [page, setPage] = useState(1);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [expandedRow, setExpandedRow] = useState<string | null>(null);
  const [autoRefresh, setAutoRefresh] = useState(false);
  const autoRefreshRef = useRef<ReturnType<typeof setInterval> | null>(null);

  const [filters, setFilters] = useState<AuditFilter>({
    startDate: '',
    endDate: '',
    eventType: '',
    userId: '',
    tenantId: '',
    success: undefined,
    search: '',
  });

  const loadEvents = useCallback(async (currentPage?: number) => {
    try {
      setLoading(true);
      setError('');
      const params: AuditFilter & { page: number; pageSize: number } = {
        ...filters,
        page: currentPage ?? page,
        pageSize: PAGE_SIZE,
      };
      // Clean up undefined/empty values
      const cleanParams = Object.fromEntries(
        Object.entries(params).filter(([, v]) => v !== undefined && v !== '')
      ) as typeof params;
      const data = await auditApi.getEvents(cleanParams);
      setEvents(data.items);
      setTotal(data.total);
    } catch (err: any) {
      setError(err.response?.data?.message || 'Failed to load audit events');
    } finally {
      setLoading(false);
    }
  }, [filters, page]);

  useEffect(() => {
    loadEvents();
  }, [loadEvents]);

  // Auto-refresh
  useEffect(() => {
    if (autoRefresh) {
      autoRefreshRef.current = setInterval(() => {
        loadEvents();
      }, 30000);
    } else {
      if (autoRefreshRef.current) {
        clearInterval(autoRefreshRef.current);
        autoRefreshRef.current = null;
      }
    }
    return () => {
      if (autoRefreshRef.current) {
        clearInterval(autoRefreshRef.current);
      }
    };
  }, [autoRefresh, loadEvents]);

  const handleFilterChange = (key: keyof AuditFilter, value: string | boolean | undefined) => {
    setFilters(prev => ({ ...prev, [key]: value }));
    setPage(1);
  };

  const handlePageChange = (newPage: number) => {
    setPage(newPage);
    loadEvents(newPage);
  };

  const handleExportCSV = () => {
    if (events.length === 0) return;

    const headers = ['Timestamp', 'Event Type', 'User', 'Tenant', 'Action', 'Resource Type', 'Resource ID', 'IP Address', 'Status', 'Risk Score', 'Details'];
    const rows = events.map(e => [
      e.createdAt,
      e.eventType,
      e.userName || e.userId || '',
      e.tenantName || e.tenantId || '',
      e.action,
      e.resourceType || '',
      e.resourceId || '',
      e.ipAddress || '',
      e.success ? 'Success' : 'Failure',
      e.riskScore?.toString() || '',
      (e.details || '').replace(/"/g, '""'),
    ]);

    const csv = [headers.join(','), ...rows.map(r => r.map(v => `"${v}"`).join(','))].join('\n');
    const blob = new Blob([csv], { type: 'text/csv;charset=utf-8;' });
    const url = URL.createObjectURL(blob);
    const link = document.createElement('a');
    link.href = url;
    link.download = `audit-log-${new Date().toISOString().slice(0, 10)}.csv`;
    link.click();
    URL.revokeObjectURL(url);
  };

  const totalPages = Math.ceil(total / PAGE_SIZE);

  const formatDetails = (details?: string) => {
    if (!details) return 'No details available';
    try {
      return JSON.stringify(JSON.parse(details), null, 2);
    } catch {
      return details;
    }
  };

  return (
    <DashboardLayout>
      <div className="px-4 sm:px-6 lg:px-8">
        {/* Header */}
        <div className="sm:flex sm:items-center">
          <div className="sm:flex-auto">
            <h1 className="text-2xl font-semibold text-gray-900">Audit Log</h1>
            <p className="mt-2 text-sm text-gray-700">
              Complete audit trail of all system events and user actions.
            </p>
          </div>
          <div className="mt-4 sm:mt-0 sm:ml-16 sm:flex-none flex gap-2">
            <button
              onClick={() => setAutoRefresh(!autoRefresh)}
              className={`inline-flex items-center px-4 py-2 text-sm font-medium rounded-md border ${
                autoRefresh
                  ? 'bg-green-50 text-green-700 border-green-300'
                  : 'bg-white text-gray-700 border-gray-300 hover:bg-gray-50'
              }`}
            >
              {autoRefresh ? (
                <>
                  <span className="relative flex h-2 w-2 mr-2">
                    <span className="animate-ping absolute inline-flex h-full w-full rounded-full bg-green-400 opacity-75"></span>
                    <span className="relative inline-flex rounded-full h-2 w-2 bg-green-500"></span>
                  </span>
                  Auto-Refresh ON
                </>
              ) : (
                'Auto-Refresh OFF'
              )}
            </button>
            <button
              onClick={handleExportCSV}
              disabled={events.length === 0}
              className="inline-flex items-center px-4 py-2 text-sm font-medium rounded-md border border-gray-300 bg-white text-gray-700 hover:bg-gray-50 disabled:opacity-50 disabled:cursor-not-allowed"
            >
              Export CSV
            </button>
          </div>
        </div>

        {/* Date Range */}
        <div className="mt-6 grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-4 gap-4">
          <div>
            <label className="block text-sm font-medium text-gray-700 mb-1">Start Date</label>
            <input
              type="datetime-local"
              value={filters.startDate || ''}
              onChange={(e) => handleFilterChange('startDate', e.target.value)}
              className="block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
            />
          </div>
          <div>
            <label className="block text-sm font-medium text-gray-700 mb-1">End Date</label>
            <input
              type="datetime-local"
              value={filters.endDate || ''}
              onChange={(e) => handleFilterChange('endDate', e.target.value)}
              className="block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
            />
          </div>
          <div>
            <label className="block text-sm font-medium text-gray-700 mb-1">Event Type</label>
            <select
              value={filters.eventType || ''}
              onChange={(e) => handleFilterChange('eventType', e.target.value)}
              className="block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
            >
              <option value="">All Types</option>
              {EVENT_TYPES.filter(Boolean).map(type => (
                <option key={type} value={type}>{type}</option>
              ))}
            </select>
          </div>
          <div>
            <label className="block text-sm font-medium text-gray-700 mb-1">Status</label>
            <select
              value={filters.success === undefined ? '' : filters.success ? 'true' : 'false'}
              onChange={(e) => {
                const val = e.target.value;
                handleFilterChange('success', val === '' ? undefined : val === 'true');
              }}
              className="block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
            >
              <option value="">All</option>
              <option value="true">Success</option>
              <option value="false">Failure</option>
            </select>
          </div>
        </div>

        {/* Search */}
        <div className="mt-4 flex gap-4">
          <div className="flex-1">
            <input
              type="text"
              placeholder="Search across all fields..."
              value={filters.search || ''}
              onChange={(e) => handleFilterChange('search', e.target.value)}
              className="block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
            />
          </div>
          <div>
            <input
              type="text"
              placeholder="User ID or name..."
              value={filters.userId || ''}
              onChange={(e) => handleFilterChange('userId', e.target.value)}
              className="block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
            />
          </div>
          <div>
            <input
              type="text"
              placeholder="Tenant ID..."
              value={filters.tenantId || ''}
              onChange={(e) => handleFilterChange('tenantId', e.target.value)}
              className="block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
            />
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
            <p className="mt-2 text-sm text-gray-500">Loading audit events...</p>
          </div>
        ) : (
          /* Events Table */
          <div className="mt-6 flex flex-col">
            <div className="-my-2 -mx-4 overflow-x-auto sm:-mx-6 lg:-mx-8">
              <div className="inline-block min-w-full py-2 align-middle md:px-6 lg:px-8">
                <div className="overflow-hidden shadow ring-1 ring-black ring-opacity-5 md:rounded-lg">
                  <table className="min-w-full divide-y divide-gray-300">
                    <thead className="bg-gray-50">
                      <tr>
                        <th className="px-3 py-3.5 text-left text-sm font-semibold text-gray-900">Timestamp</th>
                        <th className="px-3 py-3.5 text-left text-sm font-semibold text-gray-900">Event Type</th>
                        <th className="px-3 py-3.5 text-left text-sm font-semibold text-gray-900">User</th>
                        <th className="px-3 py-3.5 text-left text-sm font-semibold text-gray-900">Tenant</th>
                        <th className="px-3 py-3.5 text-left text-sm font-semibold text-gray-900">Action</th>
                        <th className="px-3 py-3.5 text-left text-sm font-semibold text-gray-900">Resource</th>
                        <th className="px-3 py-3.5 text-left text-sm font-semibold text-gray-900">IP Address</th>
                        <th className="px-3 py-3.5 text-left text-sm font-semibold text-gray-900">Status</th>
                        <th className="px-3 py-3.5 text-left text-sm font-semibold text-gray-900">Risk</th>
                      </tr>
                    </thead>
                    <tbody className="divide-y divide-gray-200 bg-white">
                      {events.length === 0 ? (
                        <tr>
                          <td colSpan={9} className="px-3 py-8 text-sm text-gray-500 text-center">
                            No audit events found matching your filters.
                          </td>
                        </tr>
                      ) : (
                        events.map((event) => {
                          const isHighRisk = (event.riskScore ?? 0) > 70;
                          const isExpanded = expandedRow === event.id;
                          return (
                            <tr key={event.id} className="group">
                              <td colSpan={9} className="p-0">
                                <div
                                  onClick={() => setExpandedRow(isExpanded ? null : event.id)}
                                  className={`cursor-pointer grid grid-cols-9 items-center px-3 py-4 text-sm hover:bg-gray-50 transition-colors ${
                                    isHighRisk ? 'bg-red-50 hover:bg-red-100' : ''
                                  }`}
                                >
                                  <span className="text-gray-500 whitespace-nowrap">
                                    {new Date(event.createdAt).toLocaleString()}
                                  </span>
                                  <span className="text-gray-900 font-medium">
                                    {event.eventType}
                                  </span>
                                  <span className="text-gray-600 truncate" title={event.userId}>
                                    {event.userName || event.userId?.slice(0, 8) || '-'}
                                  </span>
                                  <span className="text-gray-600 truncate" title={event.tenantId}>
                                    {event.tenantName || event.tenantId?.slice(0, 8) || '-'}
                                  </span>
                                  <span className="text-gray-900">
                                    {event.action}
                                  </span>
                                  <span className="text-gray-600 truncate">
                                    {event.resourceType ? `${event.resourceType}${event.resourceId ? `:${event.resourceId.slice(0, 8)}` : ''}` : '-'}
                                  </span>
                                  <span className="text-gray-500 font-mono text-xs">
                                    {event.ipAddress || '-'}
                                  </span>
                                  <span>
                                    <span className={`inline-flex rounded-full px-2 text-xs font-semibold leading-5 ${
                                      event.success
                                        ? 'bg-green-100 text-green-800'
                                        : 'bg-red-100 text-red-800'
                                    }`}>
                                      {event.success ? 'Success' : 'Failure'}
                                    </span>
                                  </span>
                                  <span>
                                    {event.riskScore !== undefined && event.riskScore !== null ? (
                                      <span className={`inline-flex items-center rounded-full px-2 py-0.5 text-xs font-semibold ${
                                        event.riskScore > 70
                                          ? 'bg-red-100 text-red-800'
                                          : event.riskScore > 40
                                          ? 'bg-yellow-100 text-yellow-800'
                                          : 'bg-gray-100 text-gray-600'
                                      }`}>
                                        {event.riskScore}
                                      </span>
                                    ) : (
                                      <span className="text-gray-400">-</span>
                                    )}
                                  </span>
                                </div>
                                {/* Expanded details */}
                                {isExpanded && (
                                  <div className="px-6 pb-4 bg-gray-50 border-t border-gray-200">
                                    <div className="grid grid-cols-2 gap-4 pt-4">
                                      <div>
                                        <h4 className="text-xs font-semibold text-gray-500 uppercase mb-1">Event Details</h4>
                                        <pre className="bg-white rounded p-3 text-xs text-gray-700 overflow-x-auto border border-gray-200 max-h-48 overflow-y-auto">
                                          {formatDetails(event.details)}
                                        </pre>
                                      </div>
                                      <div className="space-y-3">
                                        <div>
                                          <span className="text-xs font-semibold text-gray-500 uppercase">Event ID</span>
                                          <p className="text-sm text-gray-700 font-mono">{event.id}</p>
                                        </div>
                                        {event.userId && (
                                          <div>
                                            <span className="text-xs font-semibold text-gray-500 uppercase">User ID</span>
                                            <p className="text-sm text-gray-700 font-mono">{event.userId}</p>
                                          </div>
                                        )}
                                        {event.tenantId && (
                                          <div>
                                            <span className="text-xs font-semibold text-gray-500 uppercase">Tenant ID</span>
                                            <p className="text-sm text-gray-700 font-mono">{event.tenantId}</p>
                                          </div>
                                        )}
                                        {event.userAgent && (
                                          <div>
                                            <span className="text-xs font-semibold text-gray-500 uppercase">User Agent</span>
                                            <p className="text-sm text-gray-700 break-all">{event.userAgent}</p>
                                          </div>
                                        )}
                                        {event.resourceId && (
                                          <div>
                                            <span className="text-xs font-semibold text-gray-500 uppercase">Resource</span>
                                            <p className="text-sm text-gray-700 font-mono">{event.resourceType}: {event.resourceId}</p>
                                          </div>
                                        )}
                                      </div>
                                    </div>
                                  </div>
                                )}
                              </td>
                            </tr>
                          );
                        })
                      )}
                    </tbody>
                  </table>
                </div>
              </div>
            </div>
          </div>
        )}

        {/* Pagination */}
        {!loading && totalPages > 1 && (
          <div className="mt-4 flex items-center justify-between">
            <div className="text-sm text-gray-700">
              Showing {((page - 1) * PAGE_SIZE) + 1} to {Math.min(page * PAGE_SIZE, total)} of {total} events
            </div>
            <div className="flex gap-2">
              <button
                onClick={() => handlePageChange(page - 1)}
                disabled={page <= 1}
                className="px-3 py-1.5 text-sm font-medium rounded-md border border-gray-300 bg-white text-gray-700 hover:bg-gray-50 disabled:opacity-50 disabled:cursor-not-allowed"
              >
                Previous
              </button>
              {/* Page numbers */}
              {Array.from({ length: Math.min(5, totalPages) }, (_, i) => {
                let pageNum: number;
                if (totalPages <= 5) {
                  pageNum = i + 1;
                } else if (page <= 3) {
                  pageNum = i + 1;
                } else if (page >= totalPages - 2) {
                  pageNum = totalPages - 4 + i;
                } else {
                  pageNum = page - 2 + i;
                }
                return (
                  <button
                    key={pageNum}
                    onClick={() => handlePageChange(pageNum)}
                    className={`px-3 py-1.5 text-sm font-medium rounded-md border ${
                      page === pageNum
                        ? 'bg-indigo-600 text-white border-indigo-600'
                        : 'border-gray-300 bg-white text-gray-700 hover:bg-gray-50'
                    }`}
                  >
                    {pageNum}
                  </button>
                );
              })}
              <button
                onClick={() => handlePageChange(page + 1)}
                disabled={page >= totalPages}
                className="px-3 py-1.5 text-sm font-medium rounded-md border border-gray-300 bg-white text-gray-700 hover:bg-gray-50 disabled:opacity-50 disabled:cursor-not-allowed"
              >
                Next
              </button>
            </div>
          </div>
        )}

        {/* Results Count (when single page) */}
        {!loading && totalPages <= 1 && (
          <div className="mt-4 text-sm text-gray-700">
            Showing {events.length} of {total} events
          </div>
        )}
      </div>
    </DashboardLayout>
  );
}
