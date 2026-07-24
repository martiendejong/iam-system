import { api } from './api';

const client = () => api.getClient();

export interface TelemetryRecordDto {
  id: string;
  deviceId: string;
  metricName: string;
  numericValue?: number;
  stringValue?: string;
  jsonValue?: string;
  unit?: string;
  deviceType?: string;
  tenantId?: string;
  tags?: string;
  timestamp: string;
}

export interface TelemetryQueryResult {
  records: TelemetryRecordDto[];
  totalCount: number;
  earliestTimestamp?: string;
  latestTimestamp?: string;
}

export interface TelemetryAggregationBucket {
  bucketStart: string;
  bucketEnd: string;
  value: number;
  count: number;
  groupKey?: string;
}

export interface TelemetryAggregationResult {
  metricName: string;
  aggregation: string;
  interval: string;
  buckets: TelemetryAggregationBucket[];
}

export interface TelemetryLatestResult {
  deviceId: string;
  metrics: Array<{
    metricName: string;
    numericValue?: number;
    stringValue?: string;
    jsonValue?: string;
    unit?: string;
    deviceType?: string;
    tenantId?: string;
    tags?: string;
    timestamp: string;
  }>;
}

export interface TelemetryStatistics {
  totalRecords: number;
  uniqueDevices: number;
  uniqueMetrics: number;
  recordsToday: number;
  oldestRecord?: string;
  newestRecord?: string;
}

/** Maps a dashboard time-range preset to an ISO `from` timestamp and matching aggregation. */
export const TIME_RANGE_PRESETS = {
  '1h': { label: 'Last 1h', hours: 1, aggregation: 'raw' as const },
  '6h': { label: 'Last 6h', hours: 6, aggregation: 'raw' as const },
  '24h': { label: 'Last 24h', hours: 24, aggregation: '5m' as const },
  '7d': { label: 'Last 7d', hours: 24 * 7, aggregation: '1h' as const },
  '30d': { label: 'Last 30d', hours: 24 * 30, aggregation: '1h' as const },
};
export type TimeRangeKey = keyof typeof TIME_RANGE_PRESETS;

export const telemetryApi = {
  query: async (params: {
    deviceId?: string;
    metricName?: string;
    startTime: string;
    endTime: string;
    limit?: number;
  }): Promise<TelemetryQueryResult> => {
    const query = new URLSearchParams();
    if (params.deviceId) query.append('deviceId', params.deviceId);
    if (params.metricName) query.append('metricName', params.metricName);
    query.append('startTime', params.startTime);
    query.append('endTime', params.endTime);
    if (params.limit) query.append('limit', String(params.limit));
    const response = await client().get(`/telemetry/query?${query}`);
    return response.data;
  },

  aggregate: async (params: {
    metricName: string;
    deviceId?: string;
    aggregation?: string;
    interval?: string;
    startTime: string;
    endTime: string;
    groupBy?: string;
  }): Promise<TelemetryAggregationResult> => {
    const query = new URLSearchParams();
    query.append('metricName', params.metricName);
    if (params.deviceId) query.append('deviceId', params.deviceId);
    if (params.aggregation) query.append('aggregation', params.aggregation);
    if (params.interval) query.append('interval', params.interval);
    query.append('startTime', params.startTime);
    query.append('endTime', params.endTime);
    if (params.groupBy) query.append('groupBy', params.groupBy);
    const response = await client().get(`/telemetry/aggregate?${query}`);
    return response.data;
  },

  getLatest: async (deviceId: string): Promise<TelemetryLatestResult> => {
    const response = await client().get(`/telemetry/latest/${encodeURIComponent(deviceId)}`);
    return response.data;
  },

  getMetricNames: async (deviceId?: string): Promise<string[]> => {
    const query = deviceId ? `?deviceId=${encodeURIComponent(deviceId)}` : '';
    const response = await client().get(`/telemetry/metrics${query}`);
    return response.data.metrics;
  },

  getStatistics: async (tenantId?: string): Promise<TelemetryStatistics> => {
    const query = tenantId ? `?tenantId=${tenantId}` : '';
    const response = await client().get(`/telemetry/statistics${query}`);
    return response.data;
  },

  /** Downloads the current query as a file and triggers a browser save. */
  exportAndDownload: async (
    params: { deviceId?: string; metricName?: string; startTime: string; endTime: string },
    format: 'csv' | 'json'
  ): Promise<void> => {
    const query = new URLSearchParams();
    if (params.deviceId) query.append('deviceId', params.deviceId);
    if (params.metricName) query.append('metricName', params.metricName);
    query.append('startTime', params.startTime);
    query.append('endTime', params.endTime);

    const response = await client().get(`/telemetry/export?${query}`, {
      headers: { Accept: format === 'csv' ? 'text/csv' : 'application/json' },
      responseType: format === 'csv' ? 'blob' : 'json',
    });

    const blob =
      format === 'csv'
        ? response.data
        : new Blob([JSON.stringify(response.data, null, 2)], { type: 'application/json' });
    const url = URL.createObjectURL(blob);
    const link = document.createElement('a');
    link.href = url;
    link.download = `telemetry-export-${Date.now()}.${format}`;
    document.body.appendChild(link);
    link.click();
    link.remove();
    URL.revokeObjectURL(url);
  },
};
