import { useState, useEffect, useCallback } from 'react';
import DashboardLayout from '../../components/layout/DashboardLayout';
import { riskApi } from '../../services/riskApi';
import type {
  RiskDashboardData,
  LoginRiskScore,
  RiskThreshold,
  TrustedDevice,
} from '../../services/riskApi';

const ACTION_LABELS: Record<number, string> = { 0: 'Allow', 1: 'Step-Up', 2: 'Block' };
const ACTION_COLORS: Record<number, string> = {
  0: 'bg-green-100 text-green-800',
  1: 'bg-yellow-100 text-yellow-800',
  2: 'bg-red-100 text-red-800',
};

const DAY_LABELS = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'];

function riskColor(score: number): string {
  if (score <= 20) return 'bg-green-200';
  if (score <= 40) return 'bg-yellow-200';
  if (score <= 60) return 'bg-orange-200';
  if (score <= 80) return 'bg-red-200';
  return 'bg-red-400';
}

function riskTextColor(score: number): string {
  if (score <= 20) return 'text-green-700';
  if (score <= 50) return 'text-yellow-700';
  if (score <= 75) return 'text-orange-700';
  return 'text-red-700';
}

// ===========================
// Heatmap Component
// ===========================
function RiskHeatmap({ data }: { data: RiskDashboardData['heatmap'] }) {
  // Build 7x24 grid
  const grid: Record<string, { count: number; avg: number }> = {};
  data.forEach((cell) => {
    grid[`${cell.dayOfWeek}-${cell.hour}`] = { count: cell.count, avg: cell.avgRiskScore };
  });

  const maxCount = Math.max(1, ...data.map((c) => c.count));

  return (
    <div className="overflow-x-auto">
      <table className="w-full text-xs">
        <thead>
          <tr>
            <th className="text-left pr-2 text-gray-500 font-normal">Day</th>
            {Array.from({ length: 24 }, (_, h) => (
              <th key={h} className="text-center text-gray-400 font-normal px-0.5">
                {h}
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {DAY_LABELS.map((label, day) => (
            <tr key={day}>
              <td className="pr-2 text-gray-600 font-medium">{label}</td>
              {Array.from({ length: 24 }, (_, hour) => {
                const cell = grid[`${day}-${hour}`];
                const opacity = cell ? Math.max(0.1, cell.count / maxCount) : 0;
                const avgScore = cell?.avg ?? 0;
                const bg =
                  avgScore > 60
                    ? `rgba(239, 68, 68, ${opacity})`
                    : avgScore > 30
                    ? `rgba(245, 158, 11, ${opacity})`
                    : `rgba(34, 197, 94, ${opacity})`;

                return (
                  <td
                    key={hour}
                    className="text-center px-0.5 py-0.5"
                    title={cell ? `${cell.count} logins, avg risk: ${cell.avg.toFixed(1)}` : 'No data'}
                  >
                    <div
                      className="w-5 h-5 rounded-sm mx-auto"
                      style={{ backgroundColor: cell ? bg : '#f3f4f6' }}
                    />
                  </td>
                );
              })}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

// ===========================
// Distribution Bar Chart
// ===========================
function DistributionChart({ data }: { data: RiskDashboardData['distribution'] }) {
  const maxCount = Math.max(1, ...data.map((d) => d.count));
  const colors = ['bg-green-500', 'bg-yellow-500', 'bg-orange-500', 'bg-red-400', 'bg-red-600'];

  return (
    <div className="space-y-3">
      {data.map((bucket, i) => (
        <div key={bucket.range} className="flex items-center gap-3">
          <span className="text-sm text-gray-600 w-16 text-right">{bucket.range}</span>
          <div className="flex-1 bg-gray-100 rounded-full h-6 relative">
            <div
              className={`h-6 rounded-full ${colors[i]} transition-all duration-500`}
              style={{ width: `${Math.max(2, (bucket.count / maxCount) * 100)}%` }}
            />
            <span className="absolute right-2 top-0.5 text-xs font-medium text-gray-700">
              {bucket.count}
            </span>
          </div>
        </div>
      ))}
    </div>
  );
}

// ===========================
// Main Page
// ===========================
export default function RiskDashboardPage() {
  const [dashboard, setDashboard] = useState<RiskDashboardData | null>(null);
  const [recentScores, setRecentScores] = useState<LoginRiskScore[]>([]);
  const [thresholds, setThresholds] = useState<RiskThreshold[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [activeTab, setActiveTab] = useState<'overview' | 'scores' | 'thresholds' | 'devices'>('overview');

  // Threshold form state
  const [thresholdForm, setThresholdForm] = useState({
    lowThreshold: 20,
    mediumThreshold: 50,
    highThreshold: 75,
    blockThreshold: 90,
    requireMfaAbove: 40,
    isActive: true,
  });
  const [savingThreshold, setSavingThreshold] = useState(false);

  // Device lookup
  const [deviceUserId, setDeviceUserId] = useState('');
  const [devices, setDevices] = useState<TrustedDevice[]>([]);
  const [loadingDevices, setLoadingDevices] = useState(false);

  const loadDashboard = useCallback(async () => {
    try {
      setLoading(true);
      setError('');
      const [dashData, scores, thresholdData] = await Promise.all([
        riskApi.getDashboard(30),
        riskApi.getScores({ take: 20 }),
        riskApi.getThresholds(),
      ]);
      setDashboard(dashData);
      setRecentScores(scores);
      setThresholds(thresholdData);

      // Populate form from first active threshold
      const active = thresholdData.find((t) => t.isActive);
      if (active) {
        setThresholdForm({
          lowThreshold: active.lowThreshold,
          mediumThreshold: active.mediumThreshold,
          highThreshold: active.highThreshold,
          blockThreshold: active.blockThreshold,
          requireMfaAbove: active.requireMfaAbove,
          isActive: active.isActive,
        });
      }
    } catch (err: any) {
      setError(err.response?.data?.message || err.message || 'Failed to load risk dashboard');
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    loadDashboard();
  }, [loadDashboard]);

  const handleSaveThreshold = async () => {
    try {
      setSavingThreshold(true);
      setError('');
      const existing = thresholds.find((t) => t.isActive);
      await riskApi.upsertThreshold({
        id: existing?.id,
        ...thresholdForm,
      });
      await loadDashboard();
    } catch (err: any) {
      setError(err.response?.data || err.message || 'Failed to save threshold');
    } finally {
      setSavingThreshold(false);
    }
  };

  const handleLoadDevices = async () => {
    if (!deviceUserId.trim()) return;
    try {
      setLoadingDevices(true);
      const result = await riskApi.getTrustedDevices(deviceUserId.trim());
      setDevices(result);
    } catch (err: any) {
      setError(err.response?.data?.message || 'Failed to load devices');
    } finally {
      setLoadingDevices(false);
    }
  };

  const handleRemoveDevice = async (deviceId: string) => {
    if (!deviceUserId.trim()) return;
    try {
      await riskApi.removeTrustedDevice(deviceId, deviceUserId.trim());
      setDevices((prev) => prev.filter((d) => d.id !== deviceId));
    } catch (err: any) {
      setError(err.response?.data?.message || 'Failed to remove device');
    }
  };

  const tabs = [
    { key: 'overview', label: 'Overview' },
    { key: 'scores', label: 'Login Scores' },
    { key: 'thresholds', label: 'Policy Config' },
    { key: 'devices', label: 'Trusted Devices' },
  ] as const;

  return (
    <DashboardLayout>
      <div className="px-4 sm:px-6 lg:px-8">
        {/* Header */}
        <div className="sm:flex sm:items-center">
          <div className="sm:flex-auto">
            <h1 className="text-2xl font-semibold text-gray-900">Risk Assessment</h1>
            <p className="mt-2 text-sm text-gray-700">
              Adaptive authentication risk scoring, login heatmaps, and device trust management.
            </p>
          </div>
          <div className="mt-4 sm:mt-0 sm:ml-16 sm:flex-none">
            <button
              onClick={loadDashboard}
              className="inline-flex items-center px-4 py-2 text-sm font-medium rounded-md border border-gray-300 bg-white text-gray-700 hover:bg-gray-50"
            >
              Refresh
            </button>
          </div>
        </div>

        {/* Error */}
        {error && (
          <div className="mt-4 bg-red-50 border border-red-200 text-red-700 px-4 py-3 rounded">
            {typeof error === 'string' ? error : JSON.stringify(error)}
          </div>
        )}

        {/* Tabs */}
        <div className="mt-6 border-b border-gray-200">
          <nav className="-mb-px flex space-x-8">
            {tabs.map((tab) => (
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

        {/* Loading */}
        {loading ? (
          <div className="mt-8 text-center">
            <div className="inline-block animate-spin rounded-full h-8 w-8 border-b-2 border-indigo-600" />
            <p className="mt-2 text-sm text-gray-500">Loading risk dashboard...</p>
          </div>
        ) : (
          <>
            {/* ===================== OVERVIEW TAB ===================== */}
            {activeTab === 'overview' && dashboard && (
              <>
                {/* Summary Cards */}
                <div className="mt-6 grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-4 gap-4">
                  <div className="bg-white rounded-lg shadow px-5 py-4">
                    <dt className="text-sm font-medium text-gray-500 truncate">Total Assessments</dt>
                    <dd className="mt-1 text-3xl font-semibold text-gray-900">
                      {dashboard.summary.totalAssessments.toLocaleString()}
                    </dd>
                    <p className="mt-1 text-sm text-gray-500">Last 30 days</p>
                  </div>
                  <div className="bg-white rounded-lg shadow px-5 py-4">
                    <dt className="text-sm font-medium text-gray-500 truncate">Avg Risk Score</dt>
                    <dd className={`mt-1 text-3xl font-semibold ${riskTextColor(dashboard.summary.avgRiskScore)}`}>
                      {dashboard.summary.avgRiskScore}
                    </dd>
                    <p className="mt-1 text-sm text-gray-500">
                      {dashboard.summary.avgRiskScore <= 30 ? 'Healthy' : dashboard.summary.avgRiskScore <= 60 ? 'Elevated' : 'High'}
                    </p>
                  </div>
                  <div className="bg-white rounded-lg shadow px-5 py-4">
                    <dt className="text-sm font-medium text-gray-500 truncate">Blocked Logins</dt>
                    <dd className="mt-1 text-3xl font-semibold text-red-600">
                      {dashboard.summary.blockedCount.toLocaleString()}
                    </dd>
                    <p className="mt-1 text-sm text-gray-500">
                      {dashboard.summary.stepUpCount} step-up challenges
                    </p>
                  </div>
                  <div className="bg-white rounded-lg shadow px-5 py-4">
                    <dt className="text-sm font-medium text-gray-500 truncate">Trusted Devices</dt>
                    <dd className="mt-1 text-3xl font-semibold text-indigo-600">
                      {dashboard.summary.trustedDeviceCount.toLocaleString()}
                    </dd>
                    <p className="mt-1 text-sm text-gray-500">Active</p>
                  </div>
                </div>

                {/* Charts */}
                <div className="mt-8 grid grid-cols-1 lg:grid-cols-2 gap-6">
                  {/* Heatmap */}
                  <div className="bg-white rounded-lg shadow p-6">
                    <h3 className="text-lg font-medium text-gray-900 mb-4">Login Risk Heatmap</h3>
                    <p className="text-xs text-gray-500 mb-3">
                      Color intensity = login count, color hue = avg risk (green=low, red=high)
                    </p>
                    <RiskHeatmap data={dashboard.heatmap} />
                  </div>

                  {/* Distribution */}
                  <div className="bg-white rounded-lg shadow p-6">
                    <h3 className="text-lg font-medium text-gray-900 mb-4">Risk Score Distribution</h3>
                    <DistributionChart data={dashboard.distribution} />
                  </div>
                </div>

                {/* Action Breakdown */}
                <div className="mt-6 bg-white rounded-lg shadow p-6">
                  <h3 className="text-lg font-medium text-gray-900 mb-4">Authentication Actions</h3>
                  <div className="flex gap-8">
                    {[
                      { label: 'Allowed', count: dashboard.summary.allowedCount, color: 'bg-green-500' },
                      { label: 'Step-Up MFA', count: dashboard.summary.stepUpCount, color: 'bg-yellow-500' },
                      { label: 'Blocked', count: dashboard.summary.blockedCount, color: 'bg-red-500' },
                    ].map((item) => {
                      const pct = dashboard.summary.totalAssessments > 0
                        ? ((item.count / dashboard.summary.totalAssessments) * 100).toFixed(1)
                        : '0';
                      return (
                        <div key={item.label} className="flex-1">
                          <div className="flex items-center gap-2 mb-1">
                            <div className={`w-3 h-3 rounded-full ${item.color}`} />
                            <span className="text-sm font-medium text-gray-700">{item.label}</span>
                          </div>
                          <div className="text-2xl font-semibold text-gray-900">{item.count.toLocaleString()}</div>
                          <div className="text-sm text-gray-500">{pct}%</div>
                        </div>
                      );
                    })}
                  </div>
                </div>
              </>
            )}

            {/* ===================== SCORES TAB ===================== */}
            {activeTab === 'scores' && (
              <div className="mt-6 bg-white rounded-lg shadow overflow-hidden">
                <div className="px-6 py-4 border-b border-gray-200">
                  <h3 className="text-lg font-medium text-gray-900">Recent Login Risk Scores</h3>
                </div>
                <div className="overflow-x-auto">
                  <table className="min-w-full divide-y divide-gray-200">
                    <thead className="bg-gray-50">
                      <tr>
                        <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase">Time</th>
                        <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase">User ID</th>
                        <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase">IP Address</th>
                        <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase">Location</th>
                        <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase">Score</th>
                        <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase">Action</th>
                        <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase">Factors</th>
                      </tr>
                    </thead>
                    <tbody className="bg-white divide-y divide-gray-200">
                      {recentScores.length === 0 ? (
                        <tr>
                          <td colSpan={7} className="px-6 py-8 text-center text-sm text-gray-500">
                            No risk assessments recorded yet.
                          </td>
                        </tr>
                      ) : (
                        recentScores.map((score) => {
                          let factors: Array<{ factor: string; points: number; description: string }> = [];
                          try {
                            factors = JSON.parse(score.riskFactors);
                          } catch { /* ignore */ }

                          return (
                            <tr key={score.id} className="hover:bg-gray-50">
                              <td className="px-6 py-3 text-sm text-gray-500 whitespace-nowrap">
                                {new Date(score.createdAt).toLocaleString()}
                              </td>
                              <td className="px-6 py-3 text-sm text-gray-900 font-mono whitespace-nowrap">
                                {score.userId.substring(0, 8)}...
                              </td>
                              <td className="px-6 py-3 text-sm text-gray-900 whitespace-nowrap">{score.ipAddress}</td>
                              <td className="px-6 py-3 text-sm text-gray-500 whitespace-nowrap">
                                {score.geoLocation || '-'}
                              </td>
                              <td className="px-6 py-3 whitespace-nowrap">
                                <span className={`inline-flex items-center px-2.5 py-0.5 rounded-full text-xs font-semibold ${riskColor(score.riskScore)} ${riskTextColor(score.riskScore)}`}>
                                  {score.riskScore}
                                </span>
                              </td>
                              <td className="px-6 py-3 whitespace-nowrap">
                                <span className={`inline-flex items-center px-2.5 py-0.5 rounded-full text-xs font-semibold ${ACTION_COLORS[score.action]}`}>
                                  {ACTION_LABELS[score.action]}
                                </span>
                              </td>
                              <td className="px-6 py-3 text-sm text-gray-500">
                                {factors.length > 0 ? (
                                  <div className="flex flex-wrap gap-1">
                                    {factors.map((f, i) => (
                                      <span
                                        key={i}
                                        className="inline-flex items-center px-2 py-0.5 rounded text-xs bg-gray-100 text-gray-700"
                                        title={f.description}
                                      >
                                        {f.factor} ({f.points > 0 ? '+' : ''}{f.points})
                                      </span>
                                    ))}
                                  </div>
                                ) : (
                                  <span className="text-gray-400">None</span>
                                )}
                              </td>
                            </tr>
                          );
                        })
                      )}
                    </tbody>
                  </table>
                </div>
              </div>
            )}

            {/* ===================== THRESHOLDS TAB ===================== */}
            {activeTab === 'thresholds' && (
              <div className="mt-6 bg-white rounded-lg shadow p-6">
                <h3 className="text-lg font-medium text-gray-900 mb-4">Risk Policy Configuration</h3>
                <p className="text-sm text-gray-500 mb-6">
                  Configure the risk score thresholds that determine authentication behavior.
                  Scores are 0-100 where higher means more risky.
                </p>

                <div className="grid grid-cols-1 md:grid-cols-2 gap-6">
                  <div>
                    <label className="block text-sm font-medium text-gray-700 mb-1">
                      Low Threshold (green zone ceiling)
                    </label>
                    <input
                      type="number"
                      min={0}
                      max={100}
                      value={thresholdForm.lowThreshold}
                      onChange={(e) => setThresholdForm((f) => ({ ...f, lowThreshold: +e.target.value }))}
                      className="block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                    />
                  </div>
                  <div>
                    <label className="block text-sm font-medium text-gray-700 mb-1">
                      Medium Threshold (yellow zone ceiling)
                    </label>
                    <input
                      type="number"
                      min={0}
                      max={100}
                      value={thresholdForm.mediumThreshold}
                      onChange={(e) => setThresholdForm((f) => ({ ...f, mediumThreshold: +e.target.value }))}
                      className="block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                    />
                  </div>
                  <div>
                    <label className="block text-sm font-medium text-gray-700 mb-1">
                      High Threshold (orange zone ceiling)
                    </label>
                    <input
                      type="number"
                      min={0}
                      max={100}
                      value={thresholdForm.highThreshold}
                      onChange={(e) => setThresholdForm((f) => ({ ...f, highThreshold: +e.target.value }))}
                      className="block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                    />
                  </div>
                  <div>
                    <label className="block text-sm font-medium text-gray-700 mb-1">
                      Block Threshold (auto-block above this)
                    </label>
                    <input
                      type="number"
                      min={0}
                      max={100}
                      value={thresholdForm.blockThreshold}
                      onChange={(e) => setThresholdForm((f) => ({ ...f, blockThreshold: +e.target.value }))}
                      className="block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                    />
                  </div>
                  <div>
                    <label className="block text-sm font-medium text-gray-700 mb-1">
                      Require MFA Above (step-up trigger)
                    </label>
                    <input
                      type="number"
                      min={0}
                      max={100}
                      value={thresholdForm.requireMfaAbove}
                      onChange={(e) => setThresholdForm((f) => ({ ...f, requireMfaAbove: +e.target.value }))}
                      className="block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                    />
                  </div>
                  <div className="flex items-center pt-6">
                    <label className="flex items-center gap-2 text-sm font-medium text-gray-700">
                      <input
                        type="checkbox"
                        checked={thresholdForm.isActive}
                        onChange={(e) => setThresholdForm((f) => ({ ...f, isActive: e.target.checked }))}
                        className="rounded border-gray-300 text-indigo-600 focus:ring-indigo-500"
                      />
                      Active
                    </label>
                  </div>
                </div>

                {/* Visual threshold bar */}
                <div className="mt-6">
                  <p className="text-sm font-medium text-gray-700 mb-2">Threshold Visualization</p>
                  <div className="relative h-8 bg-gray-100 rounded-full overflow-hidden flex">
                    <div
                      className="bg-green-400 h-full"
                      style={{ width: `${thresholdForm.lowThreshold}%` }}
                    />
                    <div
                      className="bg-yellow-400 h-full"
                      style={{ width: `${thresholdForm.mediumThreshold - thresholdForm.lowThreshold}%` }}
                    />
                    <div
                      className="bg-orange-400 h-full"
                      style={{ width: `${thresholdForm.highThreshold - thresholdForm.mediumThreshold}%` }}
                    />
                    <div
                      className="bg-red-500 h-full"
                      style={{ width: `${thresholdForm.blockThreshold - thresholdForm.highThreshold}%` }}
                    />
                    <div
                      className="bg-red-800 h-full"
                      style={{ width: `${100 - thresholdForm.blockThreshold}%` }}
                    />
                  </div>
                  <div className="flex justify-between text-xs text-gray-500 mt-1">
                    <span>0 (Safe)</span>
                    <span>{thresholdForm.lowThreshold} Low</span>
                    <span>{thresholdForm.mediumThreshold} Medium</span>
                    <span>{thresholdForm.highThreshold} High</span>
                    <span>{thresholdForm.blockThreshold} Block</span>
                    <span>100</span>
                  </div>
                  {/* MFA trigger marker */}
                  <div className="relative mt-1">
                    <div
                      className="absolute -top-1 w-0.5 h-3 bg-indigo-600"
                      style={{ left: `${thresholdForm.requireMfaAbove}%` }}
                    />
                    <div
                      className="absolute top-2 text-xs text-indigo-600 font-medium -translate-x-1/2"
                      style={{ left: `${thresholdForm.requireMfaAbove}%` }}
                    >
                      MFA: {thresholdForm.requireMfaAbove}
                    </div>
                  </div>
                </div>

                <div className="mt-8 flex justify-end">
                  <button
                    onClick={handleSaveThreshold}
                    disabled={savingThreshold}
                    className="inline-flex items-center px-4 py-2 text-sm font-medium rounded-md border border-transparent bg-indigo-600 text-white hover:bg-indigo-700 disabled:opacity-50 disabled:cursor-not-allowed"
                  >
                    {savingThreshold ? (
                      <>
                        <span className="animate-spin rounded-full h-4 w-4 border-b-2 border-white mr-2" />
                        Saving...
                      </>
                    ) : (
                      'Save Thresholds'
                    )}
                  </button>
                </div>

                {/* Existing thresholds table */}
                {thresholds.length > 0 && (
                  <div className="mt-8 border-t border-gray-200 pt-6">
                    <h4 className="text-sm font-semibold text-gray-700 uppercase mb-3">
                      Configured Thresholds ({thresholds.length})
                    </h4>
                    <div className="overflow-x-auto">
                      <table className="min-w-full divide-y divide-gray-200">
                        <thead className="bg-gray-50">
                          <tr>
                            <th className="px-4 py-2 text-left text-xs font-medium text-gray-500 uppercase">Tenant</th>
                            <th className="px-4 py-2 text-left text-xs font-medium text-gray-500 uppercase">Low</th>
                            <th className="px-4 py-2 text-left text-xs font-medium text-gray-500 uppercase">Medium</th>
                            <th className="px-4 py-2 text-left text-xs font-medium text-gray-500 uppercase">High</th>
                            <th className="px-4 py-2 text-left text-xs font-medium text-gray-500 uppercase">Block</th>
                            <th className="px-4 py-2 text-left text-xs font-medium text-gray-500 uppercase">MFA</th>
                            <th className="px-4 py-2 text-left text-xs font-medium text-gray-500 uppercase">Status</th>
                          </tr>
                        </thead>
                        <tbody className="divide-y divide-gray-200">
                          {thresholds.map((t) => (
                            <tr key={t.id}>
                              <td className="px-4 py-2 text-sm text-gray-900">
                                {t.tenantId ? t.tenantId.substring(0, 8) + '...' : 'Global'}
                              </td>
                              <td className="px-4 py-2 text-sm text-gray-600">{t.lowThreshold}</td>
                              <td className="px-4 py-2 text-sm text-gray-600">{t.mediumThreshold}</td>
                              <td className="px-4 py-2 text-sm text-gray-600">{t.highThreshold}</td>
                              <td className="px-4 py-2 text-sm text-gray-600">{t.blockThreshold}</td>
                              <td className="px-4 py-2 text-sm text-gray-600">{t.requireMfaAbove}</td>
                              <td className="px-4 py-2 text-sm">
                                <span className={`inline-flex px-2 py-0.5 rounded-full text-xs font-semibold ${
                                  t.isActive ? 'bg-green-100 text-green-800' : 'bg-gray-100 text-gray-600'
                                }`}>
                                  {t.isActive ? 'Active' : 'Inactive'}
                                </span>
                              </td>
                            </tr>
                          ))}
                        </tbody>
                      </table>
                    </div>
                  </div>
                )}
              </div>
            )}

            {/* ===================== DEVICES TAB ===================== */}
            {activeTab === 'devices' && (
              <div className="mt-6 bg-white rounded-lg shadow p-6">
                <h3 className="text-lg font-medium text-gray-900 mb-4">Trusted Device Management</h3>
                <p className="text-sm text-gray-500 mb-6">
                  View and manage trusted devices for users. Trusted devices reduce login risk scores.
                </p>

                {/* User lookup */}
                <div className="flex items-end gap-4 mb-6">
                  <div className="flex-1 max-w-md">
                    <label className="block text-sm font-medium text-gray-700 mb-1">User ID</label>
                    <input
                      type="text"
                      value={deviceUserId}
                      onChange={(e) => setDeviceUserId(e.target.value)}
                      placeholder="Enter user ID (GUID)"
                      className="block w-full rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                    />
                  </div>
                  <button
                    onClick={handleLoadDevices}
                    disabled={loadingDevices || !deviceUserId.trim()}
                    className="inline-flex items-center px-4 py-2 text-sm font-medium rounded-md border border-transparent bg-indigo-600 text-white hover:bg-indigo-700 disabled:opacity-50"
                  >
                    {loadingDevices ? 'Loading...' : 'Load Devices'}
                  </button>
                </div>

                {/* Devices table */}
                {devices.length > 0 ? (
                  <div className="overflow-x-auto">
                    <table className="min-w-full divide-y divide-gray-200">
                      <thead className="bg-gray-50">
                        <tr>
                          <th className="px-4 py-2 text-left text-xs font-medium text-gray-500 uppercase">Name</th>
                          <th className="px-4 py-2 text-left text-xs font-medium text-gray-500 uppercase">Fingerprint</th>
                          <th className="px-4 py-2 text-left text-xs font-medium text-gray-500 uppercase">Trust Score</th>
                          <th className="px-4 py-2 text-left text-xs font-medium text-gray-500 uppercase">Last Used</th>
                          <th className="px-4 py-2 text-left text-xs font-medium text-gray-500 uppercase">Expires</th>
                          <th className="px-4 py-2 text-left text-xs font-medium text-gray-500 uppercase">Actions</th>
                        </tr>
                      </thead>
                      <tbody className="divide-y divide-gray-200">
                        {devices.map((device) => (
                          <tr key={device.id}>
                            <td className="px-4 py-3 text-sm font-medium text-gray-900">{device.name}</td>
                            <td className="px-4 py-3 text-sm text-gray-500 font-mono">
                              {device.deviceFingerprint.substring(0, 16)}...
                            </td>
                            <td className="px-4 py-3 text-sm">
                              <div className="flex items-center gap-2">
                                <div className="w-16 bg-gray-200 rounded-full h-2">
                                  <div
                                    className={`h-2 rounded-full ${
                                      device.trustScore >= 80 ? 'bg-green-500' : device.trustScore >= 50 ? 'bg-yellow-500' : 'bg-red-500'
                                    }`}
                                    style={{ width: `${device.trustScore}%` }}
                                  />
                                </div>
                                <span className="text-gray-700">{device.trustScore}</span>
                              </div>
                            </td>
                            <td className="px-4 py-3 text-sm text-gray-500">
                              {new Date(device.lastUsedAt).toLocaleDateString()}
                            </td>
                            <td className="px-4 py-3 text-sm text-gray-500">
                              {new Date(device.expiresAt).toLocaleDateString()}
                            </td>
                            <td className="px-4 py-3 text-sm">
                              <button
                                onClick={() => handleRemoveDevice(device.id)}
                                className="text-red-600 hover:text-red-800 text-sm font-medium"
                              >
                                Remove
                              </button>
                            </td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  </div>
                ) : deviceUserId && !loadingDevices ? (
                  <div className="text-center py-8 text-sm text-gray-500">
                    No trusted devices found for this user.
                  </div>
                ) : null}
              </div>
            )}
          </>
        )}
      </div>
    </DashboardLayout>
  );
}
