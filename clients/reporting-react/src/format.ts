import type { ReportScalar } from "./types";

export interface FormatOptions {
  /** BCP 47 dil etiketi (ör. "tr-TR"). */
  locale?: string;
  /** "C" biçimi için para birimi (ISO 4217). */
  currency?: string;
}

const isoDate = /^\d{4}-\d{2}-\d{2}(T[\d:.]+(Z|[+-]\d{2}:\d{2})?)?$/;

const cache = new Map<string, Intl.NumberFormat>();

function numberFormat(locale: string | undefined, options: Intl.NumberFormatOptions): Intl.NumberFormat {
  const key = `${locale}|${JSON.stringify(options)}`;
  let format = cache.get(key);
  if (!format) {
    format = new Intl.NumberFormat(locale, options);
    cache.set(key, format);
  }
  return format;
}

/** .NET'in standart biçimlerinin (N2, F0, P1, C2, d, g) karşılığı; özel biçimlerde 2 ondalık. */
export function formatValue(value: ReportScalar, format: string | null | undefined, options: FormatOptions = {}): string {
  if (value === null || value === undefined) return "";
  if (typeof value === "boolean") return value ? "✓" : "";
  const { locale, currency = "TRY" } = options;

  if (typeof value === "string") {
    if (!isoDate.test(value)) return value;
    const date = new Date(value.length === 10 ? `${value}T00:00:00` : value);
    if (Number.isNaN(date.getTime())) return value;
    const hasTime = value.length > 10 && !/T00:00:00(\.0+)?(Z|[+-]00:00)?$/.test(value);
    return hasTime && format !== "d" ? date.toLocaleString(locale) : date.toLocaleDateString(locale);
  }

  const kind = format?.[0]?.toUpperCase();
  const digits = format && format.length > 1 && /^\d+$/.test(format.slice(1)) ? Number(format.slice(1)) : undefined;
  switch (kind) {
    case "N":
      return numberFormat(locale, { minimumFractionDigits: digits ?? 2, maximumFractionDigits: digits ?? 2 }).format(value);
    case "F":
      return numberFormat(locale, { minimumFractionDigits: digits ?? 2, maximumFractionDigits: digits ?? 2, useGrouping: false }).format(value);
    case "P":
      return numberFormat(locale, { style: "percent", minimumFractionDigits: digits ?? 2, maximumFractionDigits: digits ?? 2 }).format(value);
    case "C":
      return numberFormat(locale, { style: "currency", currency, minimumFractionDigits: digits, maximumFractionDigits: digits }).format(value);
    default:
      return numberFormat(locale, { maximumFractionDigits: 2 }).format(value);
  }
}

/** Grafik eksenleri için kısa sayı (1,2 B / 3,4 Mn). */
export function formatCompact(value: number, locale?: string): string {
  return numberFormat(locale, { notation: "compact", maximumFractionDigits: 1 }).format(value);
}
