// Can.Core.Reporting sözleşmesinin TypeScript karşılığı (JSON: camelCase, enum'lar metin).

export type ReportDataType = "String" | "Number" | "Date" | "Boolean";

export type DateGrouping =
  | "None"
  | "Date"
  | "Year"
  | "YearQuarter"
  | "YearMonth"
  | "YearWeek"
  | "Quarter"
  | "Month"
  | "DayOfWeek"
  | "DayOfMonth"
  | "Hour";

export type DimensionSort = "KeyAscending" | "KeyDescending" | "MeasureAscending" | "MeasureDescending";

export type ReportAggregate =
  | "Sum"
  | "Count"
  | "CountDistinct"
  | "Average"
  | "Min"
  | "Max"
  | "Median"
  | "Percentile"
  | "StdDev"
  | "StdDevP"
  | "Var"
  | "VarP"
  | "First"
  | "Last";

export type MeasureDisplay =
  | "Value"
  | "PercentOfRowTotal"
  | "PercentOfColumnTotal"
  | "PercentOfGrandTotal"
  | "PercentOfParentRow"
  | "PercentOfParentColumn"
  | "RunningTotal"
  | "DifferenceFromPrevious"
  | "PercentDifferenceFromPrevious"
  | "Rank";

export type ReportAxis = "Rows" | "Columns";

export type FilterOperator =
  | "Equal"
  | "NotEqual"
  | "LessThan"
  | "LessThanOrEqual"
  | "GreaterThan"
  | "GreaterThanOrEqual"
  | "Between"
  | "In"
  | "NotIn"
  | "Contains"
  | "StartsWith"
  | "IsNull"
  | "IsNotNull";

export type ReportScalar = string | number | boolean | null;

export interface ReportField {
  name: string;
  type: ReportDataType;
  caption?: string | null;
  format?: string | null;
  isCalculated?: boolean;
}

export interface ReportCalculatedField {
  name: string;
  expression: string;
  caption?: string | null;
}

export interface ReportDimension {
  field: string;
  caption?: string | null;
  dateGrouping?: DateGrouping;
  rangeSize?: number | null;
  firstLetter?: boolean;
  sort?: DimensionSort;
  sortByMeasure?: string | null;
  top?: number | null;
  showOthers?: boolean;
}

export interface ReportMeasure {
  field?: string | null;
  aggregate: ReportAggregate;
  name?: string | null;
  caption?: string | null;
  format?: string | null;
  display?: MeasureDisplay;
  axis?: ReportAxis;
  percentile?: number | null;
}

export interface ReportFilter {
  field: string;
  operator: FilterOperator;
  values: ReportScalar[];
}

export interface ReportHaving {
  measure: string;
  operator: FilterOperator;
  values: ReportScalar[];
}

export interface ReportDefinition {
  dataSource?: string | null;
  title?: string | null;
  calculatedFields?: ReportCalculatedField[];
  filters?: ReportFilter[];
  filterExpression?: string | null;
  rows?: ReportDimension[];
  columns?: ReportDimension[];
  measures?: ReportMeasure[];
  having?: ReportHaving[];
  showSubtotals?: boolean;
  showRowGrandTotal?: boolean;
  showColumnGrandTotal?: boolean;
}

export type ReportHeaderKind = "Item" | "Group" | "Others" | "GrandTotal";

export interface ReportHeader {
  level: number;
  kind: ReportHeaderKind;
  keys: ReportScalar[];
  labels: string[];
  label: string;
}

export interface ReportResultDimension {
  field: string;
  caption: string;
  dateGrouping: DateGrouping;
}

export interface ReportResultMeasure {
  name: string;
  caption: string;
  aggregate: ReportAggregate;
  display: MeasureDisplay;
  format?: string | null;
}

export interface ReportResult {
  title?: string | null;
  rowDimensions: ReportResultDimension[];
  columnDimensions: ReportResultDimension[];
  measures: ReportResultMeasure[];
  rows: ReportHeader[];
  columns: ReportHeader[];
  /** values[satır][sütun × değer sayısı + değer] */
  values: ReportScalar[][];
  sourceRowCount: number;
  matchedRowCount: number;
  /** "database" (GROUP BY) ya da "memory". */
  mode: string;
}

export interface ReportSource {
  name: string;
  caption: string;
}

export interface ReportSourceDetail extends ReportSource {
  fields: ReportField[];
}

export type ReportExportFormat = "Csv" | "Xlsx" | "Pdf";

export type PdfPageSize = "A4Landscape" | "A4Portrait" | "A3Landscape";

export interface ReportExportRequest {
  definition: ReportDefinition;
  format: ReportExportFormat;
  title?: string | null;
  subtitle?: string | null;
  pdfPageSize?: PdfPageSize;
}

export interface SavedReportSummary {
  id: string;
  name: string;
  description?: string | null;
  dataSource: string;
  isShared: boolean;
  isOwner: boolean;
  canEdit: boolean;
  ownerName?: string | null;
  updatedAt: string;
}

export interface SavedReport extends SavedReportSummary {
  createdAt: string;
  definition: ReportDefinition;
}

export interface SaveReportRequest {
  name: string;
  description?: string | null;
  isShared: boolean;
  definition: ReportDefinition;
}

/** Hücre değeri: satır, sütun ve değer sırasıyla. */
export function cellValue(result: ReportResult, row: number, column: number, measure: number): ReportScalar {
  return result.values[row]?.[column * result.measures.length + measure] ?? null;
}
