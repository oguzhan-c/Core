import { useMemo, useState } from "react";
import { formatCompact, formatValue, type FormatOptions } from "./format";
import { cellValue, type ReportResult } from "./types";

export type ReportChartType = "bar" | "line" | "pie";

export interface ReportChartProps extends FormatOptions {
  result: ReportResult;
  type?: ReportChartType;
  /** Çizilecek değerin sırası (varsayılan ilk değer). */
  measure?: number;
  /** En fazla kategori (fazlası kesilir). */
  maxCategories?: number;
  height?: number;
  noDataLabel?: string;
  className?: string;
}

interface Series {
  name: string;
  values: (number | null)[];
}

interface ChartData {
  categories: string[];
  series: Series[];
}

/**
 * Kategoriler: en alt düzey satırlar (yoksa genel toplam); seriler: en alt düzey sütunlar (yoksa genel toplam sütunu).
 * Ara toplamlar grafiğe girmez.
 */
export function chartData(result: ReportResult, measure: number, maxCategories: number): ChartData {
  const rowDepth = result.rowDimensions.length;
  const colDepth = result.columnDimensions.length;
  const rows = result.rows
    .map((row, index) => ({ row, index }))
    .filter(({ row }) => (rowDepth === 0 ? row.kind === "GrandTotal" : row.kind !== "GrandTotal" && row.level === rowDepth))
    .slice(0, maxCategories);
  const columns = result.columns
    .map((column, index) => ({ column, index }))
    .filter(({ column }) => (colDepth === 0 ? column.kind === "GrandTotal" : column.kind !== "GrandTotal" && column.labels.length === colDepth));

  return {
    categories: rows.map(({ row }) => (rowDepth === 0 ? row.label : row.labels.join(" / "))),
    series: columns.map(({ column, index: c }) => ({
      name: colDepth === 0 ? (result.measures[measure]?.caption ?? "") : column.labels.join(" / "),
      values: rows.map(({ index: r }) => {
        const value = cellValue(result, r, c, measure);
        return typeof value === "number" ? value : null;
      }),
    })),
  };
}

/** Okunur eksen aralıkları (1, 2, 2.5, 5 × 10ⁿ). */
export function niceTicks(min: number, max: number, count = 5): number[] {
  if (min === max) {
    max = min === 0 ? 1 : min + Math.abs(min);
  }
  const span = max - min;
  const raw = span / count;
  const magnitude = 10 ** Math.floor(Math.log10(raw));
  const step = [1, 2, 2.5, 5, 10].map((f) => f * magnitude).find((s) => span / s <= count) ?? 10 * magnitude;
  const start = Math.floor(min / step) * step;
  const ticks: number[] = [];
  for (let v = start; v <= max + step * 1e-9; v += step) ticks.push(Number(v.toPrecision(12)));
  if (ticks[ticks.length - 1] < max) ticks.push(Number((ticks[ticks.length - 1] + step).toPrecision(12)));
  return ticks;
}

const WIDTH = 800;

function truncate(text: string, max: number): string {
  return text.length > max ? `${text.slice(0, max - 1)}…` : text;
}

/** Bağımlılıksız SVG grafik: gruplu sütun, çizgi ya da pasta. */
export function ReportChart({
  result,
  type = "bar",
  measure = 0,
  maxCategories = 40,
  height = 360,
  noDataLabel = "Veri yok",
  className,
  locale,
  currency,
}: ReportChartProps) {
  const data = useMemo(() => chartData(result, measure, maxCategories), [result, measure, maxCategories]);
  const [hidden, setHidden] = useState<Set<string>>(() => new Set());
  const format = result.measures[measure]?.format;
  const series = data.series.filter((s) => !hidden.has(s.name));
  const all = series.flatMap((s) => s.values).filter((v): v is number => v !== null);

  if (data.categories.length === 0 || data.series.length === 0 || all.length === 0) {
    return <div className={["cr-chart cr-chart-empty", className].filter(Boolean).join(" ")}>{noDataLabel}</div>;
  }

  const legend =
    data.series.length > 1 ? (
      <div className="cr-legend">
        {data.series.map((s, i) => (
          <button
            key={s.name}
            type="button"
            className={hidden.has(s.name) ? "cr-legend-item cr-legend-off" : "cr-legend-item"}
            onClick={() =>
              setHidden((current) => {
                const next = new Set(current);
                if (next.has(s.name)) next.delete(s.name);
                else next.add(s.name);
                return next;
              })
            }
          >
            <span className="cr-swatch" style={{ background: `var(--cr-chart-${(i % 8) + 1})` }} />
            {s.name}
          </button>
        ))}
      </div>
    ) : null;

  const colorOf = (name: string) => `var(--cr-chart-${(data.series.findIndex((s) => s.name === name) % 8) + 1})`;

  if (type === "pie") {
    const first = series[0];
    const slices = data.categories
      .map((category, i) => ({ category, value: first.values[i] ?? 0, color: `var(--cr-chart-${(i % 8) + 1})` }))
      .filter((s) => s.value > 0);
    const total = slices.reduce((sum, s) => sum + s.value, 0);
    const cx = 180;
    const cy = height / 2;
    const radius = Math.min(cy - 20, 160);
    let angle = -Math.PI / 2;
    return (
      <div className={["cr-chart", className].filter(Boolean).join(" ")}>
        <svg viewBox={`0 0 ${WIDTH} ${height}`} role="img" aria-label={first.name}>
          {slices.map((slice) => {
            const sweep = (slice.value / total) * Math.PI * 2;
            const start = angle;
            angle += sweep;
            const large = sweep > Math.PI ? 1 : 0;
            const x1 = cx + radius * Math.cos(start);
            const y1 = cy + radius * Math.sin(start);
            const x2 = cx + radius * Math.cos(angle);
            const y2 = cy + radius * Math.sin(angle);
            const path =
              slices.length === 1
                ? `M ${cx - radius} ${cy} a ${radius} ${radius} 0 1 0 ${radius * 2} 0 a ${radius} ${radius} 0 1 0 ${-radius * 2} 0`
                : `M ${cx} ${cy} L ${x1} ${y1} A ${radius} ${radius} 0 ${large} 1 ${x2} ${y2} Z`;
            return (
              <path key={slice.category} d={path} fill={slice.color} stroke="var(--cr-bg, #fff)" strokeWidth={1.5}>
                <title>{`${slice.category}: ${formatValue(slice.value, format, { locale, currency })} (${formatValue(slice.value / total, "P1", { locale })})`}</title>
              </path>
            );
          })}
          {slices.slice(0, 14).map((slice, i) => (
            <g key={slice.category} transform={`translate(${cx + radius + 50}, ${30 + i * 22})`}>
              <rect width={12} height={12} y={-10} fill={slice.color} rx={2} />
              <text x={18} className="cr-chart-text">
                {truncate(slice.category, 32)} · {formatValue(slice.value / total, "P1", { locale })}
              </text>
            </g>
          ))}
        </svg>
      </div>
    );
  }

  const left = 64;
  const right = 16;
  const top = 16;
  const longest = Math.max(...data.categories.map((c) => c.length));
  const rotate = data.categories.length > 8 || longest > 12;
  const bottom = rotate ? Math.min(120, 24 + Math.min(longest, 20) * 5.5) : 32;
  const plotWidth = WIDTH - left - right;
  const plotHeight = height - top - bottom;
  const ticks = niceTicks(Math.min(0, ...all), Math.max(0, ...all));
  const low = ticks[0];
  const high = ticks[ticks.length - 1];
  const y = (v: number) => top + plotHeight - ((v - low) / (high - low)) * plotHeight;
  const band = plotWidth / data.categories.length;
  const x = (i: number) => left + band * i + band / 2;

  return (
    <div className={["cr-chart", className].filter(Boolean).join(" ")}>
      {legend}
      <svg viewBox={`0 0 ${WIDTH} ${height}`} role="img" aria-label={result.measures[measure]?.caption}>
        {ticks.map((t) => (
          <g key={t}>
            <line x1={left} x2={WIDTH - right} y1={y(t)} y2={y(t)} className={t === 0 ? "cr-axis" : "cr-grid"} />
            <text x={left - 6} y={y(t) + 4} textAnchor="end" className="cr-chart-text">
              {formatCompact(t, locale)}
            </text>
          </g>
        ))}
        {data.categories.map((category, i) => (
          <text
            key={`${category}-${i}`}
            className="cr-chart-text"
            textAnchor={rotate ? "end" : "middle"}
            transform={rotate ? `translate(${x(i)}, ${top + plotHeight + 12}) rotate(-40)` : `translate(${x(i)}, ${top + plotHeight + 18})`}
          >
            {truncate(category, rotate ? 20 : Math.max(4, Math.floor(band / 7)))}
          </text>
        ))}
        {type === "bar" &&
          series.map((s, si) => {
            const inner = (band * 0.8) / series.length;
            return s.values.map((value, i) =>
              value === null ? null : (
                <rect
                  key={`${s.name}-${i}`}
                  x={left + band * i + band * 0.1 + inner * si}
                  width={Math.max(1, inner - 1)}
                  y={Math.min(y(value), y(0))}
                  height={Math.max(1, Math.abs(y(value) - y(0)))}
                  fill={colorOf(s.name)}
                  rx={1.5}
                >
                  <title>{`${data.categories[i]}${series.length > 1 ? ` · ${s.name}` : ""}: ${formatValue(value, format, { locale, currency })}`}</title>
                </rect>
              ),
            );
          })}
        {type === "line" &&
          series.map((s) => {
            const points = s.values.map((value, i) => (value === null ? null : `${x(i)},${y(value)}`)).filter(Boolean);
            return (
              <g key={s.name}>
                <polyline points={points.join(" ")} fill="none" stroke={colorOf(s.name)} strokeWidth={2} />
                {s.values.map((value, i) =>
                  value === null ? null : (
                    <circle key={i} cx={x(i)} cy={y(value)} r={3} fill={colorOf(s.name)}>
                      <title>{`${data.categories[i]}${series.length > 1 ? ` · ${s.name}` : ""}: ${formatValue(value, format, { locale, currency })}`}</title>
                    </circle>
                  ),
                )}
              </g>
            );
          })}
      </svg>
    </div>
  );
}
