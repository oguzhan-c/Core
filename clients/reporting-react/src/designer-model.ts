// Tasarımcının saf (React'siz) işlemleri: alan ekleme/taşıma, varsayılanlar, sunucuya giden tanım.
import type { ReportingLabels } from "./labels";
import type {
  FilterOperator,
  ReportCalculatedField,
  ReportDataType,
  ReportDefinition,
  ReportDimension,
  ReportField,
  ReportFilter,
  ReportMeasure,
} from "./types";

export type Zone = "rows" | "columns" | "measures" | "filters";

export interface DesignerDefinition extends ReportDefinition {
  calculatedFields: ReportCalculatedField[];
  filters: ReportFilter[];
  rows: ReportDimension[];
  columns: ReportDimension[];
  measures: ReportMeasure[];
}

export function normalize(definition?: ReportDefinition | null): DesignerDefinition {
  return {
    showSubtotals: true,
    showRowGrandTotal: true,
    showColumnGrandTotal: true,
    ...definition,
    calculatedFields: [...(definition?.calculatedFields ?? [])],
    filters: [...(definition?.filters ?? [])],
    rows: [...(definition?.rows ?? [])],
    columns: [...(definition?.columns ?? [])],
    measures: [...(definition?.measures ?? [])],
  };
}

const dateFunctions = /^\s*(Date|AddDays|AddMonths|AddYears|Today|Now)\s*\(/i;
const textFunctions = /\b(Concat|Upper|Lower|Trim|Substring|ToStr)\s*\(|'/i;
const boolExpression = /^\s*[^']*(=|<>|!=|<|>|\band\b|\bor\b|\bnot\b|Contains\(|StartsWith\(|EndsWith\()[^']*$/i;

/** Hesaplanmış alanın tipi ifadeden tahmin edilir (tarih gruplama, filtre girişi için). */
export function inferType(expression: string): ReportDataType {
  if (dateFunctions.test(expression)) return "Date";
  if (textFunctions.test(expression)) return "String";
  if (boolExpression.test(expression) && !/^\s*Iif\(/i.test(expression)) return "Boolean";
  return "Number";
}

/** Kaynak alanları + hesaplanmış alanlar. */
export function allFields(sourceFields: ReportField[], definition: DesignerDefinition): ReportField[] {
  return [
    ...sourceFields,
    ...definition.calculatedFields.map<ReportField>((c) => ({
      name: c.name,
      caption: c.caption ?? c.name,
      type: inferType(c.expression),
      isCalculated: true,
    })),
  ];
}

export function uniqueMeasureName(definition: DesignerDefinition): string {
  const used = new Set(definition.measures.map((m) => m.name));
  let n = definition.measures.length + 1;
  while (used.has(`m${n}`)) n++;
  return `m${n}`;
}

export function newDimension(field: ReportField): ReportDimension {
  return field.type === "Date" ? { field: field.name, dateGrouping: "Year" } : { field: field.name };
}

export function newMeasure(field: ReportField, definition: DesignerDefinition): ReportMeasure {
  return {
    field: field.name,
    aggregate: field.type === "Number" ? "Sum" : "Count",
    name: uniqueMeasureName(definition),
  };
}

export function defaultOperator(type: ReportDataType): FilterOperator {
  switch (type) {
    case "Date":
      return "Between";
    case "Number":
      return "GreaterThanOrEqual";
    case "Boolean":
      return "Equal";
    default:
      return "In";
  }
}

export function newFilter(field: ReportField): ReportFilter {
  const operator = defaultOperator(field.type);
  return { field: field.name, operator, values: field.type === "Boolean" ? [true] : [] };
}

function itemField(definition: DesignerDefinition, zone: Zone, index: number): string | null | undefined {
  return definition[zone][index]?.field;
}

/** Alanı bir bölgeye ekler (index verilirse o sıraya). */
export function addField(definition: DesignerDefinition, zone: Zone, field: ReportField, index?: number): DesignerDefinition {
  const next = normalize(definition);
  const at = (list: unknown[]) => (index === undefined ? list.length : Math.max(0, Math.min(index, list.length)));
  if (zone === "rows" || zone === "columns") {
    // aynı alan aynı eksende iki kez olmasın (tarih alanı farklı aralıklarla olabilir)
    if (field.type !== "Date" && next[zone].some((d) => d.field === field.name)) return definition;
    next[zone].splice(at(next[zone]), 0, newDimension(field));
  } else if (zone === "measures") {
    next.measures.splice(at(next.measures), 0, newMeasure(field, next));
  } else {
    next.filters.splice(at(next.filters), 0, newFilter(field));
  }
  return next;
}

/** Öğeyi bölgeler arasında (ya da bölge içinde) taşır; tür değişirse varsayılanlarla yeniden kurulur. */
export function moveItem(
  definition: DesignerDefinition,
  from: Zone,
  fromIndex: number,
  to: Zone,
  toIndex: number | undefined,
  fields: ReportField[],
): DesignerDefinition {
  const next = normalize(definition);
  if (from === to) {
    const list = next[from] as unknown[];
    const [item] = list.splice(fromIndex, 1);
    let target = toIndex ?? list.length;
    if (toIndex !== undefined && toIndex > fromIndex) target--;
    list.splice(Math.max(0, Math.min(target, list.length)), 0, item);
    return next;
  }

  const name = itemField(next, from, fromIndex);
  if ((from === "rows" || from === "columns") && (to === "rows" || to === "columns")) {
    const [dimension] = next[from].splice(fromIndex, 1);
    next[to].splice(toIndex ?? next[to].length, 0, dimension);
    return next;
  }

  const field = fields.find((f) => f.name === name);
  (next[from] as unknown[]).splice(fromIndex, 1);
  if (!field) return next; // "Adet" gibi alansız ölçüler başka bölgeye taşınamaz
  return addField(next, to, field, toIndex);
}

export function removeItem(definition: DesignerDefinition, zone: Zone, index: number): DesignerDefinition {
  const next = normalize(definition);
  const removed = next[zone][index];
  (next[zone] as unknown[]).splice(index, 1);
  if (zone === "measures" && removed && "name" in removed) {
    // bu ölçüye göre sıralanan boyutlar varsayılana döner
    const name = (removed as ReportMeasure).name;
    const reset = (d: ReportDimension) => (d.sortByMeasure === name ? { ...d, sortByMeasure: null, sort: "KeyAscending" as const } : d);
    next.rows = next.rows.map(reset);
    next.columns = next.columns.map(reset);
  }
  return next;
}

export function removeCalculatedField(definition: DesignerDefinition, name: string): DesignerDefinition {
  const next = normalize(definition);
  next.calculatedFields = next.calculatedFields.filter((c) => c.name !== name);
  next.rows = next.rows.filter((d) => d.field !== name);
  next.columns = next.columns.filter((d) => d.field !== name);
  next.measures = next.measures.filter((m) => m.field !== name);
  next.filters = next.filters.filter((f) => f.field !== name);
  return next;
}

export function fieldCaption(fields: ReportField[], name: string | null | undefined): string {
  if (!name) return "";
  const field = fields.find((f) => f.name.toLowerCase() === name.toLowerCase());
  return field?.caption || name;
}

const percentDisplays = new Set(["PercentOfRowTotal", "PercentOfColumnTotal", "PercentOfGrandTotal", "PercentOfParentRow", "PercentOfParentColumn", "PercentDifferenceFromPrevious"]);

export function measureCaption(measure: ReportMeasure, fields: ReportField[], labels: ReportingLabels): string {
  if (measure.caption) return measure.caption;
  const aggregate = labels.aggregates[measure.aggregate];
  const base = measure.field ? `${aggregate} · ${fieldCaption(fields, measure.field)}` : aggregate;
  return measure.display && measure.display !== "Value" ? `${base} (${labels.displays[measure.display]})` : base;
}

export function measureFormat(measure: ReportMeasure, fields: ReportField[]): string | null | undefined {
  if (measure.format) return measure.format;
  if (measure.display && percentDisplays.has(measure.display)) return undefined; // sunucu P1 kullanır
  if (measure.display === "Rank" || measure.aggregate === "Count" || measure.aggregate === "CountDistinct") return "N0";
  const field = fields.find((f) => f.name === measure.field);
  if (field?.type === "Date" && (measure.aggregate === "Min" || measure.aggregate === "Max" || measure.aggregate === "First" || measure.aggregate === "Last")) return "d";
  return field?.format ?? "N2";
}

export function dimensionCaption(dimension: ReportDimension, fields: ReportField[], labels: ReportingLabels): string {
  if (dimension.caption) return dimension.caption;
  const caption = fieldCaption(fields, dimension.field);
  if (dimension.dateGrouping && dimension.dateGrouping !== "None") return `${caption} (${labels.groupings[dimension.dateGrouping]})`;
  if (dimension.rangeSize) return `${caption} (${dimension.rangeSize})`;
  return caption;
}

export function filterSummary(filter: ReportFilter, fields: ReportField[], labels: ReportingLabels): string {
  const caption = fieldCaption(fields, filter.field);
  const values = filter.values.filter((v) => v !== null && v !== "").map(String);
  if (filter.operator === "IsNull" || filter.operator === "IsNotNull") return `${caption} ${labels.operators[filter.operator].toLowerCase()}`;
  if (values.length === 0) return caption;
  if (filter.operator === "Between") return `${caption}: ${values[0] ?? "…"} – ${values[1] ?? "…"}`;
  const shown = values.length > 3 ? `${values.slice(0, 3).join(", ")} +${values.length - 3}` : values.join(", ");
  return `${caption} ${labels.operators[filter.operator].toLowerCase()} ${shown}`;
}

/** Filtrenin işe yarar değeri var mı (boş filtreler sunucuya gönderilmez). */
export function isFilterComplete(filter: ReportFilter): boolean {
  if (filter.operator === "IsNull" || filter.operator === "IsNotNull") return true;
  const values = filter.values.filter((v) => v !== null && v !== "");
  if (filter.operator === "Between") return values.length > 0;
  return values.length > 0;
}

/** Sunucuya gidecek tanım: veri kaynağı, başlıklar ve biçimler doldurulur, eksik filtreler atılır. */
export function prepare(definition: DesignerDefinition, dataSource: string, fields: ReportField[], labels: ReportingLabels): ReportDefinition {
  return {
    ...definition,
    dataSource,
    filterExpression: definition.filterExpression?.trim() || null,
    filters: definition.filters
      .filter(isFilterComplete)
      .map((f) =>
        f.operator === "Between"
          ? // açık uçlu aralık: tek uç verildiyse büyük/küçük eşit
            f.values[0] !== null && f.values[0] !== "" && (f.values[1] === null || f.values[1] === "" || f.values[1] === undefined)
            ? { ...f, operator: "GreaterThanOrEqual" as const, values: [f.values[0]] }
            : f.values[0] === null || f.values[0] === "" || f.values[0] === undefined
              ? { ...f, operator: "LessThanOrEqual" as const, values: [f.values[1]] }
              : f
          : f,
      ),
    rows: definition.rows.map((d) => ({ ...d, caption: dimensionCaption(d, fields, labels) })),
    columns: definition.columns.map((d) => ({ ...d, caption: dimensionCaption(d, fields, labels) })),
    measures: definition.measures.map((m) => ({ ...m, caption: measureCaption(m, fields, labels), format: measureFormat(m, fields) })),
  };
}

/** Tanımda gösterilecek bir şey var mı. */
export function isRunnable(definition: DesignerDefinition): boolean {
  return definition.rows.length + definition.columns.length + definition.measures.length > 0;
}

/** Virgülle ayrılmış metni değerlere çevirir (sayı alanında sayı). */
export function parseValues(text: string, type: ReportDataType): (string | number | boolean)[] {
  return text
    .split(/[,;\n]/)
    .map((v) => v.trim())
    .filter((v) => v.length > 0)
    .map((v) => {
      if (type === "Number") {
        const n = Number(v.replace(",", "."));
        return Number.isNaN(n) ? v : n;
      }
      if (type === "Boolean") return v === "true" || v === "1" || v.toLowerCase() === "evet";
      return v;
    });
}
