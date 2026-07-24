import { useState, useEffect } from 'react';
import DashboardLayout from '../../components/layout/DashboardLayout';
import { api } from '../../services/api';
import { networkPolicyApi } from '../../services/networkPolicyApi';
import type { IpAllowlistEntry, GeoRestriction, GeoFence, BlockedIpLog, IpCheckResult } from '../../services/networkPolicyApi';

interface Tenant {
  id: string;
  name: string;
  slug: string;
}

type TabType = 'allowlist' | 'geo' | 'geofence' | 'blocked' | 'check';

export default function NetworkPolicyPage() {
  const [tenants, setTenants] = useState<Tenant[]>([]);
  const [selectedTenantId, setSelectedTenantId] = useState<string>('');
  const [activeTab, setActiveTab] = useState<TabType>('allowlist');
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');
  const [success, setSuccess] = useState('');

  // IP Allowlist state
  const [allowlistEntries, setAllowlistEntries] = useState<IpAllowlistEntry[]>([]);
  const [showAddIp, setShowAddIp] = useState(false);
  const [newCidr, setNewCidr] = useState('');
  const [newIpDescription, setNewIpDescription] = useState('');

  // Geo Restriction state
  const [geoRestriction, setGeoRestriction] = useState<GeoRestriction | null>(null);
  const [geoMode, setGeoMode] = useState<'allow' | 'block'>('allow');
  const [geoCountries, setGeoCountries] = useState('');
  const [geoActive, setGeoActive] = useState(true);

  // GeoFence state
  const [geoFences, setGeoFences] = useState<GeoFence[]>([]);
  const [showAddFence, setShowAddFence] = useState(false);
  const [newFenceName, setNewFenceName] = useState('');
  const [newFenceLat, setNewFenceLat] = useState('');
  const [newFenceLon, setNewFenceLon] = useState('');
  const [newFenceRadius, setNewFenceRadius] = useState('');
  const [newFenceDescription, setNewFenceDescription] = useState('');

  // Blocked log state
  const [blockedLogs, setBlockedLogs] = useState<BlockedIpLog[]>([]);

  // IP Check state
  const [checkIp, setCheckIp] = useState('');
  const [checkLat, setCheckLat] = useState('');
  const [checkLon, setCheckLon] = useState('');
  const [checkResult, setCheckResult] = useState<IpCheckResult | null>(null);

  useEffect(() => {
    loadTenants();
  }, []);

  useEffect(() => {
    if (selectedTenantId) {
      loadData();
    }
  }, [selectedTenantId, activeTab]);

  const loadTenants = async () => {
    try {
      const data = await api.getTenants();
      setTenants(data);
      if (data.length > 0 && !selectedTenantId) {
        setSelectedTenantId(data[0].id);
      }
    } catch (err: any) {
      setError(err.response?.data?.error || 'Failed to load tenants');
    }
  };

  const loadData = async () => {
    setLoading(true);
    setError('');
    try {
      switch (activeTab) {
        case 'allowlist':
          setAllowlistEntries(await networkPolicyApi.getIpAllowlist(selectedTenantId));
          break;
        case 'geo': {
          const restriction = await networkPolicyApi.getGeoRestriction(selectedTenantId);
          setGeoRestriction(restriction);
          if (restriction) {
            if (restriction.allowedCountries) {
              setGeoMode('allow');
              try { setGeoCountries(JSON.parse(restriction.allowedCountries).join(', ')); } catch { setGeoCountries(''); }
            } else if (restriction.blockedCountries) {
              setGeoMode('block');
              try { setGeoCountries(JSON.parse(restriction.blockedCountries).join(', ')); } catch { setGeoCountries(''); }
            } else {
              setGeoCountries('');
            }
            setGeoActive(restriction.isActive);
          } else {
            setGeoCountries('');
            setGeoActive(true);
          }
          break;
        }
        case 'geofence':
          setGeoFences(await networkPolicyApi.getGeoFences(selectedTenantId));
          break;
        case 'blocked':
          setBlockedLogs(await networkPolicyApi.getBlockedLogs(selectedTenantId));
          break;
      }
    } catch (err: any) {
      setError(err.response?.data?.error || 'Failed to load data');
    } finally {
      setLoading(false);
    }
  };

  const handleAddIp = async () => {
    if (!newCidr.trim()) return;
    setError('');
    try {
      await networkPolicyApi.createIpAllowlistEntry({
        tenantId: selectedTenantId,
        cidr: newCidr.trim(),
        description: newIpDescription.trim() || undefined,
      });
      setNewCidr('');
      setNewIpDescription('');
      setShowAddIp(false);
      setSuccess('IP allowlist entry created');
      loadData();
    } catch (err: any) {
      setError(err.response?.data?.error || 'Failed to create entry');
    }
  };

  const handleToggleIp = async (entry: IpAllowlistEntry) => {
    try {
      await networkPolicyApi.updateIpAllowlistEntry(entry.id, {
        cidr: entry.cidr,
        description: entry.description,
        isActive: !entry.isActive,
      });
      loadData();
    } catch (err: any) {
      setError(err.response?.data?.error || 'Failed to update entry');
    }
  };

  const handleDeleteIp = async (id: string) => {
    if (!confirm('Delete this IP allowlist entry?')) return;
    try {
      await networkPolicyApi.deleteIpAllowlistEntry(id);
      setSuccess('Entry deleted');
      loadData();
    } catch (err: any) {
      setError(err.response?.data?.error || 'Failed to delete entry');
    }
  };

  const handleSaveGeoRestriction = async () => {
    setError('');
    try {
      const codes = geoCountries.split(',').map(c => c.trim().toUpperCase()).filter(c => c.length > 0);
      const json = codes.length > 0 ? JSON.stringify(codes) : undefined;

      await networkPolicyApi.upsertGeoRestriction({
        tenantId: selectedTenantId,
        allowedCountries: geoMode === 'allow' ? json : undefined,
        blockedCountries: geoMode === 'block' ? json : undefined,
        isActive: geoActive,
      });
      setSuccess('Geo restriction saved');
      loadData();
    } catch (err: any) {
      setError(err.response?.data?.error || 'Failed to save geo restriction');
    }
  };

  const handleDeleteGeoRestriction = async () => {
    if (!confirm('Delete geo restriction for this tenant?')) return;
    try {
      await networkPolicyApi.deleteGeoRestriction(selectedTenantId);
      setGeoRestriction(null);
      setGeoCountries('');
      setSuccess('Geo restriction deleted');
    } catch (err: any) {
      setError(err.response?.data?.error || 'Failed to delete geo restriction');
    }
  };

  const handleAddFence = async () => {
    if (!newFenceName.trim() || !newFenceLat || !newFenceLon || !newFenceRadius) return;
    setError('');
    try {
      await networkPolicyApi.createGeoFence({
        tenantId: selectedTenantId,
        name: newFenceName.trim(),
        latitude: parseFloat(newFenceLat),
        longitude: parseFloat(newFenceLon),
        radiusMeters: parseFloat(newFenceRadius),
        description: newFenceDescription.trim() || undefined,
      });
      setNewFenceName('');
      setNewFenceLat('');
      setNewFenceLon('');
      setNewFenceRadius('');
      setNewFenceDescription('');
      setShowAddFence(false);
      setSuccess('Geofence created');
      loadData();
    } catch (err: any) {
      setError(err.response?.data?.error || 'Failed to create geofence');
    }
  };

  const handleToggleFence = async (fence: GeoFence) => {
    try {
      await networkPolicyApi.updateGeoFence(fence.id, {
        name: fence.name,
        latitude: fence.latitude,
        longitude: fence.longitude,
        radiusMeters: fence.radiusMeters,
        description: fence.description,
        isActive: !fence.isActive,
      });
      loadData();
    } catch (err: any) {
      setError(err.response?.data?.error || 'Failed to update geofence');
    }
  };

  const handleDeleteFence = async (id: string) => {
    if (!confirm('Delete this geofence?')) return;
    try {
      await networkPolicyApi.deleteGeoFence(id);
      setSuccess('Geofence deleted');
      loadData();
    } catch (err: any) {
      setError(err.response?.data?.error || 'Failed to delete geofence');
    }
  };

  const handleCheckIp = async () => {
    if (!checkIp.trim()) return;
    setError('');
    setCheckResult(null);
    try {
      const result = await networkPolicyApi.checkIp({
        tenantId: selectedTenantId,
        ipAddress: checkIp.trim(),
        latitude: checkLat ? parseFloat(checkLat) : undefined,
        longitude: checkLon ? parseFloat(checkLon) : undefined,
      });
      setCheckResult(result);
    } catch (err: any) {
      setError(err.response?.data?.error || 'Failed to check IP');
    }
  };

  const tabClass = (tab: TabType) =>
    `px-4 py-2 text-sm font-medium rounded-t-lg cursor-pointer ${
      activeTab === tab
        ? 'bg-white text-indigo-600 border-b-2 border-indigo-600'
        : 'text-gray-500 hover:text-gray-700 hover:bg-gray-50'
    }`;

  return (
    <DashboardLayout>
      <div className="space-y-6">
        <div className="flex justify-between items-center">
          <h1 className="text-2xl font-bold text-gray-900">Network Policy</h1>
          <select
            value={selectedTenantId}
            onChange={(e) => setSelectedTenantId(e.target.value)}
            className="rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 text-sm"
          >
            {tenants.map((t) => (
              <option key={t.id} value={t.id}>{t.name}</option>
            ))}
          </select>
        </div>

        {error && (
          <div className="bg-red-50 border border-red-200 text-red-700 px-4 py-3 rounded-md text-sm">
            {error}
          </div>
        )}
        {success && (
          <div className="bg-green-50 border border-green-200 text-green-700 px-4 py-3 rounded-md text-sm">
            {success}
            <button onClick={() => setSuccess('')} className="ml-2 text-green-900 font-bold">x</button>
          </div>
        )}

        {/* Tabs */}
        <div className="flex space-x-1 border-b border-gray-200">
          <button className={tabClass('allowlist')} onClick={() => setActiveTab('allowlist')}>IP Allowlist</button>
          <button className={tabClass('geo')} onClick={() => setActiveTab('geo')}>Geo Restrictions</button>
          <button className={tabClass('geofence')} onClick={() => setActiveTab('geofence')}>Geofences</button>
          <button className={tabClass('blocked')} onClick={() => setActiveTab('blocked')}>Blocked Log</button>
          <button className={tabClass('check')} onClick={() => setActiveTab('check')}>Check IP</button>
        </div>

        {loading ? (
          <div className="text-center py-8 text-gray-500">Loading...</div>
        ) : (
          <div className="bg-white shadow rounded-lg p-6">
            {/* IP Allowlist Tab */}
            {activeTab === 'allowlist' && (
              <div>
                <div className="flex justify-between items-center mb-4">
                  <h2 className="text-lg font-semibold text-gray-900">IP Allowlist</h2>
                  <button
                    onClick={() => setShowAddIp(!showAddIp)}
                    className="inline-flex items-center px-3 py-2 border border-transparent text-sm font-medium rounded-md text-white bg-indigo-600 hover:bg-indigo-700"
                  >
                    {showAddIp ? 'Cancel' : 'Add IP/CIDR'}
                  </button>
                </div>

                <p className="text-sm text-gray-500 mb-4">
                  When entries exist, only IPs matching at least one active CIDR range will be allowed.
                  Use CIDR notation (e.g., 192.168.1.0/24 for a range or 10.0.0.1/32 for a single IP).
                </p>

                {showAddIp && (
                  <div className="mb-4 p-4 bg-gray-50 rounded-lg space-y-3">
                    <div>
                      <label className="block text-sm font-medium text-gray-700">CIDR</label>
                      <input
                        type="text"
                        value={newCidr}
                        onChange={(e) => setNewCidr(e.target.value)}
                        placeholder="e.g., 192.168.1.0/24 or 10.0.0.1"
                        className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 text-sm"
                      />
                    </div>
                    <div>
                      <label className="block text-sm font-medium text-gray-700">Description</label>
                      <input
                        type="text"
                        value={newIpDescription}
                        onChange={(e) => setNewIpDescription(e.target.value)}
                        placeholder="e.g., Office network"
                        className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 text-sm"
                      />
                    </div>
                    <button
                      onClick={handleAddIp}
                      className="inline-flex items-center px-3 py-2 border border-transparent text-sm font-medium rounded-md text-white bg-green-600 hover:bg-green-700"
                    >
                      Save
                    </button>
                  </div>
                )}

                {allowlistEntries.length === 0 ? (
                  <p className="text-gray-500 text-sm py-4">No IP allowlist entries. All IPs are allowed (unless restricted by other policies).</p>
                ) : (
                  <table className="min-w-full divide-y divide-gray-200">
                    <thead className="bg-gray-50">
                      <tr>
                        <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase">CIDR</th>
                        <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase">Description</th>
                        <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase">Status</th>
                        <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase">Created</th>
                        <th className="px-6 py-3 text-right text-xs font-medium text-gray-500 uppercase">Actions</th>
                      </tr>
                    </thead>
                    <tbody className="bg-white divide-y divide-gray-200">
                      {allowlistEntries.map((entry) => (
                        <tr key={entry.id}>
                          <td className="px-6 py-4 whitespace-nowrap text-sm font-mono text-gray-900">{entry.cidr}</td>
                          <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-500">{entry.description || '-'}</td>
                          <td className="px-6 py-4 whitespace-nowrap">
                            <span className={`inline-flex items-center px-2.5 py-0.5 rounded-full text-xs font-medium ${entry.isActive ? 'bg-green-100 text-green-800' : 'bg-gray-100 text-gray-800'}`}>
                              {entry.isActive ? 'Active' : 'Disabled'}
                            </span>
                          </td>
                          <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-500">{new Date(entry.createdAt).toLocaleDateString()}</td>
                          <td className="px-6 py-4 whitespace-nowrap text-right text-sm font-medium space-x-2">
                            <button onClick={() => handleToggleIp(entry)} className="text-indigo-600 hover:text-indigo-900">
                              {entry.isActive ? 'Disable' : 'Enable'}
                            </button>
                            <button onClick={() => handleDeleteIp(entry.id)} className="text-red-600 hover:text-red-900">
                              Delete
                            </button>
                          </td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                )}
              </div>
            )}

            {/* Geo Restrictions Tab */}
            {activeTab === 'geo' && (
              <div>
                <h2 className="text-lg font-semibold text-gray-900 mb-4">Country-Level Geo Restrictions</h2>
                <p className="text-sm text-gray-500 mb-4">
                  Restrict access based on the country of the requesting IP address. Use ISO 3166-1 alpha-2 country codes (e.g., NL, DE, US, GB).
                </p>

                <div className="space-y-4">
                  <div>
                    <label className="block text-sm font-medium text-gray-700 mb-2">Mode</label>
                    <div className="flex space-x-4">
                      <label className="inline-flex items-center">
                        <input
                          type="radio"
                          value="allow"
                          checked={geoMode === 'allow'}
                          onChange={() => setGeoMode('allow')}
                          className="form-radio h-4 w-4 text-indigo-600"
                        />
                        <span className="ml-2 text-sm text-gray-700">Allow only these countries</span>
                      </label>
                      <label className="inline-flex items-center">
                        <input
                          type="radio"
                          value="block"
                          checked={geoMode === 'block'}
                          onChange={() => setGeoMode('block')}
                          className="form-radio h-4 w-4 text-indigo-600"
                        />
                        <span className="ml-2 text-sm text-gray-700">Block these countries (allow all others)</span>
                      </label>
                    </div>
                  </div>

                  <div>
                    <label className="block text-sm font-medium text-gray-700">Country Codes (comma-separated)</label>
                    <input
                      type="text"
                      value={geoCountries}
                      onChange={(e) => setGeoCountries(e.target.value)}
                      placeholder="NL, DE, US, GB"
                      className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 text-sm"
                    />
                  </div>

                  <div>
                    <label className="inline-flex items-center">
                      <input
                        type="checkbox"
                        checked={geoActive}
                        onChange={(e) => setGeoActive(e.target.checked)}
                        className="form-checkbox h-4 w-4 text-indigo-600"
                      />
                      <span className="ml-2 text-sm text-gray-700">Active</span>
                    </label>
                  </div>

                  <div className="flex space-x-3">
                    <button
                      onClick={handleSaveGeoRestriction}
                      className="inline-flex items-center px-4 py-2 border border-transparent text-sm font-medium rounded-md text-white bg-indigo-600 hover:bg-indigo-700"
                    >
                      Save Geo Restriction
                    </button>
                    {geoRestriction && (
                      <button
                        onClick={handleDeleteGeoRestriction}
                        className="inline-flex items-center px-4 py-2 border border-red-300 text-sm font-medium rounded-md text-red-700 bg-white hover:bg-red-50"
                      >
                        Remove Restriction
                      </button>
                    )}
                  </div>
                </div>
              </div>
            )}

            {/* Geofences Tab */}
            {activeTab === 'geofence' && (
              <div>
                <div className="flex justify-between items-center mb-4">
                  <h2 className="text-lg font-semibold text-gray-900">Geofences</h2>
                  <button
                    onClick={() => setShowAddFence(!showAddFence)}
                    className="inline-flex items-center px-3 py-2 border border-transparent text-sm font-medium rounded-md text-white bg-indigo-600 hover:bg-indigo-700"
                  >
                    {showAddFence ? 'Cancel' : 'Add Geofence'}
                  </button>
                </div>

                <p className="text-sm text-gray-500 mb-4">
                  Define circular geographic zones. When geofences are configured, access is only allowed from within at least one active fence.
                  Requires client to send latitude/longitude with the request.
                </p>

                {showAddFence && (
                  <div className="mb-4 p-4 bg-gray-50 rounded-lg space-y-3">
                    <div className="grid grid-cols-2 gap-3">
                      <div>
                        <label className="block text-sm font-medium text-gray-700">Name</label>
                        <input
                          type="text"
                          value={newFenceName}
                          onChange={(e) => setNewFenceName(e.target.value)}
                          placeholder="e.g., Amsterdam Office"
                          className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 text-sm"
                        />
                      </div>
                      <div>
                        <label className="block text-sm font-medium text-gray-700">Radius (meters)</label>
                        <input
                          type="number"
                          value={newFenceRadius}
                          onChange={(e) => setNewFenceRadius(e.target.value)}
                          placeholder="e.g., 500"
                          className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 text-sm"
                        />
                      </div>
                      <div>
                        <label className="block text-sm font-medium text-gray-700">Latitude</label>
                        <input
                          type="number"
                          step="any"
                          value={newFenceLat}
                          onChange={(e) => setNewFenceLat(e.target.value)}
                          placeholder="e.g., 52.3676"
                          className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 text-sm"
                        />
                      </div>
                      <div>
                        <label className="block text-sm font-medium text-gray-700">Longitude</label>
                        <input
                          type="number"
                          step="any"
                          value={newFenceLon}
                          onChange={(e) => setNewFenceLon(e.target.value)}
                          placeholder="e.g., 4.9041"
                          className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 text-sm"
                        />
                      </div>
                    </div>
                    <div>
                      <label className="block text-sm font-medium text-gray-700">Description</label>
                      <input
                        type="text"
                        value={newFenceDescription}
                        onChange={(e) => setNewFenceDescription(e.target.value)}
                        placeholder="Optional description"
                        className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 text-sm"
                      />
                    </div>
                    <button
                      onClick={handleAddFence}
                      className="inline-flex items-center px-3 py-2 border border-transparent text-sm font-medium rounded-md text-white bg-green-600 hover:bg-green-700"
                    >
                      Save Geofence
                    </button>
                  </div>
                )}

                {geoFences.length === 0 ? (
                  <p className="text-gray-500 text-sm py-4">No geofences configured. Location-based restrictions are not active.</p>
                ) : (
                  <table className="min-w-full divide-y divide-gray-200">
                    <thead className="bg-gray-50">
                      <tr>
                        <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase">Name</th>
                        <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase">Center</th>
                        <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase">Radius</th>
                        <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase">Status</th>
                        <th className="px-6 py-3 text-right text-xs font-medium text-gray-500 uppercase">Actions</th>
                      </tr>
                    </thead>
                    <tbody className="bg-white divide-y divide-gray-200">
                      {geoFences.map((fence) => (
                        <tr key={fence.id}>
                          <td className="px-6 py-4 whitespace-nowrap text-sm font-medium text-gray-900">
                            {fence.name}
                            {fence.description && <span className="block text-xs text-gray-400">{fence.description}</span>}
                          </td>
                          <td className="px-6 py-4 whitespace-nowrap text-sm font-mono text-gray-500">
                            {fence.latitude.toFixed(4)}, {fence.longitude.toFixed(4)}
                          </td>
                          <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-500">
                            {fence.radiusMeters >= 1000 ? `${(fence.radiusMeters / 1000).toFixed(1)} km` : `${fence.radiusMeters} m`}
                          </td>
                          <td className="px-6 py-4 whitespace-nowrap">
                            <span className={`inline-flex items-center px-2.5 py-0.5 rounded-full text-xs font-medium ${fence.isActive ? 'bg-green-100 text-green-800' : 'bg-gray-100 text-gray-800'}`}>
                              {fence.isActive ? 'Active' : 'Disabled'}
                            </span>
                          </td>
                          <td className="px-6 py-4 whitespace-nowrap text-right text-sm font-medium space-x-2">
                            <button onClick={() => handleToggleFence(fence)} className="text-indigo-600 hover:text-indigo-900">
                              {fence.isActive ? 'Disable' : 'Enable'}
                            </button>
                            <button onClick={() => handleDeleteFence(fence.id)} className="text-red-600 hover:text-red-900">
                              Delete
                            </button>
                          </td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                )}
              </div>
            )}

            {/* Blocked Log Tab */}
            {activeTab === 'blocked' && (
              <div>
                <div className="flex justify-between items-center mb-4">
                  <h2 className="text-lg font-semibold text-gray-900">Blocked IP Log</h2>
                  <button
                    onClick={loadData}
                    className="inline-flex items-center px-3 py-2 border border-gray-300 text-sm font-medium rounded-md text-gray-700 bg-white hover:bg-gray-50"
                  >
                    Refresh
                  </button>
                </div>

                {blockedLogs.length === 0 ? (
                  <p className="text-gray-500 text-sm py-4">No blocked IP attempts recorded.</p>
                ) : (
                  <table className="min-w-full divide-y divide-gray-200">
                    <thead className="bg-gray-50">
                      <tr>
                        <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase">IP Address</th>
                        <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase">Country</th>
                        <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase">City</th>
                        <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase">Reason</th>
                        <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase">Blocked At</th>
                      </tr>
                    </thead>
                    <tbody className="bg-white divide-y divide-gray-200">
                      {blockedLogs.map((log) => (
                        <tr key={log.id}>
                          <td className="px-6 py-4 whitespace-nowrap text-sm font-mono text-gray-900">{log.ipAddress}</td>
                          <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-500">{log.country || '-'}</td>
                          <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-500">{log.city || '-'}</td>
                          <td className="px-6 py-4 text-sm text-gray-500 max-w-md truncate">{log.reason}</td>
                          <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-500">{new Date(log.blockedAt).toLocaleString()}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                )}
              </div>
            )}

            {/* Check IP Tab */}
            {activeTab === 'check' && (
              <div>
                <h2 className="text-lg font-semibold text-gray-900 mb-4">Test IP Against Policies</h2>
                <p className="text-sm text-gray-500 mb-4">
                  Test whether an IP address (and optionally a location) would be allowed by the current network policies.
                </p>

                <div className="space-y-4 max-w-lg">
                  <div>
                    <label className="block text-sm font-medium text-gray-700">IP Address</label>
                    <input
                      type="text"
                      value={checkIp}
                      onChange={(e) => setCheckIp(e.target.value)}
                      placeholder="e.g., 203.0.113.42"
                      className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 text-sm"
                    />
                  </div>
                  <div className="grid grid-cols-2 gap-3">
                    <div>
                      <label className="block text-sm font-medium text-gray-700">Latitude (optional)</label>
                      <input
                        type="number"
                        step="any"
                        value={checkLat}
                        onChange={(e) => setCheckLat(e.target.value)}
                        placeholder="e.g., 52.3676"
                        className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 text-sm"
                      />
                    </div>
                    <div>
                      <label className="block text-sm font-medium text-gray-700">Longitude (optional)</label>
                      <input
                        type="number"
                        step="any"
                        value={checkLon}
                        onChange={(e) => setCheckLon(e.target.value)}
                        placeholder="e.g., 4.9041"
                        className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 text-sm"
                      />
                    </div>
                  </div>
                  <button
                    onClick={handleCheckIp}
                    className="inline-flex items-center px-4 py-2 border border-transparent text-sm font-medium rounded-md text-white bg-indigo-600 hover:bg-indigo-700"
                  >
                    Check IP
                  </button>
                </div>

                {checkResult && (
                  <div className={`mt-6 p-4 rounded-lg ${checkResult.isAllowed ? 'bg-green-50 border border-green-200' : 'bg-red-50 border border-red-200'}`}>
                    <h3 className={`text-lg font-semibold ${checkResult.isAllowed ? 'text-green-800' : 'text-red-800'}`}>
                      {checkResult.isAllowed ? 'ALLOWED' : 'BLOCKED'}
                    </h3>
                    <div className="mt-2 space-y-1 text-sm">
                      <p><span className="font-medium">IP:</span> {checkResult.ipAddress}</p>
                      {checkResult.country && <p><span className="font-medium">Country:</span> {checkResult.country}</p>}
                      {checkResult.city && <p><span className="font-medium">City:</span> {checkResult.city}</p>}
                      <div className="flex space-x-4 mt-2">
                        <span className={`inline-flex items-center px-2 py-1 rounded text-xs font-medium ${checkResult.passedIpAllowlist ? 'bg-green-100 text-green-700' : 'bg-red-100 text-red-700'}`}>
                          IP Allowlist: {checkResult.passedIpAllowlist ? 'Pass' : 'Fail'}
                        </span>
                        <span className={`inline-flex items-center px-2 py-1 rounded text-xs font-medium ${checkResult.passedGeoRestriction ? 'bg-green-100 text-green-700' : 'bg-red-100 text-red-700'}`}>
                          Geo Restriction: {checkResult.passedGeoRestriction ? 'Pass' : 'Fail'}
                        </span>
                        <span className={`inline-flex items-center px-2 py-1 rounded text-xs font-medium ${checkResult.passedGeoFence ? 'bg-green-100 text-green-700' : 'bg-red-100 text-red-700'}`}>
                          Geofence: {checkResult.passedGeoFence ? 'Pass' : 'Fail'}
                        </span>
                      </div>
                      {checkResult.reasons.length > 0 && (
                        <div className="mt-2">
                          <p className="font-medium text-red-700">Reasons:</p>
                          <ul className="list-disc list-inside text-red-600">
                            {checkResult.reasons.map((r, i) => <li key={i}>{r}</li>)}
                          </ul>
                        </div>
                      )}
                    </div>
                  </div>
                )}
              </div>
            )}
          </div>
        )}
      </div>
    </DashboardLayout>
  );
}
