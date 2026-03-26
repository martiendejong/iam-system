interface TrendData {
  date: string;
  value: number;
}

interface SimpleTrendLineProps {
  data: TrendData[];
  width?: number;
  height?: number;
  title?: string;
  color?: string;
}

export default function SimpleTrendLine({ data, width = 500, height = 200, title, color = '#6366f1' }: SimpleTrendLineProps) {
  if (!data || data.length === 0) {
    return (
      <div className="flex items-center justify-center text-gray-400 text-sm" style={{ width, height }}>
        No data available
      </div>
    );
  }

  const padding = { top: 20, right: 20, bottom: 40, left: 50 };
  const chartWidth = width - padding.left - padding.right;
  const chartHeight = height - padding.top - padding.bottom;

  const values = data.map(d => d.value);
  const minValue = Math.min(...values);
  const maxValue = Math.max(...values, 1);
  const valueRange = maxValue - minValue || 1;

  // Build points for the polyline
  const points = data.map((d, i) => {
    const x = padding.left + (i / Math.max(data.length - 1, 1)) * chartWidth;
    const y = padding.top + chartHeight - ((d.value - minValue) / valueRange) * chartHeight;
    return `${x},${y}`;
  }).join(' ');

  // Build area fill path
  const areaPoints = data.map((d, i) => {
    const x = padding.left + (i / Math.max(data.length - 1, 1)) * chartWidth;
    const y = padding.top + chartHeight - ((d.value - minValue) / valueRange) * chartHeight;
    return { x, y };
  });

  const areaPath = `M ${areaPoints[0].x},${padding.top + chartHeight} ` +
    areaPoints.map(p => `L ${p.x},${p.y}`).join(' ') +
    ` L ${areaPoints[areaPoints.length - 1].x},${padding.top + chartHeight} Z`;

  // Select label indices to avoid crowding
  const maxLabels = 7;
  const step = Math.max(1, Math.ceil(data.length / maxLabels));

  // Y-axis ticks
  const yTicks = 4;
  const yTickValues = Array.from({ length: yTicks + 1 }, (_, i) =>
    minValue + (valueRange / yTicks) * i
  );

  return (
    <div>
      {title && (
        <h3 className="text-sm font-medium text-gray-700 mb-2">{title}</h3>
      )}
      <svg width={width} height={height} className="overflow-visible">
        {/* Y-axis grid lines and labels */}
        {yTickValues.map((val, i) => {
          const y = padding.top + chartHeight - ((val - minValue) / valueRange) * chartHeight;
          return (
            <g key={`y-${i}`}>
              <line
                x1={padding.left}
                y1={y}
                x2={padding.left + chartWidth}
                y2={y}
                className="stroke-gray-200"
                strokeWidth={1}
              />
              <text
                x={padding.left - 8}
                y={y + 1}
                textAnchor="end"
                dominantBaseline="middle"
                className="fill-gray-400 text-xs"
              >
                {Math.round(val).toLocaleString()}
              </text>
            </g>
          );
        })}

        {/* Area fill */}
        <path
          d={areaPath}
          fill={color}
          opacity={0.1}
        />

        {/* Trend line */}
        <polyline
          points={points}
          fill="none"
          stroke={color}
          strokeWidth={2}
          strokeLinecap="round"
          strokeLinejoin="round"
        />

        {/* Data points */}
        {areaPoints.map((p, i) => (
          <circle
            key={i}
            cx={p.x}
            cy={p.y}
            r={3}
            fill="white"
            stroke={color}
            strokeWidth={2}
            className="opacity-0 hover:opacity-100 transition-opacity"
          />
        ))}

        {/* X-axis date labels */}
        {data.map((d, i) => {
          if (i % step !== 0 && i !== data.length - 1) return null;
          const x = padding.left + (i / Math.max(data.length - 1, 1)) * chartWidth;
          const dateStr = d.date.length >= 10 ? d.date.slice(5, 10) : d.date;
          return (
            <text
              key={`x-${i}`}
              x={x}
              y={height - 8}
              textAnchor="middle"
              className="fill-gray-400 text-xs"
            >
              {dateStr}
            </text>
          );
        })}

        {/* Min/Max annotations */}
        {data.length > 1 && (
          <>
            <text
              x={width - padding.right}
              y={padding.top - 4}
              textAnchor="end"
              className="fill-gray-400 text-xs"
            >
              max: {maxValue.toLocaleString()}
            </text>
            <text
              x={width - padding.right}
              y={padding.top + chartHeight + 14}
              textAnchor="end"
              className="fill-gray-400 text-xs"
            >
              min: {minValue.toLocaleString()}
            </text>
          </>
        )}
      </svg>
    </div>
  );
}
