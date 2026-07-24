interface LinePoint {
  timestamp: string;
  value: number;
}

interface TelemetryLineChartProps {
  series: Array<{ label: string; color: string; points: LinePoint[] }>;
  width?: number;
  height?: number;
  unit?: string;
}

/** Minimal dependency-free SVG line chart, following the same raw-SVG approach as SimpleBarChart. */
export default function TelemetryLineChart({ series, width = 700, height = 280, unit }: TelemetryLineChartProps) {
  const allPoints = series.flatMap((s) => s.points);
  if (allPoints.length === 0) {
    return (
      <div className="flex items-center justify-center text-gray-400 text-sm" style={{ width, height }}>
        No data available for this range
      </div>
    );
  }

  const padding = { top: 16, right: 16, bottom: 28, left: 56 };
  const chartWidth = width - padding.left - padding.right;
  const chartHeight = height - padding.top - padding.bottom;

  const times = allPoints.map((p) => new Date(p.timestamp).getTime());
  const values = allPoints.map((p) => p.value);
  const minTime = Math.min(...times);
  const maxTime = Math.max(...times);
  const minValue = Math.min(0, ...values);
  const maxValue = Math.max(...values, minValue + 1);

  const x = (t: number) => (maxTime === minTime ? 0 : ((t - minTime) / (maxTime - minTime)) * chartWidth);
  const y = (v: number) => chartHeight - ((v - minValue) / (maxValue - minValue)) * chartHeight;

  const yTicks = 4;
  const yTickValues = Array.from({ length: yTicks + 1 }, (_, i) => minValue + ((maxValue - minValue) * i) / yTicks);

  return (
    <svg width={width} height={height}>
      <g transform={`translate(${padding.left},${padding.top})`}>
        {/* Y gridlines + labels */}
        {yTickValues.map((v, i) => (
          <g key={i}>
            <line x1={0} x2={chartWidth} y1={y(v)} y2={y(v)} className="stroke-gray-100" strokeWidth={1} />
            <text x={-8} y={y(v)} textAnchor="end" dominantBaseline="middle" className="fill-gray-400 text-xs">
              {v.toFixed(1)}
            </text>
          </g>
        ))}

        {/* X axis start/end labels */}
        <text x={0} y={chartHeight + 18} textAnchor="start" className="fill-gray-400 text-xs">
          {new Date(minTime).toLocaleString()}
        </text>
        <text x={chartWidth} y={chartHeight + 18} textAnchor="end" className="fill-gray-400 text-xs">
          {new Date(maxTime).toLocaleString()}
        </text>

        {/* One polyline per series */}
        {series.map((s) => (
          <g key={s.label}>
            <polyline
              fill="none"
              stroke={s.color}
              strokeWidth={2}
              points={s.points.map((p) => `${x(new Date(p.timestamp).getTime())},${y(p.value)}`).join(' ')}
            />
            {s.points.map((p, i) => (
              <circle
                key={i}
                cx={x(new Date(p.timestamp).getTime())}
                cy={y(p.value)}
                r={2.5}
                fill={s.color}
              >
                <title>
                  {s.label}: {p.value.toFixed(2)}
                  {unit ? ` ${unit}` : ''} @ {new Date(p.timestamp).toLocaleString()}
                </title>
              </circle>
            ))}
          </g>
        ))}
      </g>
    </svg>
  );
}
