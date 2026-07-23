import { useState, useEffect } from 'react';
import { useParams, useNavigate } from 'react-router-dom';
import DashboardLayout from '../../components/layout/DashboardLayout';
import { deviceApi } from '../../services/deviceApi';
import type { Device } from '../../types/devices';

type TabName = 'overview' | 'certificates' | 'permissions' | 'activity';

export default function DeviceDetailPage() {
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const [device, setDevice] = useState<Device | null>(null);
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');
  const [activeTab, setActiveTab] = useState<TabName>('overview');
  const [showDeactivateModal, setShowDeactivateModal] = useState(false);

  // Edit form state
  const [editName, setEditName] = useState('');
  const [editTags, setEditTags] = useState('');
  const [editMetadata, setEditMetadata] = useState('');
  const [isEditing, setIsEditing] = useState(false);

  useEffect(() => {
    if (id) {
      loadDevice();
    }
  }, [id]);

  const loadDevice = async () => {
    try {
      setLoading(true);
      const data = await deviceApi.getDevice(id!);
      setDevice(data);
      setEditName(data.name);
      setEditTags((data.tags || []).join(', '));
      setEditMetadata(JSON.stringify(data.metadata || {}, null, 2));
    } catch (err: any) {
      setError(err.response?.data?.message || 'Failed to load device');
    } finally {
      setLoading(false);
    }
  };

  const handleSave = async () => {
    if (!device) return;
    try {
      setSaving(true);
      setError('');
      let parsedMetadata: Record<string, any> = {};
      try {
        parsedMetadata = JSON.parse(editMetadata);
      } catch {
        setError('Invalid JSON in metadata field');
        setSaving(false);
        return;
      }
      await deviceApi.updateDevice(device.id, {
        name: editName,
        tags: editTags
          .split(',')
          .map((t) => t.trim())
          .filter(Boolean),
        metadata: parsedMetadata,
      });
      setIsEditing(false);
      await loadDevice();
    } catch (err: any) {
      setError(err.response?.data?.message || 'Failed to update device');
    } finally {
      setSaving(false);
    }
  };

  const handleDeactivate = async () => {
    if (!device) return;
    try {
      await deviceApi.deactivateDevice(device.id);
      setShowDeactivateModal(false);
      await loadDevice();
    } catch (err: any) {
      alert(err.response?.data?.message || 'Failed to deactivate device');
    }
  };

  const formatDate = (dateStr?: string) => {
    if (!dateStr) return 'N/A';
    return new Date(dateStr).toLocaleString();
  };

  const tabs: { name: TabName; label: string }[] = [
    { name: 'overview', label: 'Overview' },
    { name: 'certificates', label: 'Certificates' },
    { name: 'permissions', label: 'Permissions' },
    { name: 'activity', label: 'Activity' },
  ];

  if (loading) {
    return (
      <DashboardLayout>
        <div className="flex items-center justify-center min-h-screen">
          <div className="text-center">
            <div className="inline-block animate-spin rounded-full h-8 w-8 border-b-2 border-indigo-600"></div>
            <p className="mt-2 text-sm text-gray-500">Loading device...</p>
          </div>
        </div>
      </DashboardLayout>
    );
  }

  if (!device) {
    return (
      <DashboardLayout>
        <div className="text-center py-12">
          <h2 className="text-2xl font-bold text-gray-900">Device not found</h2>
          <button
            onClick={() => navigate('/devices')}
            className="mt-4 text-indigo-600 hover:text-indigo-500"
          >
            Back to devices
          </button>
        </div>
      </DashboardLayout>
    );
  }

  return (
    <DashboardLayout>
      <div className="max-w-5xl mx-auto px-4 sm:px-6 lg:px-8">
        {/* Header */}
        <div className="md:flex md:items-center md:justify-between">
          <div className="flex-1 min-w-0">
            <div className="flex items-center gap-3">
              <h2 className="text-2xl font-bold leading-7 text-gray-900 sm:text-3xl sm:truncate">
                {device.name}
              </h2>
              <span
                className={`inline-block h-3 w-3 rounded-full ${
                  device.isOnline ? 'bg-green-500' : 'bg-gray-400'
                }`}
                title={device.isOnline ? 'Online' : 'Offline'}
              />
            </div>
            <p className="mt-1 text-sm text-gray-500 font-mono">{device.deviceId}</p>
          </div>
          <div className="mt-4 flex gap-3 md:mt-0 md:ml-4">
            <button
              type="button"
              onClick={() => navigate('/devices')}
              className="inline-flex items-center px-4 py-2 border border-gray-300 rounded-md shadow-sm text-sm font-medium text-gray-700 bg-white hover:bg-gray-50 focus:outline-none focus:ring-2 focus:ring-offset-2 focus:ring-indigo-500"
            >
              Back
            </button>
            {device.isActive && (
              <button
                type="button"
                onClick={() => setShowDeactivateModal(true)}
                className="inline-flex items-center px-4 py-2 border border-transparent rounded-md shadow-sm text-sm font-medium text-white bg-red-600 hover:bg-red-700 focus:outline-none focus:ring-2 focus:ring-offset-2 focus:ring-red-500"
              >
                Deactivate
              </button>
            )}
          </div>
        </div>

        {/* Error Message */}
        {error && (
          <div className="mt-4 bg-red-50 border border-red-200 text-red-700 px-4 py-3 rounded">
            {error}
          </div>
        )}

        {/* Tabs */}
        <div className="mt-6 border-b border-gray-200">
          <nav className="-mb-px flex space-x-8">
            {tabs.map((tab) => (
              <button
                key={tab.name}
                onClick={() => setActiveTab(tab.name)}
                className={`whitespace-nowrap py-4 px-1 border-b-2 font-medium text-sm ${
                  activeTab === tab.name
                    ? 'border-indigo-500 text-indigo-600'
                    : 'border-transparent text-gray-500 hover:text-gray-700 hover:border-gray-300'
                }`}
              >
                {tab.label}
              </button>
            ))}
          </nav>
        </div>

        {/* Tab Content */}
        <div className="mt-6">
          {/* Overview Tab */}
          {activeTab === 'overview' && (
            <div className="space-y-6">
              {/* Device Properties Card */}
              <div className="bg-white shadow overflow-hidden sm:rounded-lg">
                <div className="px-4 py-5 sm:px-6 flex justify-between items-center">
                  <div>
                    <h3 className="text-lg leading-6 font-medium text-gray-900">
                      Device Information
                    </h3>
                    <p className="mt-1 max-w-2xl text-sm text-gray-500">
                      Core properties and configuration
                    </p>
                  </div>
                  {!isEditing && (
                    <button
                      onClick={() => setIsEditing(true)}
                      className="inline-flex items-center px-3 py-1.5 border border-gray-300 rounded-md shadow-sm text-sm font-medium text-gray-700 bg-white hover:bg-gray-50"
                    >
                      Edit
                    </button>
                  )}
                </div>
                <div className="border-t border-gray-200">
                  <dl>
                    <div className="bg-gray-50 px-4 py-5 sm:grid sm:grid-cols-3 sm:gap-4 sm:px-6">
                      <dt className="text-sm font-medium text-gray-500">Device ID</dt>
                      <dd className="mt-1 text-sm text-gray-900 sm:mt-0 sm:col-span-2 font-mono">
                        {device.deviceId}
                      </dd>
                    </div>
                    <div className="bg-white px-4 py-5 sm:grid sm:grid-cols-3 sm:gap-4 sm:px-6">
                      <dt className="text-sm font-medium text-gray-500">Name</dt>
                      <dd className="mt-1 text-sm text-gray-900 sm:mt-0 sm:col-span-2">
                        {isEditing ? (
                          <input
                            type="text"
                            value={editName}
                            onChange={(e) => setEditName(e.target.value)}
                            className="block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                          />
                        ) : (
                          device.name
                        )}
                      </dd>
                    </div>
                    <div className="bg-gray-50 px-4 py-5 sm:grid sm:grid-cols-3 sm:gap-4 sm:px-6">
                      <dt className="text-sm font-medium text-gray-500">Type</dt>
                      <dd className="mt-1 text-sm sm:mt-0 sm:col-span-2">
                        <span className="inline-flex rounded-full bg-indigo-100 px-2 text-xs font-semibold leading-5 text-indigo-800">
                          {device.deviceType}
                        </span>
                      </dd>
                    </div>
                    <div className="bg-white px-4 py-5 sm:grid sm:grid-cols-3 sm:gap-4 sm:px-6">
                      <dt className="text-sm font-medium text-gray-500">Authentication Method</dt>
                      <dd className="mt-1 text-sm text-gray-900 sm:mt-0 sm:col-span-2">
                        {device.authenticationMethod}
                      </dd>
                    </div>
                    <div className="bg-gray-50 px-4 py-5 sm:grid sm:grid-cols-3 sm:gap-4 sm:px-6">
                      <dt className="text-sm font-medium text-gray-500">Resource Path</dt>
                      <dd className="mt-1 text-sm text-gray-900 sm:mt-0 sm:col-span-2 font-mono">
                        {device.resourcePath}
                      </dd>
                    </div>
                    <div className="bg-white px-4 py-5 sm:grid sm:grid-cols-3 sm:gap-4 sm:px-6">
                      <dt className="text-sm font-medium text-gray-500">Tenant ID</dt>
                      <dd className="mt-1 text-sm text-gray-900 sm:mt-0 sm:col-span-2 font-mono">
                        {device.tenantId}
                      </dd>
                    </div>
                    <div className="bg-gray-50 px-4 py-5 sm:grid sm:grid-cols-3 sm:gap-4 sm:px-6">
                      <dt className="text-sm font-medium text-gray-500">Status</dt>
                      <dd className="mt-1 text-sm sm:mt-0 sm:col-span-2">
                        <div className="flex items-center gap-3">
                          <span
                            className={`inline-flex rounded-full px-2 text-xs font-semibold leading-5 ${
                              device.isActive
                                ? 'bg-green-100 text-green-800'
                                : 'bg-red-100 text-red-800'
                            }`}
                          >
                            {device.isActive ? 'Active' : 'Inactive'}
                          </span>
                          <span className="flex items-center gap-1">
                            <span
                              className={`inline-block h-2.5 w-2.5 rounded-full ${
                                device.isOnline ? 'bg-green-500' : 'bg-gray-400'
                              }`}
                            />
                            <span className="text-sm text-gray-600">
                              {device.isOnline ? 'Online' : 'Offline'}
                            </span>
                          </span>
                          {device.isProvisioned && (
                            <span className="inline-flex rounded-full bg-purple-100 px-2 text-xs font-semibold leading-5 text-purple-800">
                              Provisioned
                            </span>
                          )}
                        </div>
                      </dd>
                    </div>
                    <div className="bg-white px-4 py-5 sm:grid sm:grid-cols-3 sm:gap-4 sm:px-6">
                      <dt className="text-sm font-medium text-gray-500">Provisioned At</dt>
                      <dd className="mt-1 text-sm text-gray-900 sm:mt-0 sm:col-span-2">
                        {formatDate(device.provisionedAt)}
                      </dd>
                    </div>
                    <div className="bg-gray-50 px-4 py-5 sm:grid sm:grid-cols-3 sm:gap-4 sm:px-6">
                      <dt className="text-sm font-medium text-gray-500">Created At</dt>
                      <dd className="mt-1 text-sm text-gray-900 sm:mt-0 sm:col-span-2">
                        {formatDate(device.createdAt)}
                      </dd>
                    </div>
                    <div className="bg-white px-4 py-5 sm:grid sm:grid-cols-3 sm:gap-4 sm:px-6">
                      <dt className="text-sm font-medium text-gray-500">Last Updated</dt>
                      <dd className="mt-1 text-sm text-gray-900 sm:mt-0 sm:col-span-2">
                        {formatDate(device.updatedAt)}
                      </dd>
                    </div>
                    <div className="bg-gray-50 px-4 py-5 sm:grid sm:grid-cols-3 sm:gap-4 sm:px-6">
                      <dt className="text-sm font-medium text-gray-500">Tags</dt>
                      <dd className="mt-1 text-sm text-gray-900 sm:mt-0 sm:col-span-2">
                        {isEditing ? (
                          <div>
                            <input
                              type="text"
                              value={editTags}
                              onChange={(e) => setEditTags(e.target.value)}
                              placeholder="tag1, tag2, tag3"
                              className="block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                            />
                            <p className="mt-1 text-xs text-gray-500">
                              Comma-separated list of tags
                            </p>
                          </div>
                        ) : device.tags && device.tags.length > 0 ? (
                          <div className="flex flex-wrap gap-1">
                            {device.tags.map((tag, i) => (
                              <span
                                key={i}
                                className="inline-flex items-center rounded-full bg-gray-100 px-2.5 py-0.5 text-xs font-medium text-gray-800"
                              >
                                {tag}
                              </span>
                            ))}
                          </div>
                        ) : (
                          <span className="text-gray-400">No tags</span>
                        )}
                      </dd>
                    </div>
                    <div className="bg-white px-4 py-5 sm:grid sm:grid-cols-3 sm:gap-4 sm:px-6">
                      <dt className="text-sm font-medium text-gray-500">Metadata</dt>
                      <dd className="mt-1 text-sm text-gray-900 sm:mt-0 sm:col-span-2">
                        {isEditing ? (
                          <textarea
                            value={editMetadata}
                            onChange={(e) => setEditMetadata(e.target.value)}
                            rows={6}
                            className="block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm font-mono text-xs"
                          />
                        ) : device.metadata &&
                          Object.keys(device.metadata).length > 0 ? (
                          <pre className="bg-gray-50 rounded-md p-3 text-xs font-mono overflow-x-auto">
                            {JSON.stringify(device.metadata, null, 2)}
                          </pre>
                        ) : (
                          <span className="text-gray-400">No metadata</span>
                        )}
                      </dd>
                    </div>
                  </dl>
                </div>
                {/* Edit Actions */}
                {isEditing && (
                  <div className="border-t border-gray-200 px-4 py-4 sm:px-6 flex justify-end gap-3">
                    <button
                      type="button"
                      onClick={() => {
                        setIsEditing(false);
                        setEditName(device.name);
                        setEditTags((device.tags || []).join(', '));
                        setEditMetadata(JSON.stringify(device.metadata || {}, null, 2));
                      }}
                      className="inline-flex items-center px-4 py-2 border border-gray-300 rounded-md shadow-sm text-sm font-medium text-gray-700 bg-white hover:bg-gray-50"
                    >
                      Cancel
                    </button>
                    <button
                      type="button"
                      onClick={handleSave}
                      disabled={saving}
                      className="inline-flex items-center px-4 py-2 border border-transparent rounded-md shadow-sm text-sm font-medium text-white bg-indigo-600 hover:bg-indigo-700 focus:outline-none focus:ring-2 focus:ring-offset-2 focus:ring-indigo-500 disabled:opacity-50"
                    >
                      {saving ? 'Saving...' : 'Save Changes'}
                    </button>
                  </div>
                )}
              </div>
            </div>
          )}

          {/* Certificates Tab */}
          {activeTab === 'certificates' && (
            <div className="bg-white shadow overflow-hidden sm:rounded-lg">
              <div className="px-4 py-5 sm:px-6">
                <h3 className="text-lg leading-6 font-medium text-gray-900">
                  Device Certificates
                </h3>
                <p className="mt-1 max-w-2xl text-sm text-gray-500">
                  X.509 certificates associated with this device
                </p>
              </div>
              <div className="border-t border-gray-200">
                {device.certificates && device.certificates.length > 0 ? (
                  <div className="overflow-x-auto">
                    <table className="min-w-full divide-y divide-gray-300">
                      <thead className="bg-gray-50">
                        <tr>
                          <th className="px-3 py-3.5 text-left text-sm font-semibold text-gray-900">
                            Subject
                          </th>
                          <th className="px-3 py-3.5 text-left text-sm font-semibold text-gray-900">
                            Serial Number
                          </th>
                          <th className="px-3 py-3.5 text-left text-sm font-semibold text-gray-900">
                            Thumbprint
                          </th>
                          <th className="px-3 py-3.5 text-left text-sm font-semibold text-gray-900">
                            Status
                          </th>
                          <th className="px-3 py-3.5 text-left text-sm font-semibold text-gray-900">
                            Valid From
                          </th>
                          <th className="px-3 py-3.5 text-left text-sm font-semibold text-gray-900">
                            Expires
                          </th>
                        </tr>
                      </thead>
                      <tbody className="divide-y divide-gray-200 bg-white">
                        {device.certificates.map((cert) => {
                          const isExpired = new Date(cert.notAfter) < new Date();
                          const isExpiringSoon =
                            !isExpired &&
                            new Date(cert.notAfter).getTime() - Date.now() <
                              30 * 24 * 60 * 60 * 1000;
                          return (
                            <tr key={cert.id}>
                              <td className="whitespace-nowrap px-3 py-4 text-sm text-gray-900">
                                {cert.subjectName}
                              </td>
                              <td className="whitespace-nowrap px-3 py-4 text-sm text-gray-500 font-mono text-xs">
                                {cert.serialNumber}
                              </td>
                              <td className="px-3 py-4 text-sm text-gray-500 font-mono text-xs max-w-xs truncate">
                                {cert.thumbprint}
                              </td>
                              <td className="whitespace-nowrap px-3 py-4 text-sm">
                                <span
                                  className={`inline-flex rounded-full px-2 text-xs font-semibold leading-5 ${
                                    isExpired
                                      ? 'bg-red-100 text-red-800'
                                      : isExpiringSoon
                                        ? 'bg-yellow-100 text-yellow-800'
                                        : cert.status === 'Active'
                                          ? 'bg-green-100 text-green-800'
                                          : 'bg-gray-100 text-gray-800'
                                  }`}
                                >
                                  {isExpired ? 'Expired' : cert.status}
                                </span>
                              </td>
                              <td className="whitespace-nowrap px-3 py-4 text-sm text-gray-500">
                                {formatDate(cert.notBefore)}
                              </td>
                              <td className="whitespace-nowrap px-3 py-4 text-sm text-gray-500">
                                {formatDate(cert.notAfter)}
                              </td>
                            </tr>
                          );
                        })}
                      </tbody>
                    </table>
                  </div>
                ) : (
                  <div className="px-4 py-12 text-center text-sm text-gray-500">
                    No certificates associated with this device
                  </div>
                )}
              </div>
            </div>
          )}

          {/* Permissions Tab */}
          {activeTab === 'permissions' && (
            <div className="bg-white shadow overflow-hidden sm:rounded-lg">
              <div className="px-4 py-5 sm:px-6">
                <h3 className="text-lg leading-6 font-medium text-gray-900">
                  Device Permissions
                </h3>
                <p className="mt-1 max-w-2xl text-sm text-gray-500">
                  Permissions granted to this device within the resource hierarchy
                </p>
              </div>
              <div className="border-t border-gray-200">
                {device.permissions && device.permissions.length > 0 ? (
                  <ul className="divide-y divide-gray-200">
                    {device.permissions.map((permission, index) => {
                      const parts = permission.split(':');
                      const depth = parts.length - 1;
                      return (
                        <li key={index} className="px-4 py-3 sm:px-6">
                          <div className="flex items-center">
                            <div style={{ paddingLeft: `${depth * 16}px` }} className="flex items-center gap-2">
                              {depth > 0 && (
                                <span className="text-gray-300">{'>'}</span>
                              )}
                              <span className="inline-flex items-center rounded-md bg-blue-50 px-2 py-1 text-xs font-medium text-blue-700 ring-1 ring-inset ring-blue-600/20 font-mono">
                                {permission}
                              </span>
                            </div>
                          </div>
                        </li>
                      );
                    })}
                  </ul>
                ) : (
                  <div className="px-4 py-12 text-center text-sm text-gray-500">
                    No permissions assigned to this device
                  </div>
                )}
              </div>
            </div>
          )}

          {/* Activity Tab */}
          {activeTab === 'activity' && (
            <div className="space-y-6">
              {/* Activity Summary */}
              <div className="bg-white shadow overflow-hidden sm:rounded-lg">
                <div className="px-4 py-5 sm:px-6">
                  <h3 className="text-lg leading-6 font-medium text-gray-900">
                    Device Activity
                  </h3>
                  <p className="mt-1 max-w-2xl text-sm text-gray-500">
                    Connection and authentication history
                  </p>
                </div>
                <div className="border-t border-gray-200">
                  <dl>
                    <div className="bg-gray-50 px-4 py-5 sm:grid sm:grid-cols-3 sm:gap-4 sm:px-6">
                      <dt className="text-sm font-medium text-gray-500">Current Status</dt>
                      <dd className="mt-1 text-sm sm:mt-0 sm:col-span-2">
                        <span className="flex items-center gap-2">
                          <span
                            className={`inline-block h-3 w-3 rounded-full ${
                              device.isOnline ? 'bg-green-500' : 'bg-gray-400'
                            }`}
                          />
                          <span className="font-medium">
                            {device.isOnline ? 'Online' : 'Offline'}
                          </span>
                        </span>
                      </dd>
                    </div>
                    <div className="bg-white px-4 py-5 sm:grid sm:grid-cols-3 sm:gap-4 sm:px-6">
                      <dt className="text-sm font-medium text-gray-500">Last Seen</dt>
                      <dd className="mt-1 text-sm text-gray-900 sm:mt-0 sm:col-span-2">
                        {formatDate(device.lastSeenAt)}
                      </dd>
                    </div>
                    <div className="bg-gray-50 px-4 py-5 sm:grid sm:grid-cols-3 sm:gap-4 sm:px-6">
                      <dt className="text-sm font-medium text-gray-500">Last Authenticated</dt>
                      <dd className="mt-1 text-sm text-gray-900 sm:mt-0 sm:col-span-2">
                        {formatDate(device.lastAuthenticatedAt)}
                      </dd>
                    </div>
                    <div className="bg-white px-4 py-5 sm:grid sm:grid-cols-3 sm:gap-4 sm:px-6">
                      <dt className="text-sm font-medium text-gray-500">Last IP Address</dt>
                      <dd className="mt-1 text-sm text-gray-900 sm:mt-0 sm:col-span-2">
                        {device.lastIpAddress ? (
                          <span className="font-mono">{device.lastIpAddress}</span>
                        ) : (
                          <span className="text-gray-400">N/A</span>
                        )}
                      </dd>
                    </div>
                  </dl>
                </div>
              </div>

              {/* Timeline */}
              <div className="bg-white shadow overflow-hidden sm:rounded-lg">
                <div className="px-4 py-5 sm:px-6">
                  <h3 className="text-lg leading-6 font-medium text-gray-900">Timeline</h3>
                </div>
                <div className="border-t border-gray-200 px-4 py-5 sm:px-6">
                  <div className="flow-root">
                    <ul className="-mb-8">
                      {device.lastAuthenticatedAt && (
                        <li className="relative pb-8">
                          <span
                            className="absolute left-4 top-4 -ml-px h-full w-0.5 bg-gray-200"
                            aria-hidden="true"
                          />
                          <div className="relative flex space-x-3">
                            <div>
                              <span className="h-8 w-8 rounded-full bg-blue-500 flex items-center justify-center ring-8 ring-white">
                                <svg
                                  className="h-4 w-4 text-white"
                                  fill="none"
                                  viewBox="0 0 24 24"
                                  strokeWidth="1.5"
                                  stroke="currentColor"
                                >
                                  <path
                                    strokeLinecap="round"
                                    strokeLinejoin="round"
                                    d="M16.5 10.5V6.75a4.5 4.5 0 10-9 0v3.75m-.75 11.25h10.5a2.25 2.25 0 002.25-2.25v-6.75a2.25 2.25 0 00-2.25-2.25H6.75a2.25 2.25 0 00-2.25 2.25v6.75a2.25 2.25 0 002.25 2.25z"
                                  />
                                </svg>
                              </span>
                            </div>
                            <div className="flex min-w-0 flex-1 justify-between space-x-4 pt-1.5">
                              <div>
                                <p className="text-sm text-gray-500">
                                  Last authenticated{' '}
                                  {device.lastIpAddress && (
                                    <span>
                                      from <span className="font-mono font-medium text-gray-900">{device.lastIpAddress}</span>
                                    </span>
                                  )}
                                </p>
                              </div>
                              <div className="whitespace-nowrap text-right text-sm text-gray-500">
                                {formatDate(device.lastAuthenticatedAt)}
                              </div>
                            </div>
                          </div>
                        </li>
                      )}
                      {device.lastSeenAt && (
                        <li className="relative pb-8">
                          <span
                            className="absolute left-4 top-4 -ml-px h-full w-0.5 bg-gray-200"
                            aria-hidden="true"
                          />
                          <div className="relative flex space-x-3">
                            <div>
                              <span className="h-8 w-8 rounded-full bg-green-500 flex items-center justify-center ring-8 ring-white">
                                <svg
                                  className="h-4 w-4 text-white"
                                  fill="none"
                                  viewBox="0 0 24 24"
                                  strokeWidth="1.5"
                                  stroke="currentColor"
                                >
                                  <path
                                    strokeLinecap="round"
                                    strokeLinejoin="round"
                                    d="M2.036 12.322a1.012 1.012 0 010-.639C3.423 7.51 7.36 4.5 12 4.5c4.638 0 8.573 3.007 9.963 7.178.07.207.07.431 0 .639C20.577 16.49 16.64 19.5 12 19.5c-4.638 0-8.573-3.007-9.963-7.178z"
                                  />
                                  <path
                                    strokeLinecap="round"
                                    strokeLinejoin="round"
                                    d="M15 12a3 3 0 11-6 0 3 3 0 016 0z"
                                  />
                                </svg>
                              </span>
                            </div>
                            <div className="flex min-w-0 flex-1 justify-between space-x-4 pt-1.5">
                              <div>
                                <p className="text-sm text-gray-500">Last seen online</p>
                              </div>
                              <div className="whitespace-nowrap text-right text-sm text-gray-500">
                                {formatDate(device.lastSeenAt)}
                              </div>
                            </div>
                          </div>
                        </li>
                      )}
                      {device.provisionedAt && (
                        <li className="relative pb-8">
                          <span
                            className="absolute left-4 top-4 -ml-px h-full w-0.5 bg-gray-200"
                            aria-hidden="true"
                          />
                          <div className="relative flex space-x-3">
                            <div>
                              <span className="h-8 w-8 rounded-full bg-purple-500 flex items-center justify-center ring-8 ring-white">
                                <svg
                                  className="h-4 w-4 text-white"
                                  fill="none"
                                  viewBox="0 0 24 24"
                                  strokeWidth="1.5"
                                  stroke="currentColor"
                                >
                                  <path
                                    strokeLinecap="round"
                                    strokeLinejoin="round"
                                    d="M9 12.75L11.25 15 15 9.75M21 12a9 9 0 11-18 0 9 9 0 0118 0z"
                                  />
                                </svg>
                              </span>
                            </div>
                            <div className="flex min-w-0 flex-1 justify-between space-x-4 pt-1.5">
                              <div>
                                <p className="text-sm text-gray-500">Device provisioned</p>
                              </div>
                              <div className="whitespace-nowrap text-right text-sm text-gray-500">
                                {formatDate(device.provisionedAt)}
                              </div>
                            </div>
                          </div>
                        </li>
                      )}
                      <li className="relative">
                        <div className="relative flex space-x-3">
                          <div>
                            <span className="h-8 w-8 rounded-full bg-gray-400 flex items-center justify-center ring-8 ring-white">
                              <svg
                                className="h-4 w-4 text-white"
                                fill="none"
                                viewBox="0 0 24 24"
                                strokeWidth="1.5"
                                stroke="currentColor"
                              >
                                <path
                                  strokeLinecap="round"
                                  strokeLinejoin="round"
                                  d="M12 6v6h4.5m4.5 0a9 9 0 11-18 0 9 9 0 0118 0z"
                                />
                              </svg>
                            </span>
                          </div>
                          <div className="flex min-w-0 flex-1 justify-between space-x-4 pt-1.5">
                            <div>
                              <p className="text-sm text-gray-500">Device registered</p>
                            </div>
                            <div className="whitespace-nowrap text-right text-sm text-gray-500">
                              {formatDate(device.createdAt)}
                            </div>
                          </div>
                        </div>
                      </li>
                    </ul>
                  </div>
                </div>
              </div>
            </div>
          )}
        </div>

        {/* Deactivate Confirmation Modal */}
        {showDeactivateModal && (
          <div className="fixed inset-0 z-50 overflow-y-auto">
            <div className="flex min-h-full items-end justify-center p-4 text-center sm:items-center sm:p-0">
              {/* Backdrop */}
              <div
                className="fixed inset-0 bg-gray-500 bg-opacity-75 transition-opacity"
                onClick={() => setShowDeactivateModal(false)}
              />
              {/* Modal */}
              <div className="relative transform overflow-hidden rounded-lg bg-white text-left shadow-xl transition-all sm:my-8 sm:w-full sm:max-w-lg">
                <div className="bg-white px-4 pb-4 pt-5 sm:p-6 sm:pb-4">
                  <div className="sm:flex sm:items-start">
                    <div className="mx-auto flex h-12 w-12 flex-shrink-0 items-center justify-center rounded-full bg-red-100 sm:mx-0 sm:h-10 sm:w-10">
                      <svg
                        className="h-6 w-6 text-red-600"
                        fill="none"
                        viewBox="0 0 24 24"
                        strokeWidth="1.5"
                        stroke="currentColor"
                      >
                        <path
                          strokeLinecap="round"
                          strokeLinejoin="round"
                          d="M12 9v3.75m-9.303 3.376c-.866 1.5.217 3.374 1.948 3.374h14.71c1.73 0 2.813-1.874 1.948-3.374L13.949 3.378c-.866-1.5-3.032-1.5-3.898 0L2.697 16.126zM12 15.75h.007v.008H12v-.008z"
                        />
                      </svg>
                    </div>
                    <div className="mt-3 text-center sm:ml-4 sm:mt-0 sm:text-left">
                      <h3 className="text-base font-semibold leading-6 text-gray-900">
                        Deactivate Device
                      </h3>
                      <div className="mt-2">
                        <p className="text-sm text-gray-500">
                          Are you sure you want to deactivate{' '}
                          <span className="font-semibold">{device.name}</span> (
                          <span className="font-mono text-xs">{device.deviceId}</span>)? The
                          device will no longer be able to authenticate or communicate with the
                          system.
                        </p>
                      </div>
                    </div>
                  </div>
                </div>
                <div className="bg-gray-50 px-4 py-3 sm:flex sm:flex-row-reverse sm:px-6">
                  <button
                    type="button"
                    onClick={handleDeactivate}
                    className="inline-flex w-full justify-center rounded-md bg-red-600 px-3 py-2 text-sm font-semibold text-white shadow-sm hover:bg-red-500 sm:ml-3 sm:w-auto"
                  >
                    Deactivate
                  </button>
                  <button
                    type="button"
                    onClick={() => setShowDeactivateModal(false)}
                    className="mt-3 inline-flex w-full justify-center rounded-md bg-white px-3 py-2 text-sm font-semibold text-gray-900 shadow-sm ring-1 ring-inset ring-gray-300 hover:bg-gray-50 sm:mt-0 sm:w-auto"
                  >
                    Cancel
                  </button>
                </div>
              </div>
            </div>
          </div>
        )}
      </div>
    </DashboardLayout>
  );
}
