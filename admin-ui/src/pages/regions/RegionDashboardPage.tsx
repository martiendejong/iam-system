import { useState, useEffect, useCallback } from 'react';
import DashboardLayout from '../../components/layout/DashboardLayout';
import { regionApi } from '../../services/regionApi';
import type {
  RegionConfig,
  RegionSyncEvent,
  RegionSyncSummary,
  RegisterRegionRequest,
} from '../../services/regionApi';
import {
  REGION_STATUS_LABELS,
  REGION_STATUS_COLORS,
  SYNC_STATUS_LABELS,
  SYNC_STATUS_COLORS,
  CONFLICT_RESOLUTION_LABELS,
} from '../../services/regionApi';

type Tab = 'regions' | 'sync' | 'events';

export default function RegionDashboardPage() {
  const [tab, setTab] = useState<Tab>('regions');
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');

  // Data
  const [regions, setRegions] = useState<RegionConfig[]>([]);
  const [syncSummary, setSyncSummary] = useState<RegionSyncSummary | null>(null);
  const [syncEvents, setSyncEvents] = useState<RegionSyncEvent[]>([]);

  // Modal
  const [showRegionModal, setShowRegionModal] = useState(false);
  const [editingRegion, setEditingRegion] = useState<RegionConfig | null>(null);
  const [showFailoverModal, setShowFailoverModal] = useState(false);

  // Form
  const [regionForm, setRegionForm] = useState<RegisterRegionRequest>({
    name: '',
    endpoint: '',
    isPrimary: false,
    status: 0,
    description: '',
    priority: 0,
  });

  const loadData = useCallback(async () => {
    try {
      setLoading(true);
      setError('');
      const [regionList, summary, events] = await Promise.all([
        regionApi.getRegions(),
        regionApi.getSyncStatus(),
        regionApi.getSyncEvents(),
      ]);
      setRegions(regionList);
      setSyncSummary(summary);
      setSyncEvents(events);
    } catch (err: any) {
      setError(err.response?.data?.message || err.message || 'Failed to load data');
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    loadData();
  }, [loadData]);

  // ── Region CRUD ─────────────────────────────────────────────

  const openCreateRegion = () => {
    setEditingRegion(null);
    setRegionForm({ name: '', endpoint: '', isPrimary: false, status: 0, description: '', priority: 0 });
    setShowRegionModal(true);
  };

  const openEditRegion = (region: RegionConfig) => {
    setEditingRegion(region);
    setRegionForm({
      name: region.name,
      endpoint: region.endpoint,
      isPrimary: region.isPrimary,
      status: region.status,
      description: region.description || '',
      priority: region.priority,
    });
    setShowRegionModal(true);
  };

  const saveRegion = async () => {
    try {
      if (editingRegion) {
        await regionApi.updateRegion(editingRegion.id, regionForm);
      } else {
        await regionApi.registerRegion(regionForm);
      }
      setShowRegionModal(false);
      await loadData();
    } catch (err: any) {
      setError(err.response?.data || err.message || 'Failed to save region');
    }
  };

  const deleteRegion = async (id: string) => {
    if (!confirm('Are you sure you want to delete this region?')) return;
    try {
      await regionApi.deleteRegion(id);
      await loadData();
    } catch (err: any) {
      setError(err.response?.data || err.message || 'Failed to delete region');
    }
  };

  const checkHealth = async (id: string) => {
    try {
      await regionApi.checkRegionHealth(id);
      await loadData();
    } catch (err: any) {
      setError(err.response?.data || err.message || 'Health check failed');
    }
  };

  // ── Failover ────────────────────────────────────────────────

  const triggerFailover = async (targetRegionId?: string) => {
    try {
      await regionApi.triggerFailover(targetRegionId ? { targetRegionId } : undefined);
      setShowFailoverModal(false);
      await loadData();
    } catch (err: any) {
      setError(err.response?.data || err.message || 'Failover failed');
    }
  };

  // ── Conflict Resolution ─────────────────────────────────────

  const resolveConflict = async (eventId: string, resolution: number) => {
    try {
      await regionApi.resolveSyncConflict(eventId, { resolution });
      await loadData();
    } catch (err: any) {
      setError(err.response?.data || err.message || 'Failed to resolve conflict');
    }
  };

  // ── Render Helpers ──────────────────────────────────────────

  const primaryRegion = regions.find((r) => r.isPrimary);
  const standbyRegions = regions.filter((r) => !r.isPrimary);

  const formatDate = (date?: string) => {
    if (!date) return 'Never';
    return new Date(date).toLocaleString();
  };

  return (
    <DashboardLayout>
      <div className="space-y-6">
        {/* Header */}
        <div className="flex justify-between items-center">
          <div>
            <h1 className="text-2xl font-bold text-gray-900">Multi-Region High Availability</h1>
            <p className="mt-1 text-sm text-gray-500">
              Manage regions, monitor health, and control failover
            </p>
          </div>
          <div className="flex gap-2">
            <button
              onClick={() => setShowFailoverModal(true)}
              className="inline-flex items-center px-4 py-2 border border-red-300 text-sm font-medium rounded-md text-red-700 bg-white hover:bg-red-50"
            >
              Trigger Failover
            </button>
            <button
              onClick={openCreateRegion}
              className="inline-flex items-center px-4 py-2 border border-transparent text-sm font-medium rounded-md text-white bg-indigo-600 hover:bg-indigo-700"
            >
              Register Region
            </button>
          </div>
        </div>

        {/* Error */}
        {error && (
          <div className="bg-red-50 border border-red-200 rounded-md p-4">
            <p className="text-sm text-red-800">{error}</p>
            <button onClick={() => setError('')} className="text-sm text-red-600 underline mt-1">
              Dismiss
            </button>
          </div>
        )}

        {/* Summary Cards */}
        <div className="grid grid-cols-1 md:grid-cols-4 gap-4">
          <div className="bg-white rounded-lg shadow p-4">
            <p className="text-sm text-gray-500">Primary Region</p>
            <p className="text-lg font-semibold text-gray-900">{primaryRegion?.name || 'None'}</p>
            {primaryRegion && (
              <span className={`inline-flex items-center px-2.5 py-0.5 rounded-full text-xs font-medium mt-1 ${REGION_STATUS_COLORS[primaryRegion.status]}`}>
                {REGION_STATUS_LABELS[primaryRegion.status]}
              </span>
            )}
          </div>
          <div className="bg-white rounded-lg shadow p-4">
            <p className="text-sm text-gray-500">Total Regions</p>
            <p className="text-lg font-semibold text-gray-900">{regions.length}</p>
            <p className="text-xs text-gray-400 mt-1">
              {regions.filter((r) => r.status === 0).length} active, {regions.filter((r) => r.status === 3).length} offline
            </p>
          </div>
          <div className="bg-white rounded-lg shadow p-4">
            <p className="text-sm text-gray-500">Sync Status</p>
            <p className="text-lg font-semibold text-gray-900">
              {syncSummary ? `${syncSummary.pendingEvents} pending` : 'Loading...'}
            </p>
            {syncSummary && syncSummary.conflictEvents > 0 && (
              <p className="text-xs text-yellow-600 mt-1">{syncSummary.conflictEvents} conflicts</p>
            )}
          </div>
          <div className="bg-white rounded-lg shadow p-4">
            <p className="text-sm text-gray-500">Avg Sync Latency</p>
            <p className="text-lg font-semibold text-gray-900">
              {syncSummary ? `${syncSummary.averageSyncLatencyMs}ms` : 'N/A'}
            </p>
            <p className="text-xs text-gray-400 mt-1">
              Last sync: {syncSummary ? formatDate(syncSummary.lastSyncAt) : 'Never'}
            </p>
          </div>
        </div>

        {/* Tabs */}
        <div className="border-b border-gray-200">
          <nav className="-mb-px flex space-x-8">
            {(['regions', 'sync', 'events'] as Tab[]).map((t) => (
              <button
                key={t}
                onClick={() => setTab(t)}
                className={`py-4 px-1 border-b-2 font-medium text-sm ${
                  tab === t
                    ? 'border-indigo-500 text-indigo-600'
                    : 'border-transparent text-gray-500 hover:text-gray-700 hover:border-gray-300'
                }`}
              >
                {t === 'regions' ? 'Regions' : t === 'sync' ? 'Sync Summary' : 'Sync Events'}
              </button>
            ))}
          </nav>
        </div>

        {loading ? (
          <div className="text-center py-12 text-gray-500">Loading...</div>
        ) : (
          <>
            {/* Regions Tab */}
            {tab === 'regions' && (
              <div className="bg-white shadow overflow-hidden rounded-lg">
                <table className="min-w-full divide-y divide-gray-200">
                  <thead className="bg-gray-50">
                    <tr>
                      <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">Region</th>
                      <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">Endpoint</th>
                      <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">Status</th>
                      <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">Latency</th>
                      <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">Last Health Check</th>
                      <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">Priority</th>
                      <th className="px-6 py-3 text-right text-xs font-medium text-gray-500 uppercase tracking-wider">Actions</th>
                    </tr>
                  </thead>
                  <tbody className="bg-white divide-y divide-gray-200">
                    {regions.length === 0 ? (
                      <tr>
                        <td colSpan={7} className="px-6 py-8 text-center text-gray-500">
                          No regions registered. Click "Register Region" to add one.
                        </td>
                      </tr>
                    ) : (
                      regions.map((region) => (
                        <tr key={region.id} className={region.isPrimary ? 'bg-indigo-50' : ''}>
                          <td className="px-6 py-4 whitespace-nowrap">
                            <div className="flex items-center">
                              <div>
                                <div className="text-sm font-medium text-gray-900">
                                  {region.name}
                                  {region.isPrimary && (
                                    <span className="ml-2 inline-flex items-center px-2 py-0.5 rounded text-xs font-medium bg-indigo-100 text-indigo-800">
                                      PRIMARY
                                    </span>
                                  )}
                                </div>
                                {region.description && (
                                  <div className="text-xs text-gray-500">{region.description}</div>
                                )}
                              </div>
                            </div>
                          </td>
                          <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-500 font-mono">
                            {region.endpoint}
                          </td>
                          <td className="px-6 py-4 whitespace-nowrap">
                            <span className={`inline-flex items-center px-2.5 py-0.5 rounded-full text-xs font-medium ${REGION_STATUS_COLORS[region.status]}`}>
                              {REGION_STATUS_LABELS[region.status]}
                            </span>
                          </td>
                          <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-500">
                            {region.latencyMs >= 0 ? `${region.latencyMs}ms` : 'N/A'}
                          </td>
                          <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-500">
                            {formatDate(region.lastHealthCheck)}
                          </td>
                          <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-500">
                            {region.priority}
                          </td>
                          <td className="px-6 py-4 whitespace-nowrap text-right text-sm font-medium space-x-2">
                            <button
                              onClick={() => checkHealth(region.id)}
                              className="text-green-600 hover:text-green-900"
                            >
                              Check Health
                            </button>
                            <button
                              onClick={() => openEditRegion(region)}
                              className="text-indigo-600 hover:text-indigo-900"
                            >
                              Edit
                            </button>
                            {!region.isPrimary && (
                              <button
                                onClick={() => deleteRegion(region.id)}
                                className="text-red-600 hover:text-red-900"
                              >
                                Delete
                              </button>
                            )}
                          </td>
                        </tr>
                      ))
                    )}
                  </tbody>
                </table>
              </div>
            )}

            {/* Sync Summary Tab */}
            {tab === 'sync' && syncSummary && (
              <div className="grid grid-cols-1 md:grid-cols-2 gap-6">
                <div className="bg-white shadow rounded-lg p-6">
                  <h3 className="text-lg font-medium text-gray-900 mb-4">Synchronization Overview</h3>
                  <dl className="grid grid-cols-2 gap-4">
                    <div>
                      <dt className="text-sm text-gray-500">Total Events</dt>
                      <dd className="text-2xl font-semibold text-gray-900">{syncSummary.totalEvents}</dd>
                    </div>
                    <div>
                      <dt className="text-sm text-gray-500">Completed</dt>
                      <dd className="text-2xl font-semibold text-green-600">{syncSummary.completedEvents}</dd>
                    </div>
                    <div>
                      <dt className="text-sm text-gray-500">Pending</dt>
                      <dd className="text-2xl font-semibold text-blue-600">{syncSummary.pendingEvents}</dd>
                    </div>
                    <div>
                      <dt className="text-sm text-gray-500">Failed</dt>
                      <dd className="text-2xl font-semibold text-red-600">{syncSummary.failedEvents}</dd>
                    </div>
                    <div>
                      <dt className="text-sm text-gray-500">Conflicts</dt>
                      <dd className="text-2xl font-semibold text-yellow-600">{syncSummary.conflictEvents}</dd>
                    </div>
                    <div>
                      <dt className="text-sm text-gray-500">Avg Latency</dt>
                      <dd className="text-2xl font-semibold text-gray-900">{syncSummary.averageSyncLatencyMs}ms</dd>
                    </div>
                  </dl>
                </div>

                <div className="bg-white shadow rounded-lg p-6">
                  <h3 className="text-lg font-medium text-gray-900 mb-4">Region Health Map</h3>
                  <div className="space-y-3">
                    {regions.map((region) => (
                      <div key={region.id} className="flex items-center justify-between p-3 bg-gray-50 rounded-lg">
                        <div className="flex items-center space-x-3">
                          <div
                            className={`w-3 h-3 rounded-full ${
                              region.status === 0
                                ? 'bg-green-400'
                                : region.status === 1
                                ? 'bg-blue-400'
                                : region.status === 2
                                ? 'bg-yellow-400'
                                : 'bg-red-400'
                            }`}
                          />
                          <div>
                            <p className="text-sm font-medium text-gray-900">
                              {region.name}
                              {region.isPrimary && <span className="ml-1 text-xs text-indigo-600">(Primary)</span>}
                            </p>
                            <p className="text-xs text-gray-500">{region.endpoint}</p>
                          </div>
                        </div>
                        <div className="text-right">
                          <p className="text-sm font-medium text-gray-900">
                            {region.latencyMs >= 0 ? `${region.latencyMs}ms` : 'N/A'}
                          </p>
                          <p className="text-xs text-gray-500">{REGION_STATUS_LABELS[region.status]}</p>
                        </div>
                      </div>
                    ))}
                    {regions.length === 0 && (
                      <p className="text-sm text-gray-500 text-center py-4">No regions registered</p>
                    )}
                  </div>
                </div>
              </div>
            )}

            {/* Sync Events Tab */}
            {tab === 'events' && (
              <div className="bg-white shadow overflow-hidden rounded-lg">
                <table className="min-w-full divide-y divide-gray-200">
                  <thead className="bg-gray-50">
                    <tr>
                      <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">Source &rarr; Target</th>
                      <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">Entity</th>
                      <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">Status</th>
                      <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">Resolution</th>
                      <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">Created</th>
                      <th className="px-6 py-3 text-right text-xs font-medium text-gray-500 uppercase tracking-wider">Actions</th>
                    </tr>
                  </thead>
                  <tbody className="bg-white divide-y divide-gray-200">
                    {syncEvents.length === 0 ? (
                      <tr>
                        <td colSpan={6} className="px-6 py-8 text-center text-gray-500">
                          No sync events recorded yet.
                        </td>
                      </tr>
                    ) : (
                      syncEvents.map((event) => (
                        <tr key={event.id}>
                          <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-900">
                            {event.sourceRegion} &rarr; {event.targetRegion}
                          </td>
                          <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-500">
                            <span className="font-medium">{event.entityType}</span>
                            <span className="text-xs text-gray-400 ml-1">/{event.entityId.substring(0, 8)}...</span>
                          </td>
                          <td className="px-6 py-4 whitespace-nowrap">
                            <span className={`inline-flex items-center px-2.5 py-0.5 rounded-full text-xs font-medium ${SYNC_STATUS_COLORS[event.syncStatus]}`}>
                              {SYNC_STATUS_LABELS[event.syncStatus]}
                            </span>
                          </td>
                          <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-500">
                            {CONFLICT_RESOLUTION_LABELS[event.conflictResolution] || 'None'}
                          </td>
                          <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-500">
                            {formatDate(event.createdAt)}
                          </td>
                          <td className="px-6 py-4 whitespace-nowrap text-right text-sm font-medium">
                            {event.syncStatus === 4 && (
                              <div className="flex justify-end space-x-1">
                                <button
                                  onClick={() => resolveConflict(event.id, 1)}
                                  className="text-xs text-indigo-600 hover:text-indigo-900"
                                  title="Last Writer Wins"
                                >
                                  LWW
                                </button>
                                <button
                                  onClick={() => resolveConflict(event.id, 2)}
                                  className="text-xs text-blue-600 hover:text-blue-900"
                                  title="Source Wins"
                                >
                                  Source
                                </button>
                                <button
                                  onClick={() => resolveConflict(event.id, 3)}
                                  className="text-xs text-green-600 hover:text-green-900"
                                  title="Target Wins"
                                >
                                  Target
                                </button>
                              </div>
                            )}
                          </td>
                        </tr>
                      ))
                    )}
                  </tbody>
                </table>
              </div>
            )}
          </>
        )}

        {/* Register/Edit Region Modal */}
        {showRegionModal && (
          <div className="fixed inset-0 bg-gray-600 bg-opacity-50 flex items-center justify-center z-50">
            <div className="bg-white rounded-lg shadow-xl p-6 w-full max-w-lg">
              <h3 className="text-lg font-medium text-gray-900 mb-4">
                {editingRegion ? 'Edit Region' : 'Register New Region'}
              </h3>
              <div className="space-y-4">
                <div>
                  <label className="block text-sm font-medium text-gray-700">Name</label>
                  <input
                    type="text"
                    value={regionForm.name}
                    onChange={(e) => setRegionForm({ ...regionForm, name: e.target.value })}
                    className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                    placeholder="eu-west-1"
                  />
                </div>
                <div>
                  <label className="block text-sm font-medium text-gray-700">Endpoint URL</label>
                  <input
                    type="text"
                    value={regionForm.endpoint}
                    onChange={(e) => setRegionForm({ ...regionForm, endpoint: e.target.value })}
                    className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                    placeholder="https://eu-west-1.iam.example.com"
                  />
                </div>
                <div>
                  <label className="block text-sm font-medium text-gray-700">Description</label>
                  <input
                    type="text"
                    value={regionForm.description || ''}
                    onChange={(e) => setRegionForm({ ...regionForm, description: e.target.value })}
                    className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                    placeholder="Primary EU region in Ireland"
                  />
                </div>
                <div className="grid grid-cols-2 gap-4">
                  <div>
                    <label className="block text-sm font-medium text-gray-700">Priority</label>
                    <input
                      type="number"
                      value={regionForm.priority || 0}
                      onChange={(e) => setRegionForm({ ...regionForm, priority: parseInt(e.target.value) || 0 })}
                      className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                      min={0}
                    />
                  </div>
                  <div>
                    <label className="block text-sm font-medium text-gray-700">Status</label>
                    <select
                      value={regionForm.status || 0}
                      onChange={(e) => setRegionForm({ ...regionForm, status: parseInt(e.target.value) })}
                      className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                    >
                      <option value={0}>Active</option>
                      <option value={1}>Standby</option>
                      <option value={2}>Degraded</option>
                      <option value={3}>Offline</option>
                    </select>
                  </div>
                </div>
                <div className="flex items-center">
                  <input
                    type="checkbox"
                    id="isPrimary"
                    checked={regionForm.isPrimary || false}
                    onChange={(e) => setRegionForm({ ...regionForm, isPrimary: e.target.checked })}
                    className="h-4 w-4 text-indigo-600 focus:ring-indigo-500 border-gray-300 rounded"
                  />
                  <label htmlFor="isPrimary" className="ml-2 block text-sm text-gray-900">
                    Primary Region
                  </label>
                </div>
              </div>
              <div className="mt-6 flex justify-end space-x-3">
                <button
                  onClick={() => setShowRegionModal(false)}
                  className="px-4 py-2 text-sm font-medium text-gray-700 bg-white border border-gray-300 rounded-md hover:bg-gray-50"
                >
                  Cancel
                </button>
                <button
                  onClick={saveRegion}
                  className="px-4 py-2 text-sm font-medium text-white bg-indigo-600 border border-transparent rounded-md hover:bg-indigo-700"
                >
                  {editingRegion ? 'Update' : 'Register'}
                </button>
              </div>
            </div>
          </div>
        )}

        {/* Failover Modal */}
        {showFailoverModal && (
          <div className="fixed inset-0 bg-gray-600 bg-opacity-50 flex items-center justify-center z-50">
            <div className="bg-white rounded-lg shadow-xl p-6 w-full max-w-md">
              <h3 className="text-lg font-medium text-red-900 mb-2">Trigger Failover</h3>
              <p className="text-sm text-gray-500 mb-4">
                This will promote a standby region to primary and demote the current primary.
                Current primary: <strong>{primaryRegion?.name || 'None'}</strong>
              </p>
              <div className="space-y-2 mb-4">
                <button
                  onClick={() => triggerFailover()}
                  className="w-full text-left px-4 py-3 border rounded-md hover:bg-gray-50"
                >
                  <p className="text-sm font-medium text-gray-900">Auto-select best region</p>
                  <p className="text-xs text-gray-500">Picks the healthiest standby with lowest priority</p>
                </button>
                {standbyRegions
                  .filter((r) => r.status === 0)
                  .map((region) => (
                    <button
                      key={region.id}
                      onClick={() => triggerFailover(region.id)}
                      className="w-full text-left px-4 py-3 border rounded-md hover:bg-gray-50"
                    >
                      <p className="text-sm font-medium text-gray-900">Failover to {region.name}</p>
                      <p className="text-xs text-gray-500">
                        Latency: {region.latencyMs}ms, Priority: {region.priority}
                      </p>
                    </button>
                  ))}
              </div>
              <div className="flex justify-end">
                <button
                  onClick={() => setShowFailoverModal(false)}
                  className="px-4 py-2 text-sm font-medium text-gray-700 bg-white border border-gray-300 rounded-md hover:bg-gray-50"
                >
                  Cancel
                </button>
              </div>
            </div>
          </div>
        )}
      </div>
    </DashboardLayout>
  );
}
