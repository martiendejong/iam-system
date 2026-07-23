import { useState, useEffect } from 'react';
import { useNavigate, Link } from 'react-router-dom';
import DashboardLayout from '../../components/layout/DashboardLayout';
import { deviceApi } from '../../services/deviceApi';
import type { Device, RegisterDeviceRequest } from '../../types/devices';

const DEVICE_TYPES = [
  { value: 'sensor', label: 'Sensor' },
  { value: 'actuator', label: 'Actuator' },
  { value: 'gateway', label: 'Gateway' },
  { value: 'controller', label: 'Controller' },
  { value: 'camera', label: 'Camera' },
  { value: 'hvac', label: 'HVAC' },
  { value: 'custom', label: 'Custom' },
];

const AUTH_METHODS = [
  { value: 'certificate', label: 'Certificate', description: 'X.509 certificate-based authentication' },
  { value: 'hmac', label: 'HMAC', description: 'Hash-based message authentication code' },
];

interface Tenant {
  id: string;
  name: string;
}

export default function DeviceRegisterPage() {
  const navigate = useNavigate();
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');
  const [tenants, setTenants] = useState<Tenant[]>([]);
  const [loadingTenants, setLoadingTenants] = useState(true);
  const [registeredDevice, setRegisteredDevice] = useState<Device | null>(null);

  // Form state
  const [deviceId, setDeviceId] = useState('');
  const [name, setName] = useState('');
  const [deviceType, setDeviceType] = useState('sensor');
  const [authenticationMethod, setAuthenticationMethod] = useState('certificate');
  const [tenantId, setTenantId] = useState('');
  const [resourcePath, setResourcePath] = useState('');
  const [permissionsText, setPermissionsText] = useState('');
  const [metadataEntries, setMetadataEntries] = useState<{ key: string; value: string }[]>([
    { key: '', value: '' },
  ]);

  // Validation state
  const [fieldErrors, setFieldErrors] = useState<Record<string, string>>({});

  useEffect(() => {
    loadTenants();
  }, []);

  const loadTenants = async () => {
    try {
      setLoadingTenants(true);
      const data = await deviceApi.getTenants();
      setTenants(data);
      if (data.length > 0) {
        setTenantId(data[0].id);
      }
    } catch (err: any) {
      console.error('Failed to load tenants:', err);
    } finally {
      setLoadingTenants(false);
    }
  };

  const validate = (): boolean => {
    const errors: Record<string, string> = {};
    if (!deviceId.trim()) errors.deviceId = 'Device ID is required';
    if (!name.trim()) errors.name = 'Name is required';
    if (!deviceType) errors.deviceType = 'Device type is required';
    if (!authenticationMethod) errors.authenticationMethod = 'Authentication method is required';
    if (!tenantId) errors.tenantId = 'Tenant is required';
    if (!resourcePath.trim()) errors.resourcePath = 'Resource path is required';
    setFieldErrors(errors);
    return Object.keys(errors).length === 0;
  };

  const addMetadataEntry = () => {
    setMetadataEntries([...metadataEntries, { key: '', value: '' }]);
  };

  const removeMetadataEntry = (index: number) => {
    setMetadataEntries(metadataEntries.filter((_, i) => i !== index));
  };

  const updateMetadataEntry = (index: number, field: 'key' | 'value', value: string) => {
    const updated = [...metadataEntries];
    updated[index][field] = value;
    setMetadataEntries(updated);
  };

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!validate()) return;

    try {
      setSaving(true);
      setError('');

      // Build metadata object from key-value entries
      const metadata: Record<string, any> = {};
      metadataEntries.forEach((entry) => {
        if (entry.key.trim()) {
          // Try to parse value as JSON, fall back to string
          try {
            metadata[entry.key.trim()] = JSON.parse(entry.value);
          } catch {
            metadata[entry.key.trim()] = entry.value;
          }
        }
      });

      // Parse permissions from text (one per line or comma-separated)
      const permissions = permissionsText
        .split(/[\n,]/)
        .map((p) => p.trim())
        .filter(Boolean);

      const request: RegisterDeviceRequest = {
        deviceId: deviceId.trim(),
        name: name.trim(),
        deviceType,
        authenticationMethod,
        tenantId,
        resourcePath: resourcePath.trim(),
        permissions,
        metadata: Object.keys(metadata).length > 0 ? metadata : undefined,
      };

      const device = await deviceApi.registerDevice(request);
      setRegisteredDevice(device);
    } catch (err: any) {
      setError(err.response?.data?.message || 'Failed to register device');
    } finally {
      setSaving(false);
    }
  };

  // Success state
  if (registeredDevice) {
    return (
      <DashboardLayout>
        <div className="max-w-3xl mx-auto px-4 sm:px-6 lg:px-8">
          <div className="bg-white shadow sm:rounded-lg">
            <div className="px-4 py-5 sm:p-6">
              <div className="text-center">
                <div className="mx-auto flex h-12 w-12 items-center justify-center rounded-full bg-green-100">
                  <svg
                    className="h-6 w-6 text-green-600"
                    fill="none"
                    viewBox="0 0 24 24"
                    strokeWidth="1.5"
                    stroke="currentColor"
                  >
                    <path
                      strokeLinecap="round"
                      strokeLinejoin="round"
                      d="M4.5 12.75l6 6 9-13.5"
                    />
                  </svg>
                </div>
                <h3 className="mt-4 text-lg font-medium text-gray-900">
                  Device Registered Successfully
                </h3>
                <p className="mt-2 text-sm text-gray-500">
                  The device has been registered and is ready for provisioning.
                </p>
              </div>

              <div className="mt-6 border-t border-gray-200 pt-6">
                <dl className="grid grid-cols-1 gap-x-4 gap-y-4 sm:grid-cols-2">
                  <div>
                    <dt className="text-sm font-medium text-gray-500">Device ID</dt>
                    <dd className="mt-1 text-sm text-gray-900 font-mono">
                      {registeredDevice.deviceId}
                    </dd>
                  </div>
                  <div>
                    <dt className="text-sm font-medium text-gray-500">Name</dt>
                    <dd className="mt-1 text-sm text-gray-900">{registeredDevice.name}</dd>
                  </div>
                  <div>
                    <dt className="text-sm font-medium text-gray-500">Type</dt>
                    <dd className="mt-1 text-sm text-gray-900">{registeredDevice.deviceType}</dd>
                  </div>
                  <div>
                    <dt className="text-sm font-medium text-gray-500">Authentication</dt>
                    <dd className="mt-1 text-sm text-gray-900">
                      {registeredDevice.authenticationMethod}
                    </dd>
                  </div>
                  <div>
                    <dt className="text-sm font-medium text-gray-500">Resource Path</dt>
                    <dd className="mt-1 text-sm text-gray-900 font-mono">
                      {registeredDevice.resourcePath}
                    </dd>
                  </div>
                  <div>
                    <dt className="text-sm font-medium text-gray-500">Status</dt>
                    <dd className="mt-1">
                      <span className="inline-flex rounded-full bg-green-100 px-2 text-xs font-semibold leading-5 text-green-800">
                        Active
                      </span>
                    </dd>
                  </div>
                </dl>
              </div>

              <div className="mt-6 flex justify-center gap-4">
                <Link
                  to={`/devices/${registeredDevice.id}`}
                  className="inline-flex items-center px-4 py-2 border border-transparent rounded-md shadow-sm text-sm font-medium text-white bg-indigo-600 hover:bg-indigo-700 focus:outline-none focus:ring-2 focus:ring-offset-2 focus:ring-indigo-500"
                >
                  View Device Details
                </Link>
                <Link
                  to="/devices"
                  className="inline-flex items-center px-4 py-2 border border-gray-300 rounded-md shadow-sm text-sm font-medium text-gray-700 bg-white hover:bg-gray-50"
                >
                  Back to Devices
                </Link>
              </div>
            </div>
          </div>
        </div>
      </DashboardLayout>
    );
  }

  return (
    <DashboardLayout>
      <div className="max-w-3xl mx-auto px-4 sm:px-6 lg:px-8">
        {/* Header */}
        <div className="md:flex md:items-center md:justify-between">
          <div className="flex-1 min-w-0">
            <h2 className="text-2xl font-bold leading-7 text-gray-900 sm:text-3xl sm:truncate">
              Register Device
            </h2>
          </div>
          <div className="mt-4 flex md:mt-0 md:ml-4">
            <button
              type="button"
              onClick={() => navigate('/devices')}
              className="inline-flex items-center px-4 py-2 border border-gray-300 rounded-md shadow-sm text-sm font-medium text-gray-700 bg-white hover:bg-gray-50 focus:outline-none focus:ring-2 focus:ring-offset-2 focus:ring-indigo-500"
            >
              Cancel
            </button>
          </div>
        </div>

        {/* Registration Form */}
        <div className="mt-6 bg-white shadow sm:rounded-lg">
          <div className="px-4 py-5 sm:p-6">
            <h3 className="text-lg leading-6 font-medium text-gray-900 mb-4">
              Device Information
            </h3>

            {error && (
              <div className="mb-4 bg-red-50 border border-red-200 text-red-700 px-4 py-3 rounded">
                {error}
              </div>
            )}

            <form onSubmit={handleSubmit} className="space-y-6">
              <div className="grid grid-cols-1 gap-6 sm:grid-cols-2">
                {/* Device ID */}
                <div>
                  <label htmlFor="deviceId" className="block text-sm font-medium text-gray-700">
                    Device ID *
                  </label>
                  <input
                    type="text"
                    id="deviceId"
                    value={deviceId}
                    onChange={(e) => setDeviceId(e.target.value)}
                    placeholder="e.g., sensor-floor3-temp-001"
                    className={`mt-1 block w-full rounded-md shadow-sm sm:text-sm ${
                      fieldErrors.deviceId
                        ? 'border-red-300 focus:ring-red-500 focus:border-red-500'
                        : 'border-gray-300 focus:ring-indigo-500 focus:border-indigo-500'
                    }`}
                  />
                  {fieldErrors.deviceId && (
                    <p className="mt-1 text-sm text-red-600">{fieldErrors.deviceId}</p>
                  )}
                </div>

                {/* Name */}
                <div>
                  <label htmlFor="name" className="block text-sm font-medium text-gray-700">
                    Name *
                  </label>
                  <input
                    type="text"
                    id="name"
                    value={name}
                    onChange={(e) => setName(e.target.value)}
                    placeholder="e.g., Floor 3 Temperature Sensor"
                    className={`mt-1 block w-full rounded-md shadow-sm sm:text-sm ${
                      fieldErrors.name
                        ? 'border-red-300 focus:ring-red-500 focus:border-red-500'
                        : 'border-gray-300 focus:ring-indigo-500 focus:border-indigo-500'
                    }`}
                  />
                  {fieldErrors.name && (
                    <p className="mt-1 text-sm text-red-600">{fieldErrors.name}</p>
                  )}
                </div>
              </div>

              {/* Device Type */}
              <div>
                <label htmlFor="deviceType" className="block text-sm font-medium text-gray-700">
                  Device Type *
                </label>
                <select
                  id="deviceType"
                  value={deviceType}
                  onChange={(e) => setDeviceType(e.target.value)}
                  className={`mt-1 block w-full rounded-md shadow-sm sm:text-sm ${
                    fieldErrors.deviceType
                      ? 'border-red-300 focus:ring-red-500 focus:border-red-500'
                      : 'border-gray-300 focus:ring-indigo-500 focus:border-indigo-500'
                  }`}
                >
                  {DEVICE_TYPES.map((type) => (
                    <option key={type.value} value={type.value}>
                      {type.label}
                    </option>
                  ))}
                </select>
                {fieldErrors.deviceType && (
                  <p className="mt-1 text-sm text-red-600">{fieldErrors.deviceType}</p>
                )}
              </div>

              {/* Authentication Method */}
              <div>
                <label className="block text-sm font-medium text-gray-700 mb-3">
                  Authentication Method *
                </label>
                <div className="space-y-3">
                  {AUTH_METHODS.map((method) => (
                    <label
                      key={method.value}
                      className={`relative flex cursor-pointer rounded-lg border p-4 shadow-sm focus:outline-none ${
                        authenticationMethod === method.value
                          ? 'border-indigo-500 ring-2 ring-indigo-500'
                          : 'border-gray-300'
                      }`}
                    >
                      <input
                        type="radio"
                        name="authMethod"
                        value={method.value}
                        checked={authenticationMethod === method.value}
                        onChange={(e) => setAuthenticationMethod(e.target.value)}
                        className="sr-only"
                      />
                      <span className="flex flex-1">
                        <span className="flex flex-col">
                          <span className="block text-sm font-medium text-gray-900">
                            {method.label}
                          </span>
                          <span className="mt-1 flex items-center text-sm text-gray-500">
                            {method.description}
                          </span>
                        </span>
                      </span>
                      <span
                        className={`pointer-events-none absolute -inset-px rounded-lg border-2 ${
                          authenticationMethod === method.value
                            ? 'border-indigo-500'
                            : 'border-transparent'
                        }`}
                        aria-hidden="true"
                      />
                    </label>
                  ))}
                </div>
                {fieldErrors.authenticationMethod && (
                  <p className="mt-1 text-sm text-red-600">{fieldErrors.authenticationMethod}</p>
                )}
              </div>

              {/* Tenant */}
              <div>
                <label htmlFor="tenantId" className="block text-sm font-medium text-gray-700">
                  Tenant *
                </label>
                {loadingTenants ? (
                  <div className="mt-1 text-sm text-gray-500">Loading tenants...</div>
                ) : (
                  <select
                    id="tenantId"
                    value={tenantId}
                    onChange={(e) => setTenantId(e.target.value)}
                    className={`mt-1 block w-full rounded-md shadow-sm sm:text-sm ${
                      fieldErrors.tenantId
                        ? 'border-red-300 focus:ring-red-500 focus:border-red-500'
                        : 'border-gray-300 focus:ring-indigo-500 focus:border-indigo-500'
                    }`}
                  >
                    <option value="">Select a tenant...</option>
                    {tenants.map((tenant) => (
                      <option key={tenant.id} value={tenant.id}>
                        {tenant.name}
                      </option>
                    ))}
                  </select>
                )}
                {fieldErrors.tenantId && (
                  <p className="mt-1 text-sm text-red-600">{fieldErrors.tenantId}</p>
                )}
              </div>

              {/* Resource Path */}
              <div>
                <label htmlFor="resourcePath" className="block text-sm font-medium text-gray-700">
                  Resource Path *
                </label>
                <input
                  type="text"
                  id="resourcePath"
                  value={resourcePath}
                  onChange={(e) => setResourcePath(e.target.value)}
                  placeholder="e.g., /buildings/hq/floors/3/rooms/301"
                  className={`mt-1 block w-full rounded-md shadow-sm sm:text-sm font-mono ${
                    fieldErrors.resourcePath
                      ? 'border-red-300 focus:ring-red-500 focus:border-red-500'
                      : 'border-gray-300 focus:ring-indigo-500 focus:border-indigo-500'
                  }`}
                />
                <p className="mt-1 text-xs text-gray-500">
                  Hierarchical path in the format: /buildings/{'{id}'}/floors/{'{id}'}/rooms/{'{id}'}
                </p>
                {fieldErrors.resourcePath && (
                  <p className="mt-1 text-sm text-red-600">{fieldErrors.resourcePath}</p>
                )}
              </div>

              {/* Permissions */}
              <div>
                <label
                  htmlFor="permissions"
                  className="block text-sm font-medium text-gray-700"
                >
                  Permissions
                </label>
                <textarea
                  id="permissions"
                  value={permissionsText}
                  onChange={(e) => setPermissionsText(e.target.value)}
                  rows={4}
                  placeholder={"device:read\ndevice:write\ntelemetry:publish\ncommand:receive"}
                  className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:ring-indigo-500 focus:border-indigo-500 sm:text-sm font-mono"
                />
                <p className="mt-1 text-xs text-gray-500">
                  One permission per line, or comma-separated. Example: device:read, telemetry:publish
                </p>
              </div>

              {/* Metadata Key-Value Pairs */}
              <div>
                <div className="flex items-center justify-between">
                  <label className="block text-sm font-medium text-gray-700">
                    Metadata
                  </label>
                  <button
                    type="button"
                    onClick={addMetadataEntry}
                    className="text-sm text-indigo-600 hover:text-indigo-500"
                  >
                    + Add Entry
                  </button>
                </div>
                <div className="mt-2 space-y-2">
                  {metadataEntries.map((entry, index) => (
                    <div key={index} className="flex gap-2 items-start">
                      <input
                        type="text"
                        value={entry.key}
                        onChange={(e) => updateMetadataEntry(index, 'key', e.target.value)}
                        placeholder="Key"
                        className="block w-1/3 rounded-md border-gray-300 shadow-sm focus:ring-indigo-500 focus:border-indigo-500 sm:text-sm"
                      />
                      <input
                        type="text"
                        value={entry.value}
                        onChange={(e) => updateMetadataEntry(index, 'value', e.target.value)}
                        placeholder="Value"
                        className="block flex-1 rounded-md border-gray-300 shadow-sm focus:ring-indigo-500 focus:border-indigo-500 sm:text-sm"
                      />
                      {metadataEntries.length > 1 && (
                        <button
                          type="button"
                          onClick={() => removeMetadataEntry(index)}
                          className="inline-flex items-center p-2 text-gray-400 hover:text-red-500"
                        >
                          <svg
                            className="h-4 w-4"
                            fill="none"
                            viewBox="0 0 24 24"
                            strokeWidth="1.5"
                            stroke="currentColor"
                          >
                            <path
                              strokeLinecap="round"
                              strokeLinejoin="round"
                              d="M6 18L18 6M6 6l12 12"
                            />
                          </svg>
                        </button>
                      )}
                    </div>
                  ))}
                </div>
                <p className="mt-1 text-xs text-gray-500">
                  Key-value pairs for additional device metadata. Values can be strings or JSON.
                </p>
              </div>

              {/* Submit Button */}
              <div className="flex justify-end gap-3">
                <button
                  type="button"
                  onClick={() => navigate('/devices')}
                  className="inline-flex justify-center py-2 px-4 border border-gray-300 shadow-sm text-sm font-medium rounded-md text-gray-700 bg-white hover:bg-gray-50 focus:outline-none focus:ring-2 focus:ring-offset-2 focus:ring-indigo-500"
                >
                  Cancel
                </button>
                <button
                  type="submit"
                  disabled={saving}
                  className="inline-flex justify-center py-2 px-4 border border-transparent shadow-sm text-sm font-medium rounded-md text-white bg-indigo-600 hover:bg-indigo-700 focus:outline-none focus:ring-2 focus:ring-offset-2 focus:ring-indigo-500 disabled:opacity-50"
                >
                  {saving ? 'Registering...' : 'Register Device'}
                </button>
              </div>
            </form>
          </div>
        </div>
      </div>
    </DashboardLayout>
  );
}
