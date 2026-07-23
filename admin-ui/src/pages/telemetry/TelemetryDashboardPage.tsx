import { useState, useEffect, useCallback } from 'react';
import DashboardLayout from '../../components/layout/DashboardLayout';
import TelemetryLineChart from '../../components/TelemetryLineChart';
import { deviceApi } from '../../services/deviceApi';
import { telemetryApi, TIME_RANGE_PRESETS } from '../../services/telemetryApi';
import type { TimeRangeKey, TelemetryLatestResult } from '../../services/telemetryApi';
import type { Device } from '../../types/devices';

interface ChartPoint {
  timestamp: string;
  value: number;
}

export default function TelemetryDashboardPage() {
  const [devices, setDevices] = useState<Device[]>([]);
  const [deviceId, setDeviceId] = useState('');
  const [metricNames, setMetricNames] = useState<string[]>([]);
  const [metricName, setMetricName] = useState('');
  const [rangeKey, setRangeKey] = useState<TimeRangeKey>('24h');
  const [customFrom, setCustomFrom] = useState('');
  const [customTo, setCustomTo] = useState('');
  const [useCustomRange, setUseCustomRange] = useState(false);

  const [latest, setLatest] = useState<TelemetryLatestResult | null>(null);
  const [chartPoints, setChartPoints] = useState<ChartPoint[]>([]);
  const [totalCount, setTotalCount] = useState(0);

  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');
  const [exporting, setExporting] = useState<'csv' | 'json' | null>(null);

  // Load device list once.
  useEffect(() => {
    deviceApi
      .getDevices()
      .then((list) => {
        setDevices(list);
        if (list.length > 0) setDeviceId(list[0].deviceId);
      })
      .catch(() => {
        /* device list is a convenience picker - a manual deviceId can still be typed below */
      });
  }, []);

  const resolveRange = useCallback((): { startTime: string; endTime: string; aggregation: string } => {
    if (useCustomRange && customFrom && customTo) {
      const hours = (new Date(customTo).getTime() - new Date(customFrom).getTime()) / 36e5;
      const aggregation = hours <= 6 ? 'raw' : hours <= 48 ? '5m' : '1h';
      return { startTime: new Date(customFrom).toISOString(), endTime: new Date(customTo).toISOString(), aggregation };
    }
    const preset = TIME_RANGE_PRESETS[rangeKey];
    const endTime = new Date();
    const startTime = new Date(endTime.getTime() - preset.hours * 36e5);
    return { startTime: startTime.toISOString(), endTime: endTime.toISOString(), aggregation: preset.aggregation };
  }, [useCustomRange, customFrom, customTo, rangeKey]);

  const load = useCallback(async () => {
    if (!deviceId) return;
    try {
      setLoading(true);
      setError('');

      const latestResult = await telemetryApi.getLatest(deviceId);
      setLatest(latestResult);

      const names = await telemetryApi.getMetricNames(deviceId);
      setMetricNames(names);
      const activeMetric = metricName && names.includes(metricName) ? metricName : names[0] || '';
      if (activeMetric !== metricName) setMetricName(activeMetric);
      if (!activeMetric) {
        setChartPoints([]);
        setTotalCount(0);
        return;
      }

      const { startTime, endTime, aggregation } = resolveRange();

      if (aggregation === 'raw') {
        const result = await telemetryApi.query({ deviceId, metricName: activeMetric, startTime, endTime, limit: 2000 });
        setTotalCount(result.totalCount);
        setChartPoints(
          result.records
            .filter((r) => r.numericValue !== undefined && r.numericValue !== null)
            .map((r) => ({ timestamp: r.timestamp, value: r.numericValue! }))
            .reverse() // query() returns newest-first; chart wants chronological order
        );
      } else {
        const result = await telemetryApi.aggregate({
          metricName: activeMetric,
          deviceId,
          aggregation: 'avg',
          interval: aggregation,
          startTime,
          endTime,
        });
        setTotalCount(result.buckets.length);
        setChartPoints(result.buckets.map((b) => ({ timestamp: b.bucketStart, value: b.value })));
      }
    } catch (err: any) {
      setError(err.response?.data?.error || err.message || 'Failed to load telemetry');
    } finally {
      setLoading(false);
    }
  }, [deviceId, metricName, resolveRange]);

  useEffect(() => {
    load();
  }, [deviceId, metricName, rangeKey, useCustomRange, customFrom, customTo]); // eslint-disable-line react-hooks/exhaustive-deps

  const handleExport = async (format: 'csv' | 'json') => {
    if (!deviceId) return;
    try {
      setExporting(format);
      const { startTime, endTime } = resolveRange();
      await telemetryApi.exportAndDownload({ deviceId, metricName: metricName || undefined, startTime, endTime }, format);
    } catch (err: any) {
      setError(err.response?.data?.error || err.message || 'Export failed');
    } finally {
      setExporting(null);
    }
  };

  const activeAggregation = resolveRange().aggregation;

  return (
    <DashboardLayout>
      <div className="px-4 sm:px-6 lg:px-8">
        <div className="sm:flex sm:items-center">
          <div className="sm:flex-auto">
            <h1 className="text-2xl font-semibold text-gray-900">Telemetry</h1>
            <p className="mt-2 text-sm text-gray-700">
              Time-series device telemetry: historical trends, latest values, and export.
            </p>
          </div>
          <div className="mt-4 sm:mt-0 sm:ml-16 sm:flex-none">
            <button
              onClick={load}
              className="inline-flex items-center px-4 py-2 text-sm font-medium rounded-md border border-gray-300 bg-white text-gray-700 hover:bg-gray-50"
            >
              Refresh
            </button>
          </div>
        </div>

        {error && (
          <div className="mt-4 bg-red-50 border border-red-200 text-red-700 px-4 py-3 rounded flex items-center justify-between">
            <span>{error}</span>
            <button onClick={load} className="text-sm font-medium underline">
              Retry
            </button>
          </div>
        )}

        {/* Controls */}
        <div className="mt-6 bg-white rounded-lg shadow p-4 flex flex-wrap items-end gap-4">
          <div>
            <label className="block text-xs font-medium text-gray-500 mb-1">Device</label>
            <select
              value={deviceId}
              onChange={(e) => setDeviceId(e.target.value)}
              className="block w-56 rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
            >
              {devices.length === 0 && deviceId && <option value={deviceId}>{deviceId}</option>}
              {devices.map((d) => (
                <option key={d.id} value={d.deviceId}>
                  {d.name} ({d.deviceId})
                </option>
              ))}
            </select>
          </div>

          <div>
            <label className="block text-xs font-medium text-gray-500 mb-1">Metric</label>
            <select
              value={metricName}
              onChange={(e) => setMetricName(e.target.value)}
              disabled={metricNames.length === 0}
              className="block w-40 rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
            >
              {metricNames.length === 0 && <option value="">No metrics</option>}
              {metricNames.map((m) => (
                <option key={m} value={m}>
                  {m}
                </option>
              ))}
            </select>
          </div>

          <div>
            <label className="block text-xs font-medium text-gray-500 mb-1">Range</label>
            <div className="flex gap-1">
              {(Object.keys(TIME_RANGE_PRESETS) as TimeRangeKey[]).map((key) => (
                <button
                  key={key}
                  onClick={() => {
                    setUseCustomRange(false);
                    setRangeKey(key);
                  }}
                  className={`px-3 py-2 text-sm rounded-md border ${
                    !useCustomRange && rangeKey === key
                      ? 'border-indigo-500 bg-indigo-50 text-indigo-700'
                      : 'border-gray-300 bg-white text-gray-700 hover:bg-gray-50'
                  }`}
                >
                  {TIME_RANGE_PRESETS[key].label}
                </button>
              ))}
              <button
                onClick={() => setUseCustomRange(true)}
                className={`px-3 py-2 text-sm rounded-md border ${
                  useCustomRange
                    ? 'border-indigo-500 bg-indigo-50 text-indigo-700'
                    : 'border-gray-300 bg-white text-gray-700 hover:bg-gray-50'
                }`}
              >
                Custom
              </button>
            </div>
          </div>

          {useCustomRange && (
            <>
              <div>
                <label className="block text-xs font-medium text-gray-500 mb-1">From</label>
                <input
                  type="datetime-local"
                  value={customFrom}
                  onChange={(e) => setCustomFrom(e.target.value)}
                  className="block rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                />
              </div>
              <div>
                <label className="block text-xs font-medium text-gray-500 mb-1">To</label>
                <input
                  type="datetime-local"
                  value={customTo}
                  onChange={(e) => setCustomTo(e.target.value)}
                  className="block rounded-md border-gray-300 shadow-sm focus:border-indigo-500 focus:ring-indigo-500 sm:text-sm"
                />
              </div>
            </>
          )}

          <div className="ml-auto flex items-center gap-2">
            <span className="text-xs text-gray-400 uppercase tracking-wide">
              Aggregation: <span className="font-semibold text-gray-600">{activeAggregation}</span>
            </span>
            <button
              onClick={() => handleExport('csv')}
              disabled={!deviceId || exporting !== null}
              className="inline-flex items-center px-3 py-2 text-sm font-medium rounded-md border border-gray-300 bg-white text-gray-700 hover:bg-gray-50 disabled:opacity-50"
            >
              {exporting === 'csv' ? 'Exporting...' : 'Export CSV'}
            </button>
            <button
              onClick={() => handleExport('json')}
              disabled={!deviceId || exporting !== null}
              className="inline-flex items-center px-3 py-2 text-sm font-medium rounded-md border border-gray-300 bg-white text-gray-700 hover:bg-gray-50 disabled:opacity-50"
            >
              {exporting === 'json' ? 'Exporting...' : 'Export JSON'}
            </button>
          </div>
        </div>

        {/* Latest values */}
        {latest && latest.metrics.length > 0 && (
          <div className="mt-6 grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-4 gap-4">
            {latest.metrics.map((m) => (
              <div key={m.metricName} className="bg-white rounded-lg shadow px-5 py-4">
                <dt className="text-sm font-medium text-gray-500 truncate">{m.metricName}</dt>
                <dd className="mt-1 text-3xl font-semibold text-gray-900">
                  {m.numericValue !== undefined && m.numericValue !== null
                    ? m.numericValue.toFixed(2)
                    : m.stringValue || '-'}
                  {m.unit && <span className="text-base font-normal text-gray-500 ml-1">{m.unit}</span>}
                </dd>
                <p className="mt-1 text-xs text-gray-500">{new Date(m.timestamp).toLocaleString()}</p>
              </div>
            ))}
          </div>
        )}

        {/* Chart */}
        <div className="mt-6 bg-white rounded-lg shadow p-6">
          <div className="flex items-center justify-between mb-4">
            <h3 className="text-lg font-medium text-gray-900">
              {metricName || 'Select a metric'} {totalCount > 0 && <span className="text-sm text-gray-400 font-normal">({totalCount} points)</span>}
            </h3>
          </div>

          {loading ? (
            <div className="animate-pulse" style={{ height: 280 }}>
              <div className="h-full bg-gray-100 rounded" />
            </div>
          ) : (
            <TelemetryLineChart
              series={
                chartPoints.length > 0
                  ? [{ label: metricName, color: '#6366f1', points: chartPoints }]
                  : []
              }
            />
          )}
        </div>

        {/* Deferred scope, noted explicitly rather than faked:
            - Real-time SignalR subscription: no @microsoft/signalr dependency exists in this
              app yet and this session has no browser to verify a live socket connection, so
              wiring it in untested would be worse than not wiring it in (see backend TelemetryHub
              changes in this same PR for the server-side half of this feature).
            - Gauge / heatmap chart types and the customizable 2x2 drag-to-resize grid layout.
            Use the Refresh button above for near-real-time updates in the meantime. */}
      </div>
    </DashboardLayout>
  );
}
