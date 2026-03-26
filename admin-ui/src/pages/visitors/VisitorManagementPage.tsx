import { useState, useEffect, useCallback } from 'react';
import DashboardLayout from '../../components/layout/DashboardLayout';
import { api } from '../../services/api';
import { visitorApi, type Visitor, type PreRegisterVisitorDto } from '../../services/visitorApi';

interface Tenant {
  id: string;
  name: string;
  slug: string;
}

type ActiveTab = 'register' | 'visitors' | 'checkin';

export default function VisitorManagementPage() {
  const [tenants, setTenants] = useState<Tenant[]>([]);
  const [selectedTenantId, setSelectedTenantId] = useState<string>('');
  const [visitors, setVisitors] = useState<Visitor[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');
  const [success, setSuccess] = useState('');
  const [activeTab, setActiveTab] = useState<ActiveTab>('visitors');

  // Registration form state
  const [formName, setFormName] = useState('');
  const [formEmail, setFormEmail] = useState('');
  const [formCompany, setFormCompany] = useState('');
  const [formVisitDate, setFormVisitDate] = useState('');
  const [formPurpose, setFormPurpose] = useState('');
  const [submitting, setSubmitting] = useState(false);

  // QR display state
  const [selectedQrToken, setSelectedQrToken] = useState<string | null>(null);
  const [selectedQrVisitorName, setSelectedQrVisitorName] = useState<string>('');

  // Status filter
  const [statusFilter, setStatusFilter] = useState<string>('');

  useEffect(() => {
    loadTenants();
  }, []);

  useEffect(() => {
    if (selectedTenantId) {
      loadVisitors();
    }
  }, [selectedTenantId, statusFilter]);

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

  const loadVisitors = useCallback(async () => {
    try {
      setLoading(true);
      const data = await visitorApi.getVisitors(
        selectedTenantId,
        0,
        50,
        statusFilter || undefined
      );
      setVisitors(data);
    } catch (err: any) {
      setError(err.response?.data?.error || 'Failed to load visitors');
    } finally {
      setLoading(false);
    }
  }, [selectedTenantId, statusFilter]);

  const handlePreRegister = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!selectedTenantId) return;

    setError('');
    setSuccess('');
    setSubmitting(true);

    try {
      const request: PreRegisterVisitorDto = {
        tenantId: selectedTenantId,
        name: formName,
        email: formEmail,
        company: formCompany || undefined,
        visitDate: formVisitDate || new Date().toISOString(),
        purpose: formPurpose || undefined,
      };

      await visitorApi.preRegister(request);
      setSuccess(`Visitor "${formName}" has been pre-registered successfully.`);

      // Clear form
      setFormName('');
      setFormEmail('');
      setFormCompany('');
      setFormVisitDate('');
      setFormPurpose('');

      // Refresh visitor list
      loadVisitors();
    } catch (err: any) {
      setError(err.response?.data?.error || 'Failed to pre-register visitor');
    } finally {
      setSubmitting(false);
    }
  };

  const handleCheckIn = async (visitorId: string) => {
    setError('');
    setSuccess('');
    try {
      const updated = await visitorApi.checkIn(visitorId);
      setSuccess(`${updated.name} has been checked in.`);
      loadVisitors();
    } catch (err: any) {
      setError(err.response?.data?.error || 'Failed to check in visitor');
    }
  };

  const handleCheckOut = async (visitorId: string) => {
    setError('');
    setSuccess('');
    try {
      const updated = await visitorApi.checkOut(visitorId);
      setSuccess(`${updated.name} has been checked out.`);
      loadVisitors();
    } catch (err: any) {
      setError(err.response?.data?.error || 'Failed to check out visitor');
    }
  };

  const handleShowQr = async (visitorId: string, visitorName: string) => {
    try {
      const data = await visitorApi.getQrToken(visitorId);
      setSelectedQrToken(data.qrToken);
      setSelectedQrVisitorName(visitorName);
    } catch (err: any) {
      setError(err.response?.data?.error || 'Failed to get QR code');
    }
  };

  const getStatusBadge = (status: string) => {
    const colors: Record<string, string> = {
      PreRegistered: 'bg-blue-100 text-blue-800',
      CheckedIn: 'bg-green-100 text-green-800',
      CheckedOut: 'bg-gray-100 text-gray-800',
      Cancelled: 'bg-red-100 text-red-800',
      NoShow: 'bg-yellow-100 text-yellow-800',
    };
    return (
      <span
        className={`inline-flex items-center px-2.5 py-0.5 rounded-full text-xs font-medium ${
          colors[status] || 'bg-gray-100 text-gray-800'
        }`}
      >
        {status}
      </span>
    );
  };

  return (
    <DashboardLayout>
      <div className="space-y-6">
        <div className="sm:flex sm:items-center sm:justify-between">
          <div>
            <h1 className="text-2xl font-bold text-gray-900">Visitor Management</h1>
            <p className="mt-1 text-sm text-gray-500">
              Pre-register visitors, manage check-in/out, and generate QR access codes
            </p>
          </div>
          <div className="mt-4 sm:mt-0">
            <select
              value={selectedTenantId}
              onChange={(e) => setSelectedTenantId(e.target.value)}
              className="block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
            >
              <option value="">Select Tenant</option>
              {tenants.map((t) => (
                <option key={t.id} value={t.id}>
                  {t.name}
                </option>
              ))}
            </select>
          </div>
        </div>

        {error && (
          <div className="rounded-md bg-red-50 p-4">
            <p className="text-sm text-red-700">{error}</p>
            <button onClick={() => setError('')} className="mt-1 text-xs text-red-500 underline">
              Dismiss
            </button>
          </div>
        )}

        {success && (
          <div className="rounded-md bg-green-50 p-4">
            <p className="text-sm text-green-700">{success}</p>
            <button
              onClick={() => setSuccess('')}
              className="mt-1 text-xs text-green-500 underline"
            >
              Dismiss
            </button>
          </div>
        )}

        {/* Tabs */}
        <div className="border-b border-gray-200">
          <nav className="-mb-px flex space-x-8">
            {(
              [
                { key: 'visitors', label: 'Visitors' },
                { key: 'register', label: 'Pre-Register' },
                { key: 'checkin', label: 'Check-In / Out' },
              ] as { key: ActiveTab; label: string }[]
            ).map((tab) => (
              <button
                key={tab.key}
                onClick={() => setActiveTab(tab.key)}
                className={`whitespace-nowrap py-4 px-1 border-b-2 font-medium text-sm ${
                  activeTab === tab.key
                    ? 'border-indigo-500 text-indigo-600'
                    : 'border-transparent text-gray-500 hover:text-gray-700 hover:border-gray-300'
                }`}
              >
                {tab.label}
              </button>
            ))}
          </nav>
        </div>

        {/* Visitors Tab - List */}
        {activeTab === 'visitors' && selectedTenantId && (
          <div className="bg-white shadow rounded-lg overflow-hidden">
            <div className="px-6 py-4 border-b border-gray-200 flex justify-between items-center">
              <h2 className="text-lg font-medium text-gray-900">All Visitors</h2>
              <div className="flex items-center space-x-4">
                <select
                  value={statusFilter}
                  onChange={(e) => setStatusFilter(e.target.value)}
                  className="rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                >
                  <option value="">All Statuses</option>
                  <option value="PreRegistered">Pre-Registered</option>
                  <option value="CheckedIn">Checked In</option>
                  <option value="CheckedOut">Checked Out</option>
                  <option value="Cancelled">Cancelled</option>
                  <option value="NoShow">No Show</option>
                </select>
                <button
                  onClick={loadVisitors}
                  disabled={loading}
                  className="text-sm text-indigo-600 hover:text-indigo-800"
                >
                  Refresh
                </button>
              </div>
            </div>

            {loading ? (
              <div className="p-6 text-center text-gray-500">Loading...</div>
            ) : visitors.length === 0 ? (
              <div className="p-6 text-center text-gray-500">No visitors found</div>
            ) : (
              <div className="overflow-x-auto">
                <table className="min-w-full divide-y divide-gray-200">
                  <thead className="bg-gray-50">
                    <tr>
                      <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">
                        Visitor
                      </th>
                      <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">
                        Company
                      </th>
                      <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">
                        Host
                      </th>
                      <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">
                        Visit Date
                      </th>
                      <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">
                        Status
                      </th>
                      <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">
                        Check-In
                      </th>
                      <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">
                        Check-Out
                      </th>
                      <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">
                        Actions
                      </th>
                    </tr>
                  </thead>
                  <tbody className="bg-white divide-y divide-gray-200">
                    {visitors.map((v) => (
                      <tr key={v.id} className="hover:bg-gray-50">
                        <td className="px-6 py-4 whitespace-nowrap">
                          <div className="text-sm font-medium text-gray-900">{v.name}</div>
                          <div className="text-sm text-gray-500">{v.email}</div>
                        </td>
                        <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-500">
                          {v.company || '-'}
                        </td>
                        <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-500">
                          {v.hostUserName || '-'}
                        </td>
                        <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-500">
                          {new Date(v.visitDate).toLocaleDateString()}
                        </td>
                        <td className="px-6 py-4 whitespace-nowrap">{getStatusBadge(v.status)}</td>
                        <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-500">
                          {v.checkInAt ? new Date(v.checkInAt).toLocaleTimeString() : '-'}
                        </td>
                        <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-500">
                          {v.checkOutAt ? new Date(v.checkOutAt).toLocaleTimeString() : '-'}
                        </td>
                        <td className="px-6 py-4 whitespace-nowrap text-sm">
                          <div className="flex space-x-2">
                            <button
                              onClick={() => handleShowQr(v.id, v.name)}
                              className="text-indigo-600 hover:text-indigo-900"
                            >
                              QR
                            </button>
                            {v.status === 'PreRegistered' && (
                              <button
                                onClick={() => handleCheckIn(v.id)}
                                className="text-green-600 hover:text-green-900"
                              >
                                Check In
                              </button>
                            )}
                            {v.status === 'CheckedIn' && (
                              <button
                                onClick={() => handleCheckOut(v.id)}
                                className="text-orange-600 hover:text-orange-900"
                              >
                                Check Out
                              </button>
                            )}
                          </div>
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </div>
        )}

        {/* Pre-Register Tab */}
        {activeTab === 'register' && selectedTenantId && (
          <div className="bg-white shadow rounded-lg p-6">
            <h2 className="text-lg font-medium text-gray-900 mb-4">Pre-Register Visitor</h2>
            <p className="text-sm text-gray-500 mb-6">
              Register a visitor in advance. They will receive a QR code for check-in.
            </p>

            <form onSubmit={handlePreRegister} className="space-y-4">
              <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
                <div>
                  <label className="block text-sm font-medium text-gray-700 mb-1">
                    Full Name *
                  </label>
                  <input
                    type="text"
                    value={formName}
                    onChange={(e) => setFormName(e.target.value)}
                    required
                    className="block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                    placeholder="John Doe"
                  />
                </div>
                <div>
                  <label className="block text-sm font-medium text-gray-700 mb-1">Email *</label>
                  <input
                    type="email"
                    value={formEmail}
                    onChange={(e) => setFormEmail(e.target.value)}
                    required
                    className="block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                    placeholder="visitor@company.com"
                  />
                </div>
                <div>
                  <label className="block text-sm font-medium text-gray-700 mb-1">Company</label>
                  <input
                    type="text"
                    value={formCompany}
                    onChange={(e) => setFormCompany(e.target.value)}
                    className="block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                    placeholder="Acme Corp"
                  />
                </div>
                <div>
                  <label className="block text-sm font-medium text-gray-700 mb-1">
                    Visit Date *
                  </label>
                  <input
                    type="datetime-local"
                    value={formVisitDate}
                    onChange={(e) => setFormVisitDate(e.target.value)}
                    required
                    className="block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                  />
                </div>
              </div>
              <div>
                <label className="block text-sm font-medium text-gray-700 mb-1">
                  Purpose of Visit
                </label>
                <textarea
                  value={formPurpose}
                  onChange={(e) => setFormPurpose(e.target.value)}
                  rows={3}
                  className="block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                  placeholder="Meeting with engineering team about project integration..."
                />
              </div>
              <div className="flex justify-end">
                <button
                  type="submit"
                  disabled={submitting || !formName || !formEmail || !formVisitDate}
                  className="inline-flex items-center px-4 py-2 border border-transparent shadow-sm text-sm font-medium rounded-md text-white bg-indigo-600 hover:bg-indigo-700 disabled:opacity-50"
                >
                  {submitting ? 'Registering...' : 'Pre-Register Visitor'}
                </button>
              </div>
            </form>
          </div>
        )}

        {/* Check-In / Out Tab */}
        {activeTab === 'checkin' && selectedTenantId && (
          <div className="space-y-4">
            <div className="bg-white shadow rounded-lg p-6">
              <h2 className="text-lg font-medium text-gray-900 mb-4">Quick Check-In / Check-Out</h2>
              <p className="text-sm text-gray-500 mb-6">
                Pre-registered visitors awaiting check-in, and checked-in visitors awaiting
                check-out.
              </p>

              {/* Awaiting Check-In */}
              <div className="mb-8">
                <h3 className="text-sm font-semibold text-gray-700 mb-3 uppercase tracking-wider">
                  Awaiting Check-In
                </h3>
                {visitors.filter((v) => v.status === 'PreRegistered').length === 0 ? (
                  <p className="text-sm text-gray-400">No visitors awaiting check-in</p>
                ) : (
                  <div className="space-y-2">
                    {visitors
                      .filter((v) => v.status === 'PreRegistered')
                      .map((v) => (
                        <div
                          key={v.id}
                          className="flex items-center justify-between p-3 bg-blue-50 rounded-lg"
                        >
                          <div>
                            <span className="text-sm font-medium text-gray-900">{v.name}</span>
                            {v.company && (
                              <span className="text-sm text-gray-500 ml-2">({v.company})</span>
                            )}
                            <div className="text-xs text-gray-500">
                              Visit: {new Date(v.visitDate).toLocaleString()}
                              {v.purpose && <> - {v.purpose}</>}
                            </div>
                          </div>
                          <div className="flex space-x-2">
                            <button
                              onClick={() => handleShowQr(v.id, v.name)}
                              className="inline-flex items-center px-3 py-1.5 border border-gray-300 shadow-sm text-xs font-medium rounded-md text-gray-700 bg-white hover:bg-gray-50"
                            >
                              QR Code
                            </button>
                            <button
                              onClick={() => handleCheckIn(v.id)}
                              className="inline-flex items-center px-3 py-1.5 border border-transparent shadow-sm text-xs font-medium rounded-md text-white bg-green-600 hover:bg-green-700"
                            >
                              Check In
                            </button>
                          </div>
                        </div>
                      ))}
                  </div>
                )}
              </div>

              {/* Currently On-Site */}
              <div>
                <h3 className="text-sm font-semibold text-gray-700 mb-3 uppercase tracking-wider">
                  Currently On-Site
                </h3>
                {visitors.filter((v) => v.status === 'CheckedIn').length === 0 ? (
                  <p className="text-sm text-gray-400">No visitors currently on-site</p>
                ) : (
                  <div className="space-y-2">
                    {visitors
                      .filter((v) => v.status === 'CheckedIn')
                      .map((v) => (
                        <div
                          key={v.id}
                          className="flex items-center justify-between p-3 bg-green-50 rounded-lg"
                        >
                          <div>
                            <span className="text-sm font-medium text-gray-900">{v.name}</span>
                            {v.company && (
                              <span className="text-sm text-gray-500 ml-2">({v.company})</span>
                            )}
                            <div className="text-xs text-gray-500">
                              Checked in:{' '}
                              {v.checkInAt ? new Date(v.checkInAt).toLocaleTimeString() : '-'}
                            </div>
                          </div>
                          <button
                            onClick={() => handleCheckOut(v.id)}
                            className="inline-flex items-center px-3 py-1.5 border border-transparent shadow-sm text-xs font-medium rounded-md text-white bg-orange-600 hover:bg-orange-700"
                          >
                            Check Out
                          </button>
                        </div>
                      ))}
                  </div>
                )}
              </div>
            </div>
          </div>
        )}

        {/* QR Code Modal */}
        {selectedQrToken && (
          <div className="fixed inset-0 z-50 flex items-center justify-center bg-black bg-opacity-50">
            <div className="bg-white rounded-lg shadow-xl p-6 max-w-sm w-full mx-4">
              <h3 className="text-lg font-medium text-gray-900 mb-2">
                QR Code - {selectedQrVisitorName}
              </h3>
              <p className="text-sm text-gray-500 mb-4">
                Present this token at the entrance for identification.
              </p>
              <div className="bg-gray-50 rounded-lg p-4 text-center mb-4">
                <p className="text-xs text-gray-500 mb-1">Access Token</p>
                <p className="text-lg font-mono font-bold text-gray-900 break-all">
                  {selectedQrToken}
                </p>
              </div>
              <p className="text-xs text-gray-400 mb-4">
                In production, this token would be rendered as a scannable QR code image.
              </p>
              <div className="flex justify-end">
                <button
                  onClick={() => {
                    setSelectedQrToken(null);
                    setSelectedQrVisitorName('');
                  }}
                  className="inline-flex items-center px-4 py-2 border border-gray-300 shadow-sm text-sm font-medium rounded-md text-gray-700 bg-white hover:bg-gray-50"
                >
                  Close
                </button>
              </div>
            </div>
          </div>
        )}

        {!selectedTenantId && (
          <div className="bg-white shadow rounded-lg p-6 text-center text-gray-500">
            Please select a tenant to manage visitors.
          </div>
        )}
      </div>
    </DashboardLayout>
  );
}
