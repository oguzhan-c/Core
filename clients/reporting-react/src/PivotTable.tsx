import { useMemo, useState, type ReactNode } from "react";
import { formatValue, type FormatOptions } from "./format";
import { cellValue, type ReportHeader, type ReportResult } from "./types";

export interface PivotTableProps extends FormatOptions {
  result: ReportResult;
  /** Ara toplam sütunlarının başlığı. */
  totalLabel?: string;
  /** Bundan fazla satır çizilmez (tarayıcıyı korumak için); tamamı dışa aktarmada. */
  maxRows?: number;
  /** Satır grupları daraltılabilsin (▸/▾). */
  collapsible?: boolean;
  className?: string;
  /** Hücreye tıklanınca (ayrıntıya inmek için): satır, sütun ve değer sırası. */
  onCellClick?: (row: number, column: number, measure: number) => void;
}

interface HeaderCell {
  key: string;
  label: string;
  colSpan: number;
  rowSpan: number;
  total: boolean;
}

const keyOf = (header: ReportHeader) => JSON.stringify(header.keys);

/** Sütun başlıklarını satırlara dizer: üst gruplar alt sütunlarının (ve ara toplamının) üstünde birleşir. */
function columnHeaderRows(result: ReportResult, totalLabel: string): HeaderCell[][] {
  const levels = result.columnDimensions.length;
  const measures = result.measures.length;
  const rows: HeaderCell[][] = [];

  for (let level = 1; level <= levels; level++) {
    const line: HeaderCell[] = [];
    for (let c = 0; c < result.columns.length; c++) {
      const column = result.columns[c];
      const depth = column.labels.length;

      if (column.kind === "GrandTotal") {
        if (level === 1) line.push({ key: `g${c}`, label: column.label, colSpan: measures, rowSpan: levels, total: true });
        continue;
      }

      if (level <= depth) {
        // aynı üst gruba ait ardışık sütunları birleştir (en alt düzeyde her sütun ayrı)
        let end = c;
        if (level < levels) {
          while (end + 1 < result.columns.length) {
            const next = result.columns[end + 1];
            if (next.kind === "GrandTotal" || next.labels.length < level) break;
            let same = true;
            for (let i = 0; i < level; i++) {
              if (JSON.stringify(next.keys[i]) !== JSON.stringify(column.keys[i])) same = false;
            }
            if (!same) break;
            end++;
          }
        }
        line.push({ key: `${level}-${c}`, label: column.labels[level - 1], colSpan: (end - c + 1) * measures, rowSpan: 1, total: false });
        c = end;
      } else if (level === depth + 1) {
        line.push({ key: `t${c}`, label: totalLabel, colSpan: measures, rowSpan: levels - depth, total: true });
      }
    }
    rows.push(line);
  }

  if (measures > 1 || levels === 0) {
    const line: HeaderCell[] = [];
    result.columns.forEach((column, c) =>
      result.measures.forEach((measure, m) =>
        line.push({ key: `m${c}-${m}`, label: measure.caption, colSpan: 1, rowSpan: 1, total: column.kind === "GrandTotal" }),
      ),
    );
    rows.push(line);
  }

  return rows;
}

/** Rapor sonucunu çapraz tablo olarak çizer (kompakt düzen: satır grupları girintili, ara toplam grup satırında). */
export function PivotTable({
  result,
  totalLabel = "Toplam",
  maxRows = 5000,
  collapsible = true,
  className,
  onCellClick,
  locale,
  currency,
}: PivotTableProps) {
  const [collapsed, setCollapsed] = useState<Set<string>>(() => new Set());
  const headerRows = useMemo(() => columnHeaderRows(result, totalLabel), [result, totalLabel]);
  const rowCaption = result.rowDimensions.map((d) => d.caption).join(" / ");

  // daraltılmış grupların altındaki satırlar gizlenir (satırlar üst-önce sıralı)
  const visible = useMemo(() => {
    const rows: number[] = [];
    let hiddenBelow: number | null = null;
    result.rows.forEach((row, r) => {
      if (hiddenBelow !== null && row.kind !== "GrandTotal" && row.level > hiddenBelow) return;
      hiddenBelow = null;
      rows.push(r);
      if (row.kind === "Group" && collapsed.has(keyOf(row))) hiddenBelow = row.level;
    });
    return rows;
  }, [result, collapsed]);

  const toggle = (row: ReportHeader) =>
    setCollapsed((current) => {
      const next = new Set(current);
      const key = keyOf(row);
      if (next.has(key)) next.delete(key);
      else next.add(key);
      return next;
    });

  const shown = visible.slice(0, maxRows);

  return (
    <div className={["cr-table-wrap", className].filter(Boolean).join(" ")}>
      <table className="cr-table">
        <thead>
          {headerRows.map((line, h) => (
            <tr key={h}>
              {h === 0 && (
                <th className="cr-corner" rowSpan={headerRows.length} scope="col">
                  {rowCaption}
                </th>
              )}
              {line.map((cell) => (
                <th
                  key={cell.key}
                  colSpan={cell.colSpan}
                  rowSpan={cell.rowSpan}
                  scope="colgroup"
                  className={cell.total ? "cr-th cr-th-total" : "cr-th"}
                >
                  {cell.label}
                </th>
              ))}
            </tr>
          ))}
        </thead>
        <tbody>
          {shown.map((r) => {
            const row = result.rows[r];
            const kind = row.kind === "GrandTotal" ? "cr-row-total" : row.kind === "Group" ? "cr-row-group" : row.kind === "Others" ? "cr-row-others" : "";
            const indent = row.kind === "GrandTotal" ? 0 : Math.max(0, row.level - 1);
            let label: ReactNode = row.label;
            if (collapsible && row.kind === "Group") {
              const isCollapsed = collapsed.has(keyOf(row));
              label = (
                <button type="button" className="cr-toggle" aria-expanded={!isCollapsed} onClick={() => toggle(row)}>
                  <span aria-hidden="true">{isCollapsed ? "▸" : "▾"}</span> {row.label}
                </button>
              );
            }
            return (
              <tr key={r} className={kind}>
                <th scope="row" className="cr-row-header" style={{ paddingLeft: `${0.6 + indent * 1.25}em` }}>
                  {label}
                </th>
                {result.columns.map((column, c) =>
                  result.measures.map((measure, m) => {
                    const value = cellValue(result, r, c, m);
                    return (
                      <td
                        key={`${c}-${m}`}
                        className={[
                          typeof value === "number" ? "cr-num" : "",
                          column.kind === "GrandTotal" || column.labels.length < result.columnDimensions.length ? "cr-col-total" : "",
                          typeof value === "number" && value < 0 ? "cr-negative" : "",
                          onCellClick ? "cr-clickable" : "",
                        ]
                          .filter(Boolean)
                          .join(" ")}
                        onClick={onCellClick ? () => onCellClick(r, c, m) : undefined}
                      >
                        {formatValue(value, measure.format, { locale, currency })}
                      </td>
                    );
                  }),
                )}
              </tr>
            );
          })}
        </tbody>
      </table>
      {visible.length > maxRows && (
        <p className="cr-muted cr-truncated">
          {maxRows.toLocaleString(locale)} / {visible.length.toLocaleString(locale)}
        </p>
      )}
    </div>
  );
}
