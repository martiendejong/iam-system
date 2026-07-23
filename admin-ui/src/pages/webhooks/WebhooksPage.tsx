import { useState, useEffect, useCallback } from 'react';
import { Link } from 'react-router-dom';
import DashboardLayout from '../../components/layout/DashboardLayout';
import { webhookApi } from '../../services/webhookApi';
import { api } from '../../services/api';
import type { WebhookSubscription } from '../../types/webhooks';

export default function WebhooksPage() {
  const [webhooks, setWebhooks] = useState<WebhookSubscription[]>([]);
  const [tenants, setTenants] = useState<any[]>([]);
  const [tenantId, setTenantId] = useState('');
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [deletingId, setDeletingId] = useState<string | null>(null);
  const [testingId, setTestingId] = useState<string | null>(null);
  const [testResult, setTestResult] = useState<{ id: string; success: boolean; message: string } | null>(null);

  useEffect(() => {
    loadTenants();
  }, []);

  useEffect(() => {
    if (tenantId) {
      loadWebhooks(tenantId);
    }
  }, [tenantId]);

  const loadTenants = async () => {
    try {
      const data = await api.getTenants();
      setTenants(data);
      if (data.length > 0) {
        setTenantId(data[0].id);
      } else {
        setLoading(false);
      }
    } catch (err: any) {
      setError(err.response?.data?.error || 'Failed to load tenants');
      setLoading(false);
    }
  };

  const loadWebhooks = useCallback(async (forTenantId: string) => {
    try {
      setLoading(true);
      const data = await webhookApi.getWebhooks(forTenantId);
      setWebhooks(data);
    } catch (err: any) {
      setError(err.response?.data?.error || 'Failed to load webhooks');
    } finally {
      setLoading(false);
    }
  }, []);

  const handleDelete = async (id: string) => {
    if (!window.confirm('Are you sure you want to delete this webhook subscription? This action cannot be undone.')) {
      return;
    }
    try {
      setDeletingId(id);
      await webhookApi.deleteWebhook(id);
      setWebhooks((prev) => prev.filter((w) => w.id !== id));
    } catch (err: any) {
      alert(err.response?.data?.error || 'Failed to delete webhook');
    } finally {
      setDeletingId(null);
    }
  };

  const handleTest = async (id: string) => {
    try {
      setTestingId(id);
      setTestResult(null);
      const delivery = await webhookApi.testWebhook(id);
      setTestResult({
        id,
        success: delivery.success,
        message: delivery.success
          ? `Delivered successfully (HTTP ${delivery.httpStatusCode}, ${Math.round(delivery.durationMs)}ms)`
          : `Delivery failed: ${delivery.error || `HTTP ${delivery.httpStatusCode}`}`,
      });
      if (tenantId) {
        loadWebhooks(tenantId);
      }
    } catch (err: any) {
      setTestResult({ id, success: false, message: err.response?.data?.error || 'Test delivery failed' });
    } finally {
      setTestingId(null);
    }
  };

  return (
    <DashboardLayout>
      <div className="px-4 sm:px-6 lg:px-8">
        {/* Header */}
        <div className="sm:flex sm:items-center">
          <div className="sm:flex-auto">
            <h1 className="text-2xl font-semibold text-gray-900">Webhooks</h1>
            <p className="mt-2 text-sm text-gray-700">
              Notify external services of IAM events (logins, device activity, policy decisions, and more) via signed HTTP callbacks.
            </p>
          </div>
          <div className="mt-4 sm:mt-0 sm:ml-16 sm:flex-none">
            <Link
              to="/webhooks/new"
              className="inline-flex items-center justify-center rounded-md border border-transparent bg-indigo-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-indigo-700 focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:ring-offset-2"
            >
              Create Webhook
            </Link>
          </div>
        </div>

        {/* Tenant Filter */}
        <div className="mt-6 max-w-xs">
          <label htmlFor="tenantFilter" className="block text-sm font-medium text-gray-700">
            Tenant
          </label>
          <select
            id="tenantFilter"
            value={tenantId}
            onChange={(e) => setTenantId(e.target.value)}
            className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
          >
            {tenants.map((tenant) => (
              <option key={tenant.id} value={tenant.id}>
                {tenant.name} ({tenant.type})
              </option>
            ))}
          </select>
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
            <p className="mt-2 text-sm text-gray-500">Loading webhooks...</p>
          </div>
        ) : (
          <div className="mt-8 flex flex-col">
            <div className="-my-2 -mx-4 overflow-x-auto sm:-mx-6 lg:-mx-8">
              <div className="inline-block min-w-full py-2 align-middle md:px-6 lg:px-8">
                <div className="overflow-hidden shadow ring-1 ring-black ring-opacity-5 md:rounded-lg">
                  <table className="min-w-full divide-y divide-gray-300">
                    <thead className="bg-gray-50">
                      <tr>
                        <th className="px-3 py-3.5 text-left text-sm font-semibold text-gray-900">Name</th>
                        <th className="px-3 py-3.5 text-left text-sm font-semibold text-gray-900">URL</th>
                        <th className="px-3 py-3.5 text-left text-sm font-semibold text-gray-900">Events</th>
                        <th className="px-3 py-3.5 text-left text-sm font-semibold text-gray-900">Status</th>
                        <th className="px-3 py-3.5 text-left text-sm font-semibold text-gray-900">Last Delivery</th>
                        <th className="relative py-3.5 pl-3 pr-4 sm:pr-6">
                          <span className="sr-only">Actions</span>
                        </th>
                      </tr>
                    </thead>
                    <tbody className="divide-y divide-gray-200 bg-white">
                      {webhooks.length === 0 ? (
                        <tr>
                          <td colSpan={6} className="px-3 py-8 text-sm text-gray-500 text-center">
                            No webhooks configured for this tenant. Create one to get started.
                          </td>
                        </tr>
                      ) : (
                        webhooks.map((webhook) => (
                          <tr key={webhook.id} className={!webhook.isActive ? 'bg-gray-50 opacity-60' : ''}>
                            <td className="whitespace-nowrap px-3 py-4 text-sm">
                              <div className="font-medium text-gray-900">{webhook.name}</div>
                              {webhook.hasSecret && (
                                <div className="text-xs text-gray-500">HMAC signing enabled</div>
                              )}
                            </td>
                            <td className="px-3 py-4 text-sm text-gray-500 font-mono text-xs max-w-xs truncate">
                              {webhook.url}
                            </td>
                            <td className="px-3 py-4 text-sm">
                              <div className="flex flex-wrap gap-1 max-w-xs">
                                {webhook.events.slice(0, 3).map((evt) => (
                                  <span
                                    key={evt}
                                    className="inline-flex items-center px-2 py-0.5 rounded text-xs font-medium bg-indigo-100 text-indigo-800"
                                  >
                                    {evt}
                                  </span>
                                ))}
                                {webhook.events.length > 3 && (
                                  <span className="inline-flex items-center px-2 py-0.5 rounded text-xs font-medium bg-gray-100 text-gray-600">
                                    +{webhook.events.length - 3} more
                                  </span>
                                )}
                              </div>
                            </td>
                            <td className="whitespace-nowrap px-3 py-4 text-sm">
                              <span
                                className={`inline-flex rounded-full px-2 text-xs font-semibold leading-5 ${
                                  webhook.isActive ? 'bg-green-100 text-green-800' : 'bg-red-100 text-red-800'
                                }`}
                              >
                                {webhook.isActive ? 'Active' : 'Inactive'}
                              </span>
                              <div className="mt-1 text-xs text-gray-500">
                                {webhook.successfulDeliveries}/{webhook.totalDeliveries} succeeded
                              </div>
                            </td>
                            <td className="whitespace-nowrap px-3 py-4 text-sm text-gray-500">
                              {webhook.lastDeliveryAt ? new Date(webhook.lastDeliveryAt).toLocaleString() : 'Never'}
                              {testResult && testResult.id === webhook.id && (
                                <div className={`mt-1 text-xs ${testResult.success ? 'text-green-600' : 'text-red-600'}`}>
                                  {testResult.message}
                                </div>
                              )}
                            </td>
                            <td className="relative whitespace-nowrap py-4 pl-3 pr-4 text-right text-sm font-medium sm:pr-6">
                              <button
                                onClick={() => handleTest(webhook.id)}
                                disabled={testingId === webhook.id}
                                className="text-gray-600 hover:text-gray-900 disabled:opacity-50 mr-3"
                              >
                                {testingId === webhook.id ? 'Testing...' : 'Test'}
                              </button>
                              <Link
                                to={`/webhooks/${webhook.id}`}
                                className="text-indigo-600 hover:text-indigo-900 mr-3"
                              >
                                Edit
                              </Link>
                              <button
                                onClick={() => handleDelete(webhook.id)}
                                disabled={deletingId === webhook.id}
                                className="text-red-600 hover:text-red-900 disabled:opacity-50"
                              >
                                {deletingId === webhook.id ? 'Deleting...' : 'Delete'}
                              </button>
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
      </div>
    </DashboardLayout>
  );
}
