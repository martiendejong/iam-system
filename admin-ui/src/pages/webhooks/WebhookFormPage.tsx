import { useState, useEffect } from 'react';
import { useParams, useNavigate } from 'react-router-dom';
import { useForm, Controller } from 'react-hook-form';
import DashboardLayout from '../../components/layout/DashboardLayout';
import { webhookApi } from '../../services/webhookApi';
import { api } from '../../services/api';
import type { EventTypeInfo } from '../../types/webhooks';

interface WebhookFormData {
  name: string;
  url: string;
  secret: string;
  tenantId: string;
  isActive: boolean;
  maxRetries: number;
  timeoutSeconds: number;
}

export default function WebhookFormPage() {
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const isEditMode = !!id;

  const [loading, setLoading] = useState(isEditMode);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');
  const [tenants, setTenants] = useState<any[]>([]);
  const [eventTypes, setEventTypes] = useState<EventTypeInfo[]>([]);
  const [selectedEvents, setSelectedEvents] = useState<string[]>([]);
  const [hasSecret, setHasSecret] = useState(false);

  const {
    register,
    handleSubmit,
    reset,
    control,
    formState: { errors },
  } = useForm<WebhookFormData>({
    defaultValues: {
      isActive: true,
      maxRetries: 3,
      timeoutSeconds: 30,
    },
  });

  useEffect(() => {
    loadTenants();
    loadEventTypes();
    if (isEditMode) {
      loadWebhook();
    }
  }, [id]);

  const loadTenants = async () => {
    try {
      const data = await api.getTenants();
      setTenants(data);
    } catch (err) {
      console.error('Failed to load tenants:', err);
    }
  };

  const loadEventTypes = async () => {
    try {
      const data = await webhookApi.getEventTypes();
      setEventTypes(data.eventTypes);
    } catch (err) {
      console.error('Failed to load event types:', err);
    }
  };

  const loadWebhook = async () => {
    try {
      setLoading(true);
      const webhook = await webhookApi.getWebhook(id!);
      setSelectedEvents(webhook.events);
      setHasSecret(webhook.hasSecret);
      reset({
        name: webhook.name,
        url: webhook.url,
        secret: '',
        tenantId: webhook.tenantId,
        isActive: webhook.isActive,
        maxRetries: webhook.maxRetries,
        timeoutSeconds: webhook.timeoutSeconds,
      });
    } catch (err: any) {
      setError(err.response?.data?.error || 'Failed to load webhook');
    } finally {
      setLoading(false);
    }
  };

  const toggleEvent = (eventName: string) => {
    setSelectedEvents((prev) =>
      prev.includes(eventName) ? prev.filter((e) => e !== eventName) : [...prev, eventName]
    );
  };

  const eventsByCategory = eventTypes.reduce<Record<string, EventTypeInfo[]>>((acc, evt) => {
    (acc[evt.category] ||= []).push(evt);
    return acc;
  }, {});

  const onSubmit = async (data: WebhookFormData) => {
    if (selectedEvents.length === 0) {
      setError('Select at least one event type to subscribe to.');
      return;
    }

    try {
      setSaving(true);
      setError('');

      if (isEditMode) {
        await webhookApi.updateWebhook(id!, {
          name: data.name,
          url: data.url,
          events: selectedEvents,
          isActive: data.isActive,
        });
      } else {
        await webhookApi.createWebhook({
          name: data.name,
          url: data.url,
          secret: data.secret || undefined,
          tenantId: data.tenantId,
          events: selectedEvents,
          isActive: data.isActive,
          maxRetries: data.maxRetries,
          timeoutSeconds: data.timeoutSeconds,
        });
      }

      navigate('/webhooks');
    } catch (err: any) {
      setError(err.response?.data?.error || `Failed to ${isEditMode ? 'update' : 'create'} webhook`);
    } finally {
      setSaving(false);
    }
  };

  if (loading) {
    return (
      <DashboardLayout>
        <div className="flex items-center justify-center min-h-screen">
          <div className="text-center">
            <div className="inline-block animate-spin rounded-full h-8 w-8 border-b-2 border-indigo-600"></div>
            <p className="mt-2 text-sm text-gray-500">Loading webhook...</p>
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
              {isEditMode ? 'Edit Webhook' : 'Create New Webhook'}
            </h2>
            <p className="mt-1 text-sm text-gray-500">
              {isEditMode
                ? 'Modify which events this endpoint is subscribed to.'
                : 'Subscribe an external endpoint to IAM events.'}
            </p>
          </div>
          <div className="mt-4 flex md:mt-0 md:ml-4">
            <button
              type="button"
              onClick={() => navigate('/webhooks')}
              className="inline-flex items-center px-4 py-2 border border-gray-300 rounded-md shadow-sm text-sm font-medium text-gray-700 bg-white hover:bg-gray-50 focus:outline-none focus:ring-2 focus:ring-offset-2 focus:ring-indigo-500"
            >
              Cancel
            </button>
          </div>
        </div>

        {/* Webhook Form */}
        <div className="mt-6 bg-white shadow sm:rounded-lg">
          <div className="px-4 py-5 sm:p-6">
            {error && (
              <div className="mb-4 bg-red-50 border border-red-200 text-red-700 px-4 py-3 rounded">
                {error}
              </div>
            )}

            <form onSubmit={handleSubmit(onSubmit)} className="space-y-6">
              {/* Name */}
              <div>
                <label htmlFor="name" className="block text-sm font-medium text-gray-700">
                  Name *
                </label>
                <input
                  type="text"
                  id="name"
                  {...register('name', { required: 'Name is required' })}
                  className={`mt-1 block w-full rounded-md shadow-sm sm:text-sm ${
                    errors.name
                      ? 'border-red-300 focus:ring-red-500 focus:border-red-500'
                      : 'border-gray-300 focus:ring-indigo-500 focus:border-indigo-500'
                  }`}
                  placeholder="e.g., SIEM Integration"
                />
                {errors.name && <p className="mt-1 text-sm text-red-600">{errors.name.message}</p>}
              </div>

              {/* URL */}
              <div>
                <label htmlFor="url" className="block text-sm font-medium text-gray-700">
                  Endpoint URL *
                </label>
                <input
                  type="text"
                  id="url"
                  {...register('url', { required: 'URL is required' })}
                  className={`mt-1 block w-full rounded-md shadow-sm sm:text-sm ${
                    errors.url
                      ? 'border-red-300 focus:ring-red-500 focus:border-red-500'
                      : 'border-gray-300 focus:ring-indigo-500 focus:border-indigo-500'
                  }`}
                  placeholder="https://example.com/webhooks/iam"
                />
                {errors.url && <p className="mt-1 text-sm text-red-600">{errors.url.message}</p>}
              </div>

              {/* Tenant (create only) */}
              {!isEditMode && (
                <div>
                  <label htmlFor="tenantId" className="block text-sm font-medium text-gray-700">
                    Tenant *
                  </label>
                  <select
                    id="tenantId"
                    {...register('tenantId', { required: 'Tenant is required' })}
                    className={`mt-1 block w-full rounded-md shadow-sm sm:text-sm ${
                      errors.tenantId
                        ? 'border-red-300 focus:ring-red-500 focus:border-red-500'
                        : 'border-gray-300 focus:ring-indigo-500 focus:border-indigo-500'
                    }`}
                  >
                    <option value="">Select a tenant</option>
                    {tenants.map((tenant) => (
                      <option key={tenant.id} value={tenant.id}>
                        {tenant.name} ({tenant.type})
                      </option>
                    ))}
                  </select>
                  {errors.tenantId && <p className="mt-1 text-sm text-red-600">{errors.tenantId.message}</p>}
                </div>
              )}

              {/* Secret (create only - update endpoint does not support rotating it) */}
              {!isEditMode && (
                <div>
                  <label htmlFor="secret" className="block text-sm font-medium text-gray-700">
                    Signing Secret
                  </label>
                  <input
                    type="text"
                    id="secret"
                    {...register('secret')}
                    className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:ring-indigo-500 focus:border-indigo-500 sm:text-sm font-mono text-xs"
                    placeholder="Optional - used to HMAC-SHA256 sign the X-Webhook-Signature header"
                  />
                  <p className="mt-1 text-xs text-gray-500">
                    If set, every delivery includes an <code>X-Webhook-Signature: sha256=...</code> header the
                    receiver can verify. Leave blank to send unsigned payloads.
                  </p>
                </div>
              )}
              {isEditMode && hasSecret && (
                <p className="text-xs text-gray-500">
                  This webhook has a signing secret configured. Delete and recreate it to change the secret.
                </p>
              )}

              {/* Event Types */}
              <div>
                <label className="block text-sm font-medium text-gray-700 mb-2">Events *</label>
                {Object.keys(eventsByCategory).length === 0 ? (
                  <p className="text-sm text-gray-500">Loading available event types...</p>
                ) : (
                  <div className="space-y-4 border border-gray-200 rounded-md p-4 max-h-80 overflow-y-auto">
                    {Object.entries(eventsByCategory).map(([category, events]) => (
                      <div key={category}>
                        <h4 className="text-xs font-semibold text-gray-500 uppercase tracking-wide mb-1.5">
                          {category}
                        </h4>
                        <div className="grid grid-cols-1 sm:grid-cols-2 gap-1.5">
                          {events.map((evt) => (
                            <label key={evt.name} className="inline-flex items-center gap-2 text-sm text-gray-700">
                              <input
                                type="checkbox"
                                checked={selectedEvents.includes(evt.name)}
                                onChange={() => toggleEvent(evt.name)}
                                className="rounded border-gray-300 text-indigo-600 focus:ring-indigo-500"
                              />
                              <span className="font-mono text-xs">{evt.name}</span>
                            </label>
                          ))}
                        </div>
                      </div>
                    ))}
                  </div>
                )}
                {selectedEvents.length === 0 && (
                  <p className="mt-1 text-sm text-red-600">Select at least one event type.</p>
                )}
              </div>

              {/* Retry / Timeout (create only) */}
              {!isEditMode && (
                <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
                  <div>
                    <label htmlFor="maxRetries" className="block text-sm font-medium text-gray-700">
                      Max Retries
                    </label>
                    <input
                      type="number"
                      id="maxRetries"
                      min={0}
                      max={10}
                      {...register('maxRetries', { valueAsNumber: true })}
                      className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:ring-indigo-500 focus:border-indigo-500 sm:text-sm"
                    />
                    <p className="mt-1 text-xs text-gray-500">Exponential backoff between attempts</p>
                  </div>
                  <div>
                    <label htmlFor="timeoutSeconds" className="block text-sm font-medium text-gray-700">
                      Timeout (seconds)
                    </label>
                    <input
                      type="number"
                      id="timeoutSeconds"
                      min={5}
                      max={120}
                      {...register('timeoutSeconds', { valueAsNumber: true })}
                      className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:ring-indigo-500 focus:border-indigo-500 sm:text-sm"
                    />
                  </div>
                </div>
              )}

              {/* Active Toggle */}
              <div className="flex items-center justify-between py-3 border-t border-gray-200">
                <div>
                  <label className="text-sm font-medium text-gray-700">Webhook Active</label>
                  <p className="text-xs text-gray-500">Inactive webhooks do not receive event deliveries</p>
                </div>
                <Controller
                  name="isActive"
                  control={control}
                  render={({ field }) => (
                    <button
                      type="button"
                      onClick={() => field.onChange(!field.value)}
                      className={`relative inline-flex h-6 w-11 flex-shrink-0 cursor-pointer rounded-full border-2 border-transparent transition-colors duration-200 ease-in-out focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:ring-offset-2 ${
                        field.value ? 'bg-indigo-600' : 'bg-gray-200'
                      }`}
                    >
                      <span
                        className={`pointer-events-none inline-block h-5 w-5 transform rounded-full bg-white shadow ring-0 transition duration-200 ease-in-out ${
                          field.value ? 'translate-x-5' : 'translate-x-0'
                        }`}
                      />
                    </button>
                  )}
                />
              </div>

              {/* Submit Buttons */}
              <div className="flex justify-end space-x-3 pt-4 border-t border-gray-200">
                <button
                  type="button"
                  onClick={() => navigate('/webhooks')}
                  className="px-4 py-2 border border-gray-300 rounded-md shadow-sm text-sm font-medium text-gray-700 bg-white hover:bg-gray-50 focus:outline-none focus:ring-2 focus:ring-offset-2 focus:ring-indigo-500"
                >
                  Cancel
                </button>
                <button
                  type="submit"
                  disabled={saving}
                  className="inline-flex justify-center py-2 px-4 border border-transparent shadow-sm text-sm font-medium rounded-md text-white bg-indigo-600 hover:bg-indigo-700 focus:outline-none focus:ring-2 focus:ring-offset-2 focus:ring-indigo-500 disabled:opacity-50"
                >
                  {saving ? 'Saving...' : isEditMode ? 'Update Webhook' : 'Create Webhook'}
                </button>
              </div>
            </form>
          </div>
        </div>
      </div>
    </DashboardLayout>
  );
}
