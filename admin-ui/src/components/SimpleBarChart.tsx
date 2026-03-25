interface BarData {
  label: string;
  value: number;
  color?: string;
}

interface SimpleBarChartProps {
  data: BarData[];
  width?: number;
  height?: number;
  title?: string;
}

export default function SimpleBarChart({ data, width = 500, height = 300, title }: SimpleBarChartProps) {
  if (!data || data.length === 0) {
    return (
      <div className="flex items-center justify-center text-gray-400 text-sm" style={{ width, height }}>
        No data available
      </div>
    );
  }

  const maxValue = Math.max(...data.map(d => d.value), 1);
  const barHeight = Math.min(32, (height - 40) / data.length - 4);
  const labelWidth = 120;
  const valueWidth = 50;
  const chartWidth = width - labelWidth - valueWidth - 20;
  const totalBarsHeight = data.length * (barHeight + 4);
  const svgHeight = Math.max(height, totalBarsHeight + 40);

  return (
    <div>
      {title && (
        <h3 className="text-sm font-medium text-gray-700 mb-2">{title}</h3>
      )}
      <svg width={width} height={svgHeight} className="overflow-visible">
        {data.map((item, index) => {
          const y = index * (barHeight + 4) + 4;
          const barWidth = (item.value / maxValue) * chartWidth;
          const color = item.color || '#6366f1';

          return (
            <g key={index}>
              {/* Label */}
              <text
                x={labelWidth - 8}
                y={y + barHeight / 2 + 1}
                textAnchor="end"
                dominantBaseline="middle"
                className="fill-gray-600 text-xs"
              >
                {item.label.length > 16 ? item.label.slice(0, 14) + '...' : item.label}
              </text>

              {/* Background bar */}
              <rect
                x={labelWidth}
                y={y}
                width={chartWidth}
                height={barHeight}
                rx={4}
                className="fill-gray-100"
              />

              {/* Value bar */}
              <rect
                x={labelWidth}
                y={y}
                width={Math.max(barWidth, 2)}
                height={barHeight}
                rx={4}
                fill={color}
                className="opacity-80 hover:opacity-100 transition-opacity"
              />

              {/* Value text */}
              <text
                x={labelWidth + chartWidth + 8}
                y={y + barHeight / 2 + 1}
                textAnchor="start"
                dominantBaseline="middle"
                className="fill-gray-700 text-xs font-medium"
              >
                {item.value.toLocaleString()}
              </text>
            </g>
          );
        })}
      </svg>
    </div>
  );
}
