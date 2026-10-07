import { useState, type ReactNode } from "react";
import { measureCaption, normalize, parseValues, type DesignerDefinition, type Zone } from "./designer-model";
import type { ReportingLabels } from "./labels";
import type {
  DateGrouping,
  DimensionSort,
  FilterOperator,
  MeasureDisplay,
  ReportAggregate,
  ReportDataType,
  ReportDimension,
  ReportField,
  ReportFilter,
  ReportMeasure,
  ReportScalar,
} from "./types";

interface ItemSettingsProps {
  definition: DesignerDefinition;
  zone: Zone;
  index: number;
  fields: ReportField[];
  labels: ReportingLabels;
  onChange: (definition: DesignerDefinition) => void;
  onClose: () => void;
}

const groupings: DateGrouping[] = ["Year", "YearQuarter", "YearMonth", "YearWeek", "Date", "Quarter", "Month", "DayOfWeek", "DayOfMonth", "Hour", "None"];
const sorts: DimensionSort[] = ["KeyAscending", "KeyDescending", "MeasureDescending", "MeasureAscending"];
const numericAggregates: ReportAggregate[] = ["Sum", "Count", "CountDistinct", "Average", "Min", "Max", "Median", "Percentile", "StdDev", "StdDevP", "Var", "VarP", "First", "Last"];
const otherAggregates: ReportAggregate[] = ["Count", "CountDistinct", "Min", "Max", "First", "Last"];
const displays: MeasureDisplay[] = [
  "Value",
  "PercentOfGrandTotal",
  "PercentOfColumnTotal",
  "PercentOfRowTotal",
  "PercentOfParentRow",
  "PercentOfParentColumn",
  "RunningTotal",
  "DifferenceFromPrevious",
  "PercentDifferenceFromPrevious",
  "Rank",
];
const formats = ["", "N0", "N2", "C2", "P1", "F2", "d"];

const operatorsByType: Record<ReportDataType, FilterOperator[]> = {
  String: ["In", "NotIn", "Equal", "NotEqual", "Contains", "StartsWith", "IsNull", "IsNotNull"],
  Number: ["GreaterThanOrEqual", "LessThanOrEqual", "GreaterThan", "LessThan", "Between", "Equal", "NotEqual", "In", "NotIn", "IsNull", "IsNotNull"],
  Date: ["Between", "GreaterThanOrEqual", "LessThanOrEqual", "Equal", "IsNull", "IsNotNull"],
  Boolean: ["Equal", "IsNull", "IsNotNull"],
};

/** Seçili çipin ayarları (boyut, değer ya da filtre). Değişiklikler anında uygulanır. */
export function ItemSettings({ definition, zone, index, fields, labels, onChange, onClose }: ItemSettingsProps) {
  const item = definition[zone][index];
  const field = fields.find((f) => f.name === item?.field);

  const replace = (value: ReportDimension | ReportMeasure | ReportFilter) => {
    const next = normalize(definition);
    (next[zone] as unknown[])[index] = value;
    onChange(next);
  };

  let body;
  if (zone === "measures") body = <MeasureEditor measure={item as ReportMeasure} field={field} labels={labels} onChange={replace} />;
  else if (zone === "filters") body = <FilterEditor filter={item as ReportFilter} type={field?.type ?? "String"} labels={labels} onChange={replace} />;
  else
    body = (
      <DimensionEditor
        dimension={item as ReportDimension}
        type={field?.type ?? "String"}
        measures={definition.measures.map((m) => ({ name: m.name ?? "", caption: measureCaption(m, fields, labels) }))}
        labels={labels}
        onChange={replace}
      />
    );

  return (
    <div className="cr-settings">
      <div className="cr-settings-head">
        <strong>{field?.caption ?? item?.field ?? labels.settings}</strong>
        <button type="button" className="cr-icon" aria-label={labels.cancel} onClick={onClose}>
          ×
        </button>
      </div>
      <div className="cr-settings-grid">{body}</div>
    </div>
  );
}

function Field({ label, children, wide }: { label: string; children: ReactNode; wide?: boolean }) {
  return (
    <label className={wide ? "cr-field-row cr-wide" : "cr-field-row"}>
      <span className="cr-label">{label}</span>
      {children}
    </label>
  );
}

function DimensionEditor({
  dimension,
  type,
  measures,
  labels,
  onChange,
}: {
  dimension: ReportDimension;
  type: ReportDataType;
  measures: { name: string; caption: string }[];
  labels: ReportingLabels;
  onChange: (value: ReportDimension) => void;
}) {
  const set = (patch: Partial<ReportDimension>) => onChange({ ...dimension, ...patch });
  const byMeasure = dimension.sort === "MeasureAscending" || dimension.sort === "MeasureDescending";
  return (
    <>
      <Field label={labels.caption}>
        <input className="cr-input" value={dimension.caption ?? ""} onChange={(e) => set({ caption: e.target.value || null })} />
      </Field>
      {type === "Date" && (
        <Field label={labels.dateGrouping}>
          <select className="cr-input" value={dimension.dateGrouping ?? "None"} onChange={(e) => set({ dateGrouping: e.target.value as DateGrouping })}>
            {groupings.map((g) => (
              <option key={g} value={g}>
                {labels.groupings[g]}
              </option>
            ))}
          </select>
        </Field>
      )}
      {type === "Number" && (
        <Field label={labels.rangeSize}>
          <input
            className="cr-input"
            type="number"
            min={0}
            value={dimension.rangeSize ?? ""}
            onChange={(e) => set({ rangeSize: e.target.value === "" ? null : Number(e.target.value) })}
          />
        </Field>
      )}
      {type === "String" && (
        <label className="cr-check">
          <input type="checkbox" checked={dimension.firstLetter ?? false} onChange={(e) => set({ firstLetter: e.target.checked })} />
          {labels.firstLetter}
        </label>
      )}
      <Field label={labels.sort}>
        <select
          className="cr-input"
          value={dimension.sort ?? "KeyAscending"}
          onChange={(e) => {
            const sort = e.target.value as DimensionSort;
            const measure = sort.startsWith("Measure") ? (dimension.sortByMeasure ?? measures[0]?.name ?? null) : null;
            set({ sort, sortByMeasure: measure });
          }}
        >
          {sorts.filter((s) => measures.length > 0 || !s.startsWith("Measure")).map((s) => (
            <option key={s} value={s}>
              {labels.sorts[s]}
            </option>
          ))}
        </select>
      </Field>
      {byMeasure && measures.length > 1 && (
        <Field label={labels.sortByMeasure}>
          <select className="cr-input" value={dimension.sortByMeasure ?? ""} onChange={(e) => set({ sortByMeasure: e.target.value })}>
            {measures.map((m) => (
              <option key={m.name} value={m.name}>
                {m.caption}
              </option>
            ))}
          </select>
        </Field>
      )}
      <Field label={labels.top}>
        <input
          className="cr-input"
          type="number"
          min={1}
          value={dimension.top ?? ""}
          onChange={(e) => set({ top: e.target.value === "" ? null : Math.max(1, Math.floor(Number(e.target.value))) })}
        />
      </Field>
      {dimension.top ? (
        <label className="cr-check">
          <input type="checkbox" checked={dimension.showOthers ?? true} onChange={(e) => set({ showOthers: e.target.checked })} />
          {labels.showOthers}
        </label>
      ) : null}
    </>
  );
}

function MeasureEditor({
  measure,
  field,
  labels,
  onChange,
}: {
  measure: ReportMeasure;
  field: ReportField | undefined;
  labels: ReportingLabels;
  onChange: (value: ReportMeasure) => void;
}) {
  const set = (patch: Partial<ReportMeasure>) => onChange({ ...measure, ...patch });
  const aggregates = field?.type === "Number" ? numericAggregates : otherAggregates;
  const customFormat = measure.format && !formats.includes(measure.format);
  return (
    <>
      <Field label={labels.aggregate}>
        <select className="cr-input" value={measure.aggregate} onChange={(e) => set({ aggregate: e.target.value as ReportAggregate })}>
          {aggregates.map((a) => (
            <option key={a} value={a}>
              {labels.aggregates[a]}
            </option>
          ))}
        </select>
      </Field>
      {measure.aggregate === "Percentile" && (
        <Field label={labels.percentile}>
          <input
            className="cr-input"
            type="number"
            min={0}
            max={1}
            step={0.05}
            value={measure.percentile ?? 0.9}
            onChange={(e) => set({ percentile: Number(e.target.value) })}
          />
        </Field>
      )}
      <Field label={labels.display}>
        <select className="cr-input" value={measure.display ?? "Value"} onChange={(e) => set({ display: e.target.value as MeasureDisplay })}>
          {displays.map((d) => (
            <option key={d} value={d}>
              {labels.displays[d]}
            </option>
          ))}
        </select>
      </Field>
      {measure.display && measure.display !== "Value" && measure.display !== "Rank" && (
        <Field label="">
          <select className="cr-input" value={measure.axis ?? "Rows"} onChange={(e) => set({ axis: e.target.value as "Rows" | "Columns" })}>
            <option value="Rows">↓ {labels.rows}</option>
            <option value="Columns">→ {labels.columns}</option>
          </select>
        </Field>
      )}
      <Field label={labels.format}>
        <select
          className="cr-input"
          value={customFormat ? "custom" : (measure.format ?? "")}
          onChange={(e) => set({ format: e.target.value === "custom" ? "#,0.00" : e.target.value || null })}
        >
          {formats.map((f) => (
            <option key={f} value={f}>
              {f === "" ? "—" : f}
            </option>
          ))}
          <option value="custom">…</option>
        </select>
      </Field>
      {customFormat && (
        <Field label="">
          <input className="cr-input cr-mono" value={measure.format ?? ""} onChange={(e) => set({ format: e.target.value || null })} />
        </Field>
      )}
      <Field label={labels.caption} wide>
        <input className="cr-input" value={measure.caption ?? ""} onChange={(e) => set({ caption: e.target.value || null })} />
      </Field>
    </>
  );
}

function ValueInput({ type, value, onChange }: { type: ReportDataType; value: ReportScalar; onChange: (value: ReportScalar) => void }) {
  if (type === "Boolean") {
    return (
      <select className="cr-input" value={value === false ? "false" : "true"} onChange={(e) => onChange(e.target.value === "true")}>
        <option value="true">✓</option>
        <option value="false">✗</option>
      </select>
    );
  }
  return (
    <input
      className="cr-input"
      type={type === "Date" ? "date" : type === "Number" ? "number" : "text"}
      value={value === null || value === undefined ? "" : String(value).slice(0, type === "Date" ? 10 : undefined)}
      onChange={(e) => onChange(e.target.value === "" ? null : type === "Number" ? Number(e.target.value) : e.target.value)}
    />
  );
}

function FilterEditor({
  filter,
  type,
  labels,
  onChange,
}: {
  filter: ReportFilter;
  type: ReportDataType;
  labels: ReportingLabels;
  onChange: (value: ReportFilter) => void;
}) {
  const [listText, setListText] = useState(() => filter.values.filter((v) => v !== null).join(", "));
  const set = (patch: Partial<ReportFilter>) => onChange({ ...filter, ...patch });
  const operators = operatorsByType[type];
  const list = filter.operator === "In" || filter.operator === "NotIn";

  return (
    <>
      <Field label={labels.operator}>
        <select
          className="cr-input"
          value={filter.operator}
          onChange={(e) => {
            const operator = e.target.value as FilterOperator;
            const values = operator === "IsNull" || operator === "IsNotNull" ? [] : operator === "Between" ? filter.values.slice(0, 2) : filter.values.slice(0, operator === "In" || operator === "NotIn" ? undefined : 1);
            set({ operator, values });
            setListText(values.filter((v) => v !== null).join(", "));
          }}
        >
          {operators.map((o) => (
            <option key={o} value={o}>
              {labels.operators[o]}
            </option>
          ))}
        </select>
      </Field>
      {filter.operator === "Between" && (
        <>
          <Field label={labels.from}>
            <ValueInput type={type} value={filter.values[0] ?? null} onChange={(v) => set({ values: [v, filter.values[1] ?? null] })} />
          </Field>
          <Field label={labels.to}>
            <ValueInput type={type} value={filter.values[1] ?? null} onChange={(v) => set({ values: [filter.values[0] ?? null, v] })} />
          </Field>
        </>
      )}
      {list && (
        <Field label={labels.value} wide>
          <textarea
            className="cr-input"
            rows={2}
            value={listText}
            placeholder={labels.valuesHint}
            onChange={(e) => {
              setListText(e.target.value);
              set({ values: parseValues(e.target.value, type) });
            }}
          />
        </Field>
      )}
      {!list && filter.operator !== "Between" && filter.operator !== "IsNull" && filter.operator !== "IsNotNull" && (
        <Field label={labels.value}>
          <ValueInput type={type} value={filter.values[0] ?? null} onChange={(v) => set({ values: [v] })} />
        </Field>
      )}
    </>
  );
}
