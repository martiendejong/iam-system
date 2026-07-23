import { useState, useEffect, useCallback } from 'react';
import PortalLayout from '../../components/layout/PortalLayout';
import { portalApi } from '../../services/portalApi';
import type { ActivityEntry } from '../../services/portalApi';

const EVENT_TYPES = [
  'Login',
  'Logout',
  'PasswordChanged',
  'MfaEnabled',
  'MfaDisabled',
  'PasskeyRegistered',
  'PasskeyDeleted',
  'SessionRevoked',
  'ProfileUpdated',
];

const PAGE_SIZE = 20;

export default function PortalActivityPage() {
  const [activities, setActivities] = useState<ActivityEntry[]>([]);
  const [total, setTotal] = useState(0);
  const [page, setPage] = useState(1);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [eventType, setEventType] = useState('');

  const loadActivity = useCallback(async (currentPage?: number) => {
    try {
      setLoading(true);
      setError('');
      const data = await portalApi.getActivity({
        page: currentPage ?? page,
        pageSize: PAGE_SIZE,
        eventType: eventType || undefined,
      });
      setActivities(data.items);
      setTotal(data.total);
    } catch (err: any) {
      setError(err.response?.data?.error || 'Failed to load activity');
    } finally {
      setLoading(false);
    }
  }, [page, eventType]);

  useEffect(() => {
    loadActivity();
  }, [loadActivity]);

  const handlePageChange = (newPage: number) => {
    setPage(newPage);
    loadActivity(newPage);
  };

  const handleEventTypeChange = (value: string) => {
    setEventType(value);
    setPage(1);
  };

  const totalPages = Math.ceil(total / PAGE_SIZE);

  const getActionBadgeColor = (action: string): string => {
    switch (action) {
      case 'Login':
        return 'bg-blue-100 text-blue-800';
      case 'Logout':
        return 'bg-gray-100 text-gray-800';
      case 'PasswordChanged':
        return 'bg-yellow-100 text-yellow-800';
      case 'MfaEnabled':
      case 'PasskeyRegistered':
        return 'bg-green-100 text-green-800';
      case 'MfaDisabled':
      case 'PasskeyDeleted':
      case 'SessionRevoked':
        return 'bg-red-100 text-red-800';
      default:
        return 'bg-gray-100 text-gray-800';
    }
  };

  const parseDetails = (details?: string): Record<string, any> | null => {
    if (!details) return null;
    try {
      return JSON.parse(details);
    } catch {
      return null;
    }
  };

  return (
    <PortalLayout>
      <div className="bg-white rounded-lg shadow-sm">
        <div className="px-6 py-4 border-b border-gray-200">
          <div className="flex justify-between items-start">
            <div>
              <h2 className="text-lg font-semibold text-gray-900">Activity Log</h2>
              <p className="mt-1 text-sm text-gray-500">Your recent account activity and security events.</p>
            </div>
            <div>
              <select
                value={eventType}
                onChange={(e) => handleEventTypeChange(e.target.value)}
                className="block rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
              >
                <option value="">All Events</option>
                {EVENT_TYPES.map(type => (
                  <option key={type} value={type}>{type}</option>
                ))}
              </select>
            </div>
          </div>
        </div>

        <div className="px-6 py-4">
          {error && (
            <div className="mb-4 bg-red-50 border border-red-200 text-red-700 px-4 py-3 rounded text-sm">
              {error}
            </div>
          )}

          {loading ? (
            <div className="text-center py-8">
              <div className="inline-block animate-spin rounded-full h-8 w-8 border-b-2 border-indigo-600"></div>
              <p className="mt-2 text-sm text-gray-500">Loading activity...</p>
            </div>
          ) : activities.length === 0 ? (
            <p className="text-sm text-gray-500 text-center py-8">No activity found.</p>
          ) : (
            <div className="space-y-0">
              {/* Timeline */}
              <div className="flow-root">
                <ul className="-mb-8">
                  {activities.map((activity, index) => {
                    const details = parseDetails(activity.details);
                    return (
                      <li key={activity.id}>
                        <div className="relative pb-8">
                          {index < activities.length - 1 && (
                            <span className="absolute left-4 top-4 -ml-px h-full w-0.5 bg-gray-200" aria-hidden="true" />
                          )}
                          <div className="relative flex space-x-3">
                            <div>
                              <span className="h-8 w-8 rounded-full bg-gray-100 flex items-center justify-center ring-8 ring-white">
                                <span className="text-xs text-gray-500 font-medium">
                                  {activity.action.charAt(0)}
                                </span>
                              </span>
                            </div>
                            <div className="flex min-w-0 flex-1 justify-between space-x-4 pt-1">
                              <div>
                                <p className="text-sm text-gray-900">
                                  <span className={`inline-flex rounded-full px-2 py-0.5 text-xs font-semibold mr-2 ${getActionBadgeColor(activity.action)}`}>
                                    {activity.action}
                                  </span>
                                  <span className="text-gray-500">{activity.resource}</span>
                                </p>
                                <div className="mt-1 text-xs text-gray-500 space-x-3">
                                  {activity.ipAddress && (
                                    <span>IP: <span className="font-mono">{activity.ipAddress}</span></span>
                                  )}
                                  {details?.source && (
                                    <span>Source: {details.source}</span>
                                  )}
                                </div>
                              </div>
                              <div className="whitespace-nowrap text-right text-xs text-gray-500">
                                <time dateTime={activity.createdAt}>
                                  {new Date(activity.createdAt).toLocaleString()}
                                </time>
                              </div>
                            </div>
                          </div>
                        </div>
                      </li>
                    );
                  })}
                </ul>
              </div>
            </div>
          )}
        </div>

        {/* Pagination */}
        {!loading && totalPages > 1 && (
          <div className="px-6 py-4 border-t border-gray-200 flex items-center justify-between">
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
      </div>
    </PortalLayout>
  );
}
