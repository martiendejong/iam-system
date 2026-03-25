import { useState, useEffect } from 'react';
import DashboardLayout from '../components/layout/DashboardLayout';
import SimpleBarChart from '../components/SimpleBarChart';
import SimpleTrendLine from '../components/SimpleTrendLine';
import { auditApi } from '../services/auditApi';
import type { AuditEvent, AuditStatistics, ComplianceReport } from '../types/audit';

const FRAMEWORKS = ['SOC2', 'GDPR', 'HIPAA'] as const;

function ScoreIndicator({ score }: { score: number }) {
  const color = score > 80 ? 'text-green-600' : score > 60 ? 'text-yellow-600' : 'text-red-600';
  const bgColor = score > 80 ? 'bg-green-100' : score > 60 ? 'bg-yellow-100' : 'bg-red-100';
  const ringColor = score > 80 ? 'stroke-green-500' : score > 60 ? 'stroke-yellow-500' : 'stroke-red-500';

  const radius = 40;
  const circumference = 2 * Math.PI * radius;
  const offset = circumference - (score / 100) * circumference;

  return (
    <div className="flex flex-col items-center">
      <svg width="100" height="100" viewBox="0 0 100 100">
        <circle
          cx="50" cy="50" r={radius}
          fill="none" strokeWidth="8"
          className="stroke-gray-200"
        />
        <circle
          cx="50" cy="50" r={radius}
          fill="none" strokeWidth="8"
          className={ringColor}
          strokeLinecap="round"
          strokeDasharray={circumference}
          strokeDashoffset={offset}
          transform="rotate(-90 50 50)"
        />
        <text x="50" y="50" textAnchor="middle" dominantBaseline="central"
          className={`text-xl font-bold ${color}`} fill="currentColor">
          {score}
        </text>
      </svg>
      <span className={`mt-1 text-sm font-medium px-2 py-0.5 rounded-full ${bgColor} ${color}`}>
        {score > 80 ? 'Good' : score > 60 ? 'Fair' : 'Poor'}
      </span>
    </div>
  );
}

export default function ComplianceDashboardPage() {
  const [statistics, setStatistics] = useState<AuditStatistics | null>(null);
  const [anomalies, setAnomalies] = useState<AuditEvent[]>([]);
  const [report, setReport] = useState<ComplianceReport | null>(null);
  const [selectedFramework, setSelectedFramework] = useState<string>('SOC2');
  const [generatingReport, setGeneratingReport] = useState(false);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');

  useEffect(() => {
    loadDashboard();
  }, []);

  const loadDashboard = async () => {
    try {
      setLoading(true);
      setError('');
      const [stats, anomalyData] = await Promise.all([
        auditApi.getStatistics(),
        auditApi.getAnomalies(),
      ]);
      setStatistics(stats);
      setAnomalies(anomalyData);
    } catch (err: any) {
      setError(err.response?.data?.message || 'Failed to load compliance dashboard');
    } finally {
      setLoading(false);
    }
  };

  const handleGenerateReport = async () => {
    try {
      setGeneratingReport(true);
      setError('');
      const result = await auditApi.generateReport(selectedFramework);
      setReport(result);
    } catch (err: any) {
      setError(err.response?.data?.message || 'Failed to generate compliance report');
    } finally {
      setGeneratingReport(false);
    }
  };

  const barChartData = statistics
    ? Object.entries(statistics.eventsByType).map(([label, value]) => ({
        label,
        value,
        color: label.includes('Delete') || label.includes('Revoke')
          ? '#ef4444'
          : label.includes('Create') || label.includes('Grant')
          ? '#22c55e'
          : '#6366f1',
      }))
    : [];

  const trendData = statistics
    ? statistics.eventsByDay.map(d => ({ date: d.date, value: d.count }))
    : [];

  const findingBadge = (status: string) => {
    switch (status) {
      case 'pass': return 'bg-green-100 text-green-800';
      case 'fail': return 'bg-red-100 text-red-800';
      case 'warning': return 'bg-yellow-100 text-yellow-800';
      default: return 'bg-gray-100 text-gray-600';
    }
  };

  return (
    <DashboardLayout>
      <div className="px-4 sm:px-6 lg:px-8">
        {/* Header */}
        <div className="sm:flex sm:items-center">
          <div className="sm:flex-auto">
            <h1 className="text-2xl font-semibold text-gray-900">Compliance Dashboard</h1>
            <p className="mt-2 text-sm text-gray-700">
              Security metrics, compliance reporting, and anomaly detection overview.
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
            {error}
          </div>
        )}

        {/* Loading */}
        {loading ? (
          <div className="mt-8 text-center">
            <div className="inline-block animate-spin rounded-full h-8 w-8 border-b-2 border-indigo-600"></div>
            <p className="mt-2 text-sm text-gray-500">Loading compliance dashboard...</p>
          </div>
        ) : statistics && (
          <>
            {/* Summary Cards */}
            <div className="mt-6 grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-4 gap-4">
              <div className="bg-white rounded-lg shadow px-5 py-4">
                <dt className="text-sm font-medium text-gray-500 truncate">Total Events Today</dt>
                <dd className="mt-1 text-3xl font-semibold text-gray-900">
                  {statistics.totalEvents.toLocaleString()}
                </dd>
              </div>
              <div className="bg-white rounded-lg shadow px-5 py-4">
                <dt className="text-sm font-medium text-gray-500 truncate">Failed Events</dt>
                <dd className="mt-1 text-3xl font-semibold text-red-600">
                  {statistics.failedEvents.toLocaleString()}
                </dd>
                {statistics.totalEvents > 0 && (
                  <p className="mt-1 text-sm text-gray-500">
                    {((statistics.failedEvents / statistics.totalEvents) * 100).toFixed(1)}% failure rate
                  </p>
                )}
              </div>
              <div className="bg-white rounded-lg shadow px-5 py-4">
                <dt className="text-sm font-medium text-gray-500 truncate">High-Risk Events</dt>
                <dd className="mt-1 text-3xl font-semibold text-yellow-600">
                  {statistics.highRiskEvents.toLocaleString()}
                </dd>
                {statistics.totalEvents > 0 && (
                  <p className="mt-1 text-sm text-gray-500">
                    {((statistics.highRiskEvents / statistics.totalEvents) * 100).toFixed(1)}% of total
                  </p>
                )}
              </div>
              <div className="bg-white rounded-lg shadow px-5 py-4">
                <dt className="text-sm font-medium text-gray-500 truncate">Compliance Score</dt>
                <dd className="mt-1">
                  {report ? (
                    <ScoreIndicator score={report.overallScore} />
                  ) : (
                    <span className="text-3xl font-semibold text-gray-400">--</span>
                  )}
                </dd>
              </div>
            </div>

            {/* Charts */}
            <div className="mt-8 grid grid-cols-1 lg:grid-cols-2 gap-6">
              {/* Events by Type */}
              <div className="bg-white rounded-lg shadow p-6">
                <SimpleBarChart
                  data={barChartData}
                  width={480}
                  height={Math.max(200, barChartData.length * 36 + 40)}
                  title="Events by Type"
                />
              </div>

              {/* Events Trend */}
              <div className="bg-white rounded-lg shadow p-6">
                <SimpleTrendLine
                  data={trendData}
                  width={480}
                  height={250}
                  title="Events by Day (Last 30 Days)"
                  color="#6366f1"
                />
              </div>
            </div>

            {/* Generate Report Section */}
            <div className="mt-8 bg-white rounded-lg shadow p-6">
              <h2 className="text-lg font-medium text-gray-900 mb-4">Generate Compliance Report</h2>
              <div className="flex items-end gap-4">
                <div>
                  <label className="block text-sm font-medium text-gray-700 mb-1">Framework</label>
                  <select
                    value={selectedFramework}
                    onChange={(e) => setSelectedFramework(e.target.value)}
                    className="block w-48 rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                  >
                    {FRAMEWORKS.map(fw => (
                      <option key={fw} value={fw}>{fw}</option>
                    ))}
                  </select>
                </div>
                <button
                  onClick={handleGenerateReport}
                  disabled={generatingReport}
                  className="inline-flex items-center px-4 py-2 text-sm font-medium rounded-md border border-transparent bg-indigo-600 text-white hover:bg-indigo-700 disabled:opacity-50 disabled:cursor-not-allowed"
                >
                  {generatingReport ? (
                    <>
                      <span className="animate-spin rounded-full h-4 w-4 border-b-2 border-white mr-2"></span>
                      Generating...
                    </>
                  ) : (
                    'Generate Report'
                  )}
                </button>
              </div>

              {/* Report Results */}
              {report && (
                <div className="mt-6 border-t border-gray-200 pt-6">
                  <div className="flex items-start justify-between">
                    <div>
                      <h3 className="text-lg font-medium text-gray-900">
                        {report.framework} Compliance Report
                      </h3>
                      <p className="text-sm text-gray-500 mt-1">
                        Generated at {new Date(report.generatedAt).toLocaleString()}
                      </p>
                    </div>
                    <ScoreIndicator score={report.overallScore} />
                  </div>

                  {/* Findings */}
                  {report.findings && report.findings.length > 0 && (
                    <div className="mt-6">
                      <h4 className="text-sm font-semibold text-gray-700 uppercase mb-3">
                        Findings ({report.findings.length})
                      </h4>
                      <div className="space-y-3">
                        {report.findings.map((finding, index) => (
                          <div key={index} className="flex items-start gap-3 p-3 rounded-lg border border-gray-200 bg-gray-50">
                            <span className={`inline-flex shrink-0 rounded-full px-2.5 py-0.5 text-xs font-semibold leading-5 ${findingBadge(finding.status)}`}>
                              {finding.status.toUpperCase()}
                            </span>
                            <div className="min-w-0 flex-1">
                              <p className="text-sm font-medium text-gray-900">{finding.category}</p>
                              <p className="text-sm text-gray-600 mt-0.5">{finding.description}</p>
                              {finding.recommendation && (
                                <p className="text-sm text-indigo-600 mt-1">
                                  Recommendation: {finding.recommendation}
                                </p>
                              )}
                            </div>
                          </div>
                        ))}
                      </div>
                    </div>
                  )}
                </div>
              )}
            </div>

            {/* Anomaly Alerts */}
            <div className="mt-8 bg-white rounded-lg shadow p-6">
              <h2 className="text-lg font-medium text-gray-900 mb-4">
                Anomaly Alerts
                {anomalies.length > 0 && (
                  <span className="ml-2 inline-flex items-center rounded-full bg-red-100 px-2.5 py-0.5 text-xs font-medium text-red-800">
                    {anomalies.length}
                  </span>
                )}
              </h2>
              {anomalies.length === 0 ? (
                <div className="text-center py-8">
                  <div className="text-4xl mb-2">&#10003;</div>
                  <p className="text-sm text-gray-500">No anomalies detected. System is operating normally.</p>
                </div>
              ) : (
                <div className="space-y-3">
                  {anomalies.map((anomaly) => {
                    const severity = (anomaly.riskScore ?? 0) > 70
                      ? 'critical'
                      : (anomaly.riskScore ?? 0) > 40
                      ? 'warning'
                      : 'info';
                    const severityStyles = {
                      critical: 'border-red-300 bg-red-50',
                      warning: 'border-yellow-300 bg-yellow-50',
                      info: 'border-blue-300 bg-blue-50',
                    };
                    const severityBadge = {
                      critical: 'bg-red-100 text-red-800',
                      warning: 'bg-yellow-100 text-yellow-800',
                      info: 'bg-blue-100 text-blue-800',
                    };

                    return (
                      <div key={anomaly.id} className={`p-4 rounded-lg border ${severityStyles[severity]}`}>
                        <div className="flex items-start justify-between">
                          <div className="flex items-start gap-3">
                            <span className={`inline-flex shrink-0 rounded-full px-2.5 py-0.5 text-xs font-semibold ${severityBadge[severity]}`}>
                              {severity.toUpperCase()}
                            </span>
                            <div>
                              <p className="text-sm font-medium text-gray-900">
                                {anomaly.eventType}: {anomaly.action}
                              </p>
                              <p className="text-sm text-gray-600 mt-0.5">
                                {anomaly.userName || anomaly.userId || 'Unknown user'} {anomaly.ipAddress ? `from ${anomaly.ipAddress}` : ''}
                              </p>
                              {anomaly.details && (
                                <p className="text-xs text-gray-500 mt-1 truncate max-w-lg">
                                  {anomaly.details}
                                </p>
                              )}
                            </div>
                          </div>
                          <div className="text-right shrink-0 ml-4">
                            <p className="text-xs text-gray-500">
                              {new Date(anomaly.createdAt).toLocaleString()}
                            </p>
                            {anomaly.riskScore !== undefined && (
                              <p className="text-sm font-semibold mt-1">
                                Risk: <span className={severity === 'critical' ? 'text-red-600' : severity === 'warning' ? 'text-yellow-600' : 'text-blue-600'}>{anomaly.riskScore}</span>
                              </p>
                            )}
                          </div>
                        </div>
                      </div>
                    );
                  })}
                </div>
              )}
            </div>
          </>
        )}
      </div>
    </DashboardLayout>
  );
}
