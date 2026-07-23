import { useState, useEffect, useCallback } from 'react';
import DashboardLayout from '../../components/layout/DashboardLayout';
import { alertsApi } from '../../services/alertsApi';
import type {
  AlertRule,
  SecurityAlert,
  SiemIntegration,
  CreateAlertRuleRequest,
  CreateSiemIntegrationRequest,
} from '../../services/alertsApi';

const SEVERITY_LABELS = ['Info', 'Warning', 'Critical'] as const;
const SEVERITY_COLORS = [
  'bg-blue-100 text-blue-800',
  'bg-yellow-100 text-yellow-800',
  'bg-red-100 text-red-800',
];

const SIEM_TYPES = ['Syslog', 'Webhook', 'Splunk', 'Elastic', 'AzureSentinel'] as const;

type Tab = 'active' | 'rules' | 'history' | 'siem';

export default function SecurityAlertsPage() {
  const [tab, setTab] = useState<Tab>('active');
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');

  // Data
  const [activeAlerts, setActiveAlerts] = useState<SecurityAlert[]>([]);
  const [alertHistory, setAlertHistory] = useState<SecurityAlert[]>([]);
  const [rules, setRules] = useState<AlertRule[]>([]);
  const [siemIntegrations, setSiemIntegrations] = useState<SiemIntegration[]>([]);

  // Modals
  const [showRuleModal, setShowRuleModal] = useState(false);
  const [editingRule, setEditingRule] = useState<AlertRule | null>(null);
  const [showSiemModal, setShowSiemModal] = useState(false);
  const [editingSiem, setEditingSiem] = useState<SiemIntegration | null>(null);

  // Rule form
  const [ruleForm, setRuleForm] = useState<CreateAlertRuleRequest>({
    name: '',
    condition: '{}',
    severity: 1,
    channels: '[]',
    cooldownMinutes: 15,
    autoResponseAction: '',
    isActive: true,
  });

  // SIEM form
  const [siemForm, setSiemForm] = useState<CreateSiemIntegrationRequest>({
    name: '',
    type: 1,
    endpointUrl: '',
    authConfig: '',
    format: 'JSON',
    eventFilter: '',
    isActive: true,
  });

  const loadData = useCallback(async () => {
    try {
      setLoading(true);
      setError('');
      const [alerts, history, ruleList, siemList] = await Promise.all([
        alertsApi.getActiveAlerts(),
        alertsApi.getAlertHistory(),
        alertsApi.getRules(),
        alertsApi.getSiemIntegrations(),
      ]);
      setActiveAlerts(alerts);
      setAlertHistory(history);
      setRules(ruleList);
      setSiemIntegrations(siemList);
    } catch (err: any) {
      setError(err.response?.data?.message || err.message || 'Failed to load data');
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    loadData();
  }, [loadData]);

  // Auto-refresh active alerts every 30s
  useEffect(() => {
    if (tab !== 'active') return;
    const interval = setInterval(async () => {
      try {
        const alerts = await alertsApi.getActiveAlerts();
        setActiveAlerts(alerts);
      } catch { /* silent */ }
    }, 30000);
    return () => clearInterval(interval);
  }, [tab]);

  const handleAcknowledge = async (id: string) => {
    try {
      await alertsApi.acknowledgeAlert(id);
      setActiveAlerts((prev) => prev.filter((a) => a.id !== id));
    } catch (err: any) {
      setError(err.message || 'Failed to acknowledge alert');
    }
  };

  // ── Rule CRUD ──────────────────────────────────────────────
  const openRuleModal = (rule?: AlertRule) => {
    if (rule) {
      setEditingRule(rule);
      setRuleForm({
        name: rule.name,
        condition: rule.condition,
        severity: rule.severity,
        channels: rule.channels,
        cooldownMinutes: rule.cooldownMinutes,
        autoResponseAction: rule.autoResponseAction || '',
        isActive: rule.isActive,
      });
    } else {
      setEditingRule(null);
      setRuleForm({
        name: '',
        condition: '{"type": "brute_force", "threshold": 5, "windowMinutes": 5}',
        severity: 1,
        channels: '[]',
        cooldownMinutes: 15,
        autoResponseAction: '',
        isActive: true,
      });
    }
    setShowRuleModal(true);
  };

  const saveRule = async () => {
    try {
      if (editingRule) {
        await alertsApi.updateRule(editingRule.id, ruleForm);
      } else {
        await alertsApi.createRule(ruleForm);
      }
      setShowRuleModal(false);
      await loadData();
    } catch (err: any) {
      setError(err.message || 'Failed to save rule');
    }
  };

  const deleteRule = async (id: string) => {
    if (!confirm('Delete this alert rule?')) return;
    try {
      await alertsApi.deleteRule(id);
      await loadData();
    } catch (err: any) {
      setError(err.message || 'Failed to delete rule');
    }
  };

  // ── SIEM CRUD ─────────────────────────────────────────────
  const openSiemModal = (siem?: SiemIntegration) => {
    if (siem) {
      setEditingSiem(siem);
      setSiemForm({
        name: siem.name,
        type: siem.type,
        endpointUrl: siem.endpointUrl,
        authConfig: siem.authConfig || '',
        format: siem.format,
        eventFilter: siem.eventFilter || '',
        isActive: siem.isActive,
      });
    } else {
      setEditingSiem(null);
      setSiemForm({
        name: '',
        type: 1,
        endpointUrl: '',
        authConfig: '',
        format: 'JSON',
        eventFilter: '',
        isActive: true,
      });
    }
    setShowSiemModal(true);
  };

  const saveSiem = async () => {
    try {
      if (editingSiem) {
        await alertsApi.updateSiemIntegration(editingSiem.id, siemForm);
      } else {
        await alertsApi.createSiemIntegration(siemForm);
      }
      setShowSiemModal(false);
      await loadData();
    } catch (err: any) {
      setError(err.message || 'Failed to save SIEM integration');
    }
  };

  const deleteSiem = async (id: string) => {
    if (!confirm('Delete this SIEM integration?')) return;
    try {
      await alertsApi.deleteSiemIntegration(id);
      await loadData();
    } catch (err: any) {
      setError(err.message || 'Failed to delete SIEM integration');
    }
  };

  const parseDetails = (json?: string): Record<string, any> | null => {
    if (!json) return null;
    try { return JSON.parse(json); } catch { return null; }
  };

  const formatDate = (d: string) => new Date(d).toLocaleString();

  return (
    <DashboardLayout>
      <div className="space-y-6">
        {/* Header */}
        <div className="flex justify-between items-center">
          <div>
            <h1 className="text-2xl font-bold text-gray-900">Security Alerts & SIEM</h1>
            <p className="mt-1 text-sm text-gray-500">
              Monitor security events, configure alert rules, and manage SIEM integrations
            </p>
          </div>
          {activeAlerts.length > 0 && (
            <span className="inline-flex items-center px-3 py-1 rounded-full text-sm font-medium bg-red-100 text-red-800">
              {activeAlerts.length} active alert{activeAlerts.length !== 1 ? 's' : ''}
            </span>
          )}
        </div>

        {error && (
          <div className="rounded-md bg-red-50 p-4">
            <p className="text-sm text-red-800">{error}</p>
          </div>
        )}

        {/* Tabs */}
        <div className="border-b border-gray-200">
          <nav className="-mb-px flex space-x-8">
            {([
              ['active', `Active Alerts (${activeAlerts.length})`],
              ['rules', 'Alert Rules'],
              ['history', 'History'],
              ['siem', 'SIEM Integrations'],
            ] as [Tab, string][]).map(([key, label]) => (
              <button
                key={key}
                onClick={() => setTab(key)}
                className={`py-4 px-1 border-b-2 font-medium text-sm ${
                  tab === key
                    ? 'border-indigo-500 text-indigo-600'
                    : 'border-transparent text-gray-500 hover:text-gray-700 hover:border-gray-300'
                }`}
              >
                {label}
              </button>
            ))}
          </nav>
        </div>

        {loading ? (
          <div className="text-center py-12">
            <div className="animate-spin rounded-full h-8 w-8 border-b-2 border-indigo-600 mx-auto" />
            <p className="mt-2 text-sm text-gray-500">Loading...</p>
          </div>
        ) : (
          <>
            {/* ── Active Alerts Tab ──────────────────────── */}
            {tab === 'active' && (
              <div className="space-y-4">
                {activeAlerts.length === 0 ? (
                  <div className="text-center py-12 bg-white rounded-lg shadow">
                    <svg className="mx-auto h-12 w-12 text-green-400" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 12l2 2 4-4m6 2a9 9 0 11-18 0 9 9 0 0118 0z" />
                    </svg>
                    <h3 className="mt-2 text-sm font-medium text-gray-900">No active alerts</h3>
                    <p className="mt-1 text-sm text-gray-500">All security events are under control.</p>
                  </div>
                ) : (
                  activeAlerts.map((alert) => {
                    const details = parseDetails(alert.details);
                    return (
                      <div key={alert.id} className="bg-white shadow rounded-lg p-6">
                        <div className="flex items-start justify-between">
                          <div className="flex items-start space-x-3">
                            <span className={`inline-flex items-center px-2.5 py-0.5 rounded-full text-xs font-medium ${SEVERITY_COLORS[alert.severity]}`}>
                              {SEVERITY_LABELS[alert.severity]}
                            </span>
                            <div>
                              <h3 className="text-sm font-medium text-gray-900">{alert.title}</h3>
                              <p className="mt-1 text-xs text-gray-500">{formatDate(alert.createdAt)}</p>
                              {alert.rule && (
                                <p className="mt-1 text-xs text-gray-400">Rule: {alert.rule.name}</p>
                              )}
                              {alert.autoResponseAction && (
                                <p className="mt-1 text-xs text-orange-600">
                                  Auto-response: {alert.autoResponseAction.replace('_', ' ')}
                                </p>
                              )}
                            </div>
                          </div>
                          <button
                            onClick={() => handleAcknowledge(alert.id)}
                            className="inline-flex items-center px-3 py-1.5 border border-gray-300 text-xs font-medium rounded-md text-gray-700 bg-white hover:bg-gray-50"
                          >
                            Acknowledge
                          </button>
                        </div>
                        {details && (
                          <div className="mt-3 bg-gray-50 rounded p-3">
                            <pre className="text-xs text-gray-600 whitespace-pre-wrap overflow-x-auto">
                              {JSON.stringify(details, null, 2)}
                            </pre>
                          </div>
                        )}
                      </div>
                    );
                  })
                )}
              </div>
            )}

            {/* ── Alert Rules Tab ────────────────────────── */}
            {tab === 'rules' && (
              <div>
                <div className="flex justify-end mb-4">
                  <button
                    onClick={() => openRuleModal()}
                    className="inline-flex items-center px-4 py-2 border border-transparent text-sm font-medium rounded-md text-white bg-indigo-600 hover:bg-indigo-700"
                  >
                    Create Rule
                  </button>
                </div>
                <div className="bg-white shadow overflow-hidden rounded-lg">
                  <table className="min-w-full divide-y divide-gray-200">
                    <thead className="bg-gray-50">
                      <tr>
                        <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">Name</th>
                        <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">Severity</th>
                        <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">Type</th>
                        <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">Auto-Response</th>
                        <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">Cooldown</th>
                        <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">Status</th>
                        <th className="px-6 py-3 text-right text-xs font-medium text-gray-500 uppercase tracking-wider">Actions</th>
                      </tr>
                    </thead>
                    <tbody className="bg-white divide-y divide-gray-200">
                      {rules.map((rule) => {
                        let condType = '';
                        try { condType = JSON.parse(rule.condition)?.type || ''; } catch { /* */ }
                        return (
                          <tr key={rule.id}>
                            <td className="px-6 py-4 whitespace-nowrap text-sm font-medium text-gray-900">{rule.name}</td>
                            <td className="px-6 py-4 whitespace-nowrap">
                              <span className={`inline-flex items-center px-2.5 py-0.5 rounded-full text-xs font-medium ${SEVERITY_COLORS[rule.severity]}`}>
                                {SEVERITY_LABELS[rule.severity]}
                              </span>
                            </td>
                            <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-500">{condType || '-'}</td>
                            <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-500">
                              {rule.autoResponseAction?.replace('_', ' ') || '-'}
                            </td>
                            <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-500">{rule.cooldownMinutes}min</td>
                            <td className="px-6 py-4 whitespace-nowrap">
                              <span className={`inline-flex items-center px-2.5 py-0.5 rounded-full text-xs font-medium ${rule.isActive ? 'bg-green-100 text-green-800' : 'bg-gray-100 text-gray-800'}`}>
                                {rule.isActive ? 'Active' : 'Disabled'}
                              </span>
                            </td>
                            <td className="px-6 py-4 whitespace-nowrap text-right text-sm">
                              <button onClick={() => openRuleModal(rule)} className="text-indigo-600 hover:text-indigo-900 mr-3">Edit</button>
                              <button onClick={() => deleteRule(rule.id)} className="text-red-600 hover:text-red-900">Delete</button>
                            </td>
                          </tr>
                        );
                      })}
                      {rules.length === 0 && (
                        <tr>
                          <td colSpan={7} className="px-6 py-8 text-center text-sm text-gray-500">
                            No alert rules configured. Create one to start monitoring.
                          </td>
                        </tr>
                      )}
                    </tbody>
                  </table>
                </div>
              </div>
            )}

            {/* ── History Tab ────────────────────────────── */}
            {tab === 'history' && (
              <div className="bg-white shadow overflow-hidden rounded-lg">
                <table className="min-w-full divide-y divide-gray-200">
                  <thead className="bg-gray-50">
                    <tr>
                      <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">Time</th>
                      <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">Severity</th>
                      <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">Title</th>
                      <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">Rule</th>
                      <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">Status</th>
                      <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">Acknowledged By</th>
                    </tr>
                  </thead>
                  <tbody className="bg-white divide-y divide-gray-200">
                    {alertHistory.map((alert) => (
                      <tr key={alert.id}>
                        <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-500">{formatDate(alert.createdAt)}</td>
                        <td className="px-6 py-4 whitespace-nowrap">
                          <span className={`inline-flex items-center px-2.5 py-0.5 rounded-full text-xs font-medium ${SEVERITY_COLORS[alert.severity]}`}>
                            {SEVERITY_LABELS[alert.severity]}
                          </span>
                        </td>
                        <td className="px-6 py-4 text-sm text-gray-900 max-w-xs truncate">{alert.title}</td>
                        <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-500">{alert.rule?.name || '-'}</td>
                        <td className="px-6 py-4 whitespace-nowrap">
                          <span className={`inline-flex items-center px-2.5 py-0.5 rounded-full text-xs font-medium ${alert.acknowledgedAt ? 'bg-green-100 text-green-800' : 'bg-red-100 text-red-800'}`}>
                            {alert.acknowledgedAt ? 'Acknowledged' : 'Open'}
                          </span>
                        </td>
                        <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-500">
                          {alert.acknowledgedByUser
                            ? `${alert.acknowledgedByUser.firstName} ${alert.acknowledgedByUser.lastName}`
                            : alert.acknowledgedAt ? 'System' : '-'}
                        </td>
                      </tr>
                    ))}
                    {alertHistory.length === 0 && (
                      <tr>
                        <td colSpan={6} className="px-6 py-8 text-center text-sm text-gray-500">
                          No alert history yet.
                        </td>
                      </tr>
                    )}
                  </tbody>
                </table>
              </div>
            )}

            {/* ── SIEM Tab ───────────────────────────────── */}
            {tab === 'siem' && (
              <div>
                <div className="flex justify-end mb-4">
                  <button
                    onClick={() => openSiemModal()}
                    className="inline-flex items-center px-4 py-2 border border-transparent text-sm font-medium rounded-md text-white bg-indigo-600 hover:bg-indigo-700"
                  >
                    Add SIEM Integration
                  </button>
                </div>
                <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
                  {siemIntegrations.map((siem) => (
                    <div key={siem.id} className="bg-white shadow rounded-lg p-6">
                      <div className="flex items-start justify-between">
                        <div>
                          <h3 className="text-sm font-medium text-gray-900">{siem.name}</h3>
                          <p className="mt-1 text-xs text-gray-500">{SIEM_TYPES[siem.type]}</p>
                        </div>
                        <span className={`inline-flex items-center px-2.5 py-0.5 rounded-full text-xs font-medium ${siem.isActive ? 'bg-green-100 text-green-800' : 'bg-gray-100 text-gray-800'}`}>
                          {siem.isActive ? 'Active' : 'Disabled'}
                        </span>
                      </div>
                      <div className="mt-3 space-y-1">
                        <p className="text-xs text-gray-500">Endpoint: <span className="font-mono">{siem.endpointUrl}</span></p>
                        <p className="text-xs text-gray-500">Format: {siem.format}</p>
                        <p className="text-xs text-gray-400">Created: {formatDate(siem.createdAt)}</p>
                      </div>
                      <div className="mt-4 flex space-x-3">
                        <button onClick={() => openSiemModal(siem)} className="text-sm text-indigo-600 hover:text-indigo-900">Edit</button>
                        <button onClick={() => deleteSiem(siem.id)} className="text-sm text-red-600 hover:text-red-900">Delete</button>
                      </div>
                    </div>
                  ))}
                  {siemIntegrations.length === 0 && (
                    <div className="col-span-2 text-center py-12 bg-white rounded-lg shadow">
                      <h3 className="text-sm font-medium text-gray-900">No SIEM integrations</h3>
                      <p className="mt-1 text-sm text-gray-500">Add an integration to export security events to your SIEM platform.</p>
                    </div>
                  )}
                </div>
              </div>
            )}
          </>
        )}

        {/* ── Rule Modal ─────────────────────────────────── */}
        {showRuleModal && (
          <div className="fixed inset-0 z-50 overflow-y-auto">
            <div className="flex min-h-screen items-center justify-center p-4">
              <div className="fixed inset-0 bg-gray-500 bg-opacity-75" onClick={() => setShowRuleModal(false)} />
              <div className="relative bg-white rounded-lg shadow-xl max-w-lg w-full p-6">
                <h3 className="text-lg font-medium text-gray-900 mb-4">
                  {editingRule ? 'Edit Alert Rule' : 'Create Alert Rule'}
                </h3>
                <div className="space-y-4">
                  <div>
                    <label className="block text-sm font-medium text-gray-700">Name</label>
                    <input
                      type="text"
                      value={ruleForm.name}
                      onChange={(e) => setRuleForm({ ...ruleForm, name: e.target.value })}
                      className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                      placeholder="Brute Force Detection"
                    />
                  </div>
                  <div>
                    <label className="block text-sm font-medium text-gray-700">Condition (JSON)</label>
                    <textarea
                      value={ruleForm.condition}
                      onChange={(e) => setRuleForm({ ...ruleForm, condition: e.target.value })}
                      rows={3}
                      className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm font-mono"
                      placeholder='{"type": "brute_force", "threshold": 5, "windowMinutes": 5}'
                    />
                    <p className="mt-1 text-xs text-gray-400">Types: brute_force, impossible_travel, privilege_escalation, mass_deletion</p>
                  </div>
                  <div className="grid grid-cols-2 gap-4">
                    <div>
                      <label className="block text-sm font-medium text-gray-700">Severity</label>
                      <select
                        value={ruleForm.severity}
                        onChange={(e) => setRuleForm({ ...ruleForm, severity: Number(e.target.value) })}
                        className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                      >
                        <option value={0}>Info</option>
                        <option value={1}>Warning</option>
                        <option value={2}>Critical</option>
                      </select>
                    </div>
                    <div>
                      <label className="block text-sm font-medium text-gray-700">Cooldown (min)</label>
                      <input
                        type="number"
                        value={ruleForm.cooldownMinutes}
                        onChange={(e) => setRuleForm({ ...ruleForm, cooldownMinutes: Number(e.target.value) })}
                        className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                      />
                    </div>
                  </div>
                  <div>
                    <label className="block text-sm font-medium text-gray-700">Channels (JSON)</label>
                    <textarea
                      value={ruleForm.channels}
                      onChange={(e) => setRuleForm({ ...ruleForm, channels: e.target.value })}
                      rows={2}
                      className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm font-mono"
                      placeholder='[{"type": "email", "target": "admin@example.com"}]'
                    />
                    <p className="mt-1 text-xs text-gray-400">Channel types: email, webhook, slack</p>
                  </div>
                  <div>
                    <label className="block text-sm font-medium text-gray-700">Auto-Response</label>
                    <select
                      value={ruleForm.autoResponseAction || ''}
                      onChange={(e) => setRuleForm({ ...ruleForm, autoResponseAction: e.target.value || undefined })}
                      className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                    >
                      <option value="">None</option>
                      <option value="lock_account">Lock Account</option>
                      <option value="force_mfa">Force MFA</option>
                      <option value="kill_sessions">Kill Sessions</option>
                    </select>
                  </div>
                  <div className="flex items-center">
                    <input
                      type="checkbox"
                      checked={ruleForm.isActive}
                      onChange={(e) => setRuleForm({ ...ruleForm, isActive: e.target.checked })}
                      className="h-4 w-4 text-indigo-600 focus:ring-indigo-500 border-gray-300 rounded"
                    />
                    <label className="ml-2 block text-sm text-gray-900">Active</label>
                  </div>
                </div>
                <div className="mt-6 flex justify-end space-x-3">
                  <button
                    onClick={() => setShowRuleModal(false)}
                    className="px-4 py-2 text-sm font-medium text-gray-700 bg-white border border-gray-300 rounded-md hover:bg-gray-50"
                  >
                    Cancel
                  </button>
                  <button
                    onClick={saveRule}
                    className="px-4 py-2 text-sm font-medium text-white bg-indigo-600 border border-transparent rounded-md hover:bg-indigo-700"
                  >
                    {editingRule ? 'Update' : 'Create'}
                  </button>
                </div>
              </div>
            </div>
          </div>
        )}

        {/* ── SIEM Modal ─────────────────────────────────── */}
        {showSiemModal && (
          <div className="fixed inset-0 z-50 overflow-y-auto">
            <div className="flex min-h-screen items-center justify-center p-4">
              <div className="fixed inset-0 bg-gray-500 bg-opacity-75" onClick={() => setShowSiemModal(false)} />
              <div className="relative bg-white rounded-lg shadow-xl max-w-lg w-full p-6">
                <h3 className="text-lg font-medium text-gray-900 mb-4">
                  {editingSiem ? 'Edit SIEM Integration' : 'Add SIEM Integration'}
                </h3>
                <div className="space-y-4">
                  <div>
                    <label className="block text-sm font-medium text-gray-700">Name</label>
                    <input
                      type="text"
                      value={siemForm.name}
                      onChange={(e) => setSiemForm({ ...siemForm, name: e.target.value })}
                      className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                      placeholder="Production Splunk"
                    />
                  </div>
                  <div>
                    <label className="block text-sm font-medium text-gray-700">Type</label>
                    <select
                      value={siemForm.type}
                      onChange={(e) => setSiemForm({ ...siemForm, type: Number(e.target.value) })}
                      className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                    >
                      {SIEM_TYPES.map((t, i) => (
                        <option key={t} value={i}>{t}</option>
                      ))}
                    </select>
                  </div>
                  <div>
                    <label className="block text-sm font-medium text-gray-700">Endpoint URL</label>
                    <input
                      type="url"
                      value={siemForm.endpointUrl}
                      onChange={(e) => setSiemForm({ ...siemForm, endpointUrl: e.target.value })}
                      className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                      placeholder="https://splunk.example.com:8088/services/collector"
                    />
                  </div>
                  <div>
                    <label className="block text-sm font-medium text-gray-700">Auth Config (JSON)</label>
                    <textarea
                      value={siemForm.authConfig}
                      onChange={(e) => setSiemForm({ ...siemForm, authConfig: e.target.value })}
                      rows={2}
                      className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm font-mono"
                      placeholder='{"apiKey": "...", "hecToken": "..."}'
                    />
                  </div>
                  <div className="grid grid-cols-2 gap-4">
                    <div>
                      <label className="block text-sm font-medium text-gray-700">Format</label>
                      <select
                        value={siemForm.format}
                        onChange={(e) => setSiemForm({ ...siemForm, format: e.target.value })}
                        className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                      >
                        <option value="JSON">JSON</option>
                        <option value="CEF">CEF</option>
                        <option value="LEEF">LEEF</option>
                      </select>
                    </div>
                    <div className="flex items-end">
                      <div className="flex items-center">
                        <input
                          type="checkbox"
                          checked={siemForm.isActive}
                          onChange={(e) => setSiemForm({ ...siemForm, isActive: e.target.checked })}
                          className="h-4 w-4 text-indigo-600 focus:ring-indigo-500 border-gray-300 rounded"
                        />
                        <label className="ml-2 block text-sm text-gray-900">Active</label>
                      </div>
                    </div>
                  </div>
                  <div>
                    <label className="block text-sm font-medium text-gray-700">Event Filter (JSON, optional)</label>
                    <input
                      type="text"
                      value={siemForm.eventFilter}
                      onChange={(e) => setSiemForm({ ...siemForm, eventFilter: e.target.value })}
                      className="mt-1 block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm font-mono"
                      placeholder='["security.alert", "auth.login_failed"]'
                    />
                    <p className="mt-1 text-xs text-gray-400">Leave empty for all events</p>
                  </div>
                </div>
                <div className="mt-6 flex justify-end space-x-3">
                  <button
                    onClick={() => setShowSiemModal(false)}
                    className="px-4 py-2 text-sm font-medium text-gray-700 bg-white border border-gray-300 rounded-md hover:bg-gray-50"
                  >
                    Cancel
                  </button>
                  <button
                    onClick={saveSiem}
                    className="px-4 py-2 text-sm font-medium text-white bg-indigo-600 border border-transparent rounded-md hover:bg-indigo-700"
                  >
                    {editingSiem ? 'Update' : 'Create'}
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
