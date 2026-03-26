import { useState, useEffect } from 'react';
import { Link } from 'react-router-dom';
import DashboardLayout from '../../components/layout/DashboardLayout';
import { deviceApi } from '../../services/deviceApi';
import type { Device, DeviceStatistics } from '../../types/devices';

export default function DevicesPage() {
  const [devices, setDevices] = useState<Device[]>([]);
  const [statistics, setStatistics] = useState<DeviceStatistics | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [searchTerm, setSearchTerm] = useState('');
  const [filterType, setFilterType] = useState<string>('all');
  const [filterStatus, setFilterStatus] = useState<'all' | 'active' | 'inactive'>('all');
  const [filterOnline, setFilterOnline] = useState<'all' | 'online' | 'offline'>('all');

  useEffect(() => {
    loadData();
  }, []);

  const loadData = async () => {
    try {
      setLoading(true);
      const [devicesData, statsData] = await Promise.all([
        deviceApi.getDevices(),
        deviceApi.getStatistics(),
      ]);
      setDevices(devicesData);
      setStatistics(statsData);
    } catch (err: any) {
      setError(err.response?.data?.message || 'Failed to load devices');
    } finally {
      setLoading(false);
    }
  };

  const handleDeactivate = async (deviceId: string) => {
    if (!confirm('Are you sure you want to deactivate this device?')) return;
    try {
      await deviceApi.deactivateDevice(deviceId);
      await loadData();
    } catch (err: any) {
      alert(err.response?.data?.message || 'Failed to deactivate device');
    }
  };

  // Collect unique device types for the filter dropdown
  const deviceTypes = Array.from(new Set(devices.map((d) => d.deviceType))).sort();

  // Filter devices based on search and filters
  const filteredDevices = devices.filter((device) => {
    const matchesSearch =
      device.name.toLowerCase().includes(searchTerm.toLowerCase()) ||
      device.deviceId.toLowerCase().includes(searchTerm.toLowerCase());

    const matchesType = filterType === 'all' || device.deviceType === filterType;

    const matchesStatus =
      filterStatus === 'all' ||
      (filterStatus === 'active' && device.isActive) ||
      (filterStatus === 'inactive' && !device.isActive);

    const matchesOnline =
      filterOnline === 'all' ||
      (filterOnline === 'online' && device.isOnline) ||
      (filterOnline === 'offline' && !device.isOnline);

    return matchesSearch && matchesType && matchesStatus && matchesOnline;
  });

  const formatDate = (dateStr?: string) => {
    if (!dateStr) return 'Never';
    return new Date(dateStr).toLocaleString();
  };

  return (
    <DashboardLayout>
      <div className="px-4 sm:px-6 lg:px-8">
        {/* Header */}
        <div className="sm:flex sm:items-center">
          <div className="sm:flex-auto">
            <h1 className="text-2xl font-semibold text-gray-900">Devices</h1>
            <p className="mt-2 text-sm text-gray-700">
              Manage IoT devices, their authentication, and monitor their status.
            </p>
          </div>
          <div className="mt-4 sm:mt-0 sm:ml-16 sm:flex-none">
            <Link
              to="/devices/register"
              className="inline-flex items-center justify-center rounded-md border border-transparent bg-indigo-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-indigo-700 focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:ring-offset-2"
            >
              Register Device
            </Link>
          </div>
        </div>

        {/* Statistics Cards */}
        {statistics && (
          <div className="mt-6 grid grid-cols-1 gap-5 sm:grid-cols-2 lg:grid-cols-4">
            <div className="overflow-hidden rounded-lg bg-white px-4 py-5 shadow sm:p-6">
              <dt className="truncate text-sm font-medium text-gray-500">Total Devices</dt>
              <dd className="mt-1 text-3xl font-semibold tracking-tight text-gray-900">
                {statistics.totalDevices}
              </dd>
            </div>
            <div className="overflow-hidden rounded-lg bg-white px-4 py-5 shadow sm:p-6">
              <dt className="truncate text-sm font-medium text-gray-500">Active Devices</dt>
              <dd className="mt-1 text-3xl font-semibold tracking-tight text-green-600">
                {statistics.activeDevices}
              </dd>
            </div>
            <div className="overflow-hidden rounded-lg bg-white px-4 py-5 shadow sm:p-6">
              <dt className="truncate text-sm font-medium text-gray-500">Online Now</dt>
              <dd className="mt-1 text-3xl font-semibold tracking-tight text-blue-600">
                {statistics.onlineDevices}
              </dd>
            </div>
            <div className="overflow-hidden rounded-lg bg-white px-4 py-5 shadow sm:p-6">
              <dt className="truncate text-sm font-medium text-gray-500">Provisioned</dt>
              <dd className="mt-1 text-3xl font-semibold tracking-tight text-purple-600">
                {statistics.provisionedDevices}
              </dd>
            </div>
          </div>
        )}

        {/* Filters */}
        <div className="mt-6 flex flex-col sm:flex-row gap-4">
          <div className="flex-1">
            <input
              type="text"
              placeholder="Search by name or device ID..."
              value={searchTerm}
              onChange={(e) => setSearchTerm(e.target.value)}
              className="block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
            />
          </div>
          <div>
            <select
              value={filterType}
              onChange={(e) => setFilterType(e.target.value)}
              className="block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
            >
              <option value="all">All Types</option>
              {deviceTypes.map((type) => (
                <option key={type} value={type}>
                  {type.charAt(0).toUpperCase() + type.slice(1)}
                </option>
              ))}
            </select>
          </div>
          <div className="flex gap-2">
            <button
              onClick={() => setFilterStatus('all')}
              className={`px-4 py-2 text-sm font-medium rounded-md ${
                filterStatus === 'all'
                  ? 'bg-indigo-600 text-white'
                  : 'bg-white text-gray-700 border border-gray-300 hover:bg-gray-50'
              }`}
            >
              All
            </button>
            <button
              onClick={() => setFilterStatus('active')}
              className={`px-4 py-2 text-sm font-medium rounded-md ${
                filterStatus === 'active'
                  ? 'bg-indigo-600 text-white'
                  : 'bg-white text-gray-700 border border-gray-300 hover:bg-gray-50'
              }`}
            >
              Active
            </button>
            <button
              onClick={() => setFilterStatus('inactive')}
              className={`px-4 py-2 text-sm font-medium rounded-md ${
                filterStatus === 'inactive'
                  ? 'bg-indigo-600 text-white'
                  : 'bg-white text-gray-700 border border-gray-300 hover:bg-gray-50'
              }`}
            >
              Inactive
            </button>
          </div>
          <div className="flex gap-2">
            <button
              onClick={() => setFilterOnline('all')}
              className={`px-4 py-2 text-sm font-medium rounded-md ${
                filterOnline === 'all'
                  ? 'bg-indigo-600 text-white'
                  : 'bg-white text-gray-700 border border-gray-300 hover:bg-gray-50'
              }`}
            >
              All
            </button>
            <button
              onClick={() => setFilterOnline('online')}
              className={`px-4 py-2 text-sm font-medium rounded-md ${
                filterOnline === 'online'
                  ? 'bg-green-600 text-white'
                  : 'bg-white text-gray-700 border border-gray-300 hover:bg-gray-50'
              }`}
            >
              Online
            </button>
            <button
              onClick={() => setFilterOnline('offline')}
              className={`px-4 py-2 text-sm font-medium rounded-md ${
                filterOnline === 'offline'
                  ? 'bg-gray-600 text-white'
                  : 'bg-white text-gray-700 border border-gray-300 hover:bg-gray-50'
              }`}
            >
              Offline
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
            <p className="mt-2 text-sm text-gray-500">Loading devices...</p>
          </div>
        ) : (
          /* Devices Table */
          <div className="mt-8 flex flex-col">
            <div className="-my-2 -mx-4 overflow-x-auto sm:-mx-6 lg:-mx-8">
              <div className="inline-block min-w-full py-2 align-middle md:px-6 lg:px-8">
                <div className="overflow-hidden shadow ring-1 ring-black ring-opacity-5 md:rounded-lg">
                  <table className="min-w-full divide-y divide-gray-300">
                    <thead className="bg-gray-50">
                      <tr>
                        <th className="px-3 py-3.5 text-left text-sm font-semibold text-gray-900">
                          Device ID
                        </th>
                        <th className="px-3 py-3.5 text-left text-sm font-semibold text-gray-900">
                          Name
                        </th>
                        <th className="px-3 py-3.5 text-left text-sm font-semibold text-gray-900">
                          Type
                        </th>
                        <th className="px-3 py-3.5 text-left text-sm font-semibold text-gray-900">
                          Auth Method
                        </th>
                        <th className="px-3 py-3.5 text-left text-sm font-semibold text-gray-900">
                          Status
                        </th>
                        <th className="px-3 py-3.5 text-left text-sm font-semibold text-gray-900">
                          Last Seen
                        </th>
                        <th className="relative py-3.5 pl-3 pr-4 sm:pr-6">
                          <span className="sr-only">Actions</span>
                        </th>
                      </tr>
                    </thead>
                    <tbody className="divide-y divide-gray-200 bg-white">
                      {filteredDevices.length === 0 ? (
                        <tr>
                          <td colSpan={7} className="px-3 py-4 text-sm text-gray-500 text-center">
                            No devices found
                          </td>
                        </tr>
                      ) : (
                        filteredDevices.map((device) => (
                          <tr key={device.id}>
                            <td className="whitespace-nowrap px-3 py-4 text-sm">
                              <div className="font-mono text-gray-900">{device.deviceId}</div>
                            </td>
                            <td className="whitespace-nowrap px-3 py-4 text-sm">
                              <div className="font-medium text-gray-900">{device.name}</div>
                            </td>
                            <td className="whitespace-nowrap px-3 py-4 text-sm">
                              <span className="inline-flex rounded-full bg-indigo-100 px-2 text-xs font-semibold leading-5 text-indigo-800">
                                {device.deviceType}
                              </span>
                            </td>
                            <td className="whitespace-nowrap px-3 py-4 text-sm text-gray-500">
                              {device.authenticationMethod}
                            </td>
                            <td className="whitespace-nowrap px-3 py-4 text-sm">
                              <div className="flex items-center gap-2">
                                {/* Online/Offline indicator */}
                                <span
                                  className={`inline-block h-2.5 w-2.5 rounded-full ${
                                    device.isOnline ? 'bg-green-500' : 'bg-gray-400'
                                  }`}
                                  title={device.isOnline ? 'Online' : 'Offline'}
                                />
                                <span
                                  className={`inline-flex rounded-full px-2 text-xs font-semibold leading-5 ${
                                    device.isActive
                                      ? 'bg-green-100 text-green-800'
                                      : 'bg-red-100 text-red-800'
                                  }`}
                                >
                                  {device.isActive ? 'Active' : 'Inactive'}
                                </span>
                              </div>
                            </td>
                            <td className="whitespace-nowrap px-3 py-4 text-sm text-gray-500">
                              {formatDate(device.lastSeenAt)}
                            </td>
                            <td className="relative whitespace-nowrap py-4 pl-3 pr-4 text-right text-sm font-medium sm:pr-6">
                              <Link
                                to={`/devices/${device.id}`}
                                className="text-indigo-600 hover:text-indigo-900 mr-4"
                              >
                                View
                              </Link>
                              {device.isActive && (
                                <button
                                  onClick={() => handleDeactivate(device.id)}
                                  className="text-red-600 hover:text-red-900"
                                >
                                  Deactivate
                                </button>
                              )}
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
            Showing {filteredDevices.length} of {devices.length} devices
          </div>
        )}
      </div>
    </DashboardLayout>
  );
}
