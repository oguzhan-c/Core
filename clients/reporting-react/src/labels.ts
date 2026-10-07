import type { DateGrouping, DimensionSort, FilterOperator, MeasureDisplay, ReportAggregate } from "./types";

/** Arayüz metinleri; <c>labels</c> prop'u ile kısmen ya da tamamen değiştirilebilir (çeviri). */
export interface ReportingLabels {
  source: string;
  chooseSource: string;
  fields: string;
  searchFields: string;
  rows: string;
  columns: string;
  values: string;
  filters: string;
  dropHere: string;
  addTo: string;
  remove: string;
  settings: string;
  caption: string;
  aggregate: string;
  display: string;
  format: string;
  percentile: string;
  dateGrouping: string;
  rangeSize: string;
  firstLetter: string;
  sort: string;
  sortByMeasure: string;
  top: string;
  showOthers: string;
  operator: string;
  value: string;
  from: string;
  to: string;
  valuesHint: string;
  calculatedField: string;
  calculatedFieldName: string;
  expression: string;
  expressionHint: string;
  add: string;
  cancel: string;
  apply: string;
  options: string;
  showSubtotals: string;
  showRowGrandTotal: string;
  showColumnGrandTotal: string;
  filterExpression: string;
  run: string;
  running: string;
  autoRun: string;
  table: string;
  chart: string;
  bar: string;
  line: string;
  pie: string;
  exportExcel: string;
  exportPdf: string;
  exportCsv: string;
  savedReports: string;
  newReport: string;
  save: string;
  saveAs: string;
  delete: string;
  deleteConfirm: string;
  reportName: string;
  description: string;
  shared: string;
  sharedBy: string;
  title: string;
  empty: string;
  emptyHint: string;
  rowsInfo: (matched: number, total: number, mode: string) => string;
  saved: string;
  deleted: string;
  noData: string;
  aggregates: Record<ReportAggregate, string>;
  displays: Record<MeasureDisplay, string>;
  groupings: Record<DateGrouping, string>;
  sorts: Record<DimensionSort, string>;
  operators: Record<FilterOperator, string>;
}

export const turkishLabels: ReportingLabels = {
  source: "Veri kaynağı",
  chooseSource: "Veri kaynağı seç",
  fields: "Alanlar",
  searchFields: "Alan ara",
  rows: "Satırlar",
  columns: "Sütunlar",
  values: "Değerler",
  filters: "Filtreler",
  dropHere: "Alanı buraya sürükle",
  addTo: "Ekle",
  remove: "Kaldır",
  settings: "Ayarlar",
  caption: "Başlık",
  aggregate: "Özet",
  display: "Gösterim",
  format: "Biçim",
  percentile: "Yüzdelik (0-1)",
  dateGrouping: "Tarih aralığı",
  rangeSize: "Aralık genişliği",
  firstLetter: "İlk harfe göre",
  sort: "Sıralama",
  sortByMeasure: "Sıralanacak değer",
  top: "İlk N",
  showOthers: "Kalanları \"Diğer\"de topla",
  operator: "Koşul",
  value: "Değer",
  from: "Başlangıç",
  to: "Bitiş",
  valuesHint: "Birden fazla değeri virgülle ayır",
  calculatedField: "Hesaplanmış alan",
  calculatedFieldName: "Alan adı",
  expression: "İfade",
  expressionHint: "Ör. [unitPrice] * [quantity] veya Iif([quantity] > 10, 'Çok', 'Az')",
  add: "Ekle",
  cancel: "Vazgeç",
  apply: "Uygula",
  options: "Seçenekler",
  showSubtotals: "Ara toplamlar",
  showRowGrandTotal: "Satır genel toplamı",
  showColumnGrandTotal: "Sütun genel toplamı",
  filterExpression: "İfade ile filtre",
  run: "Çalıştır",
  running: "Hesaplanıyor…",
  autoRun: "Otomatik",
  table: "Tablo",
  chart: "Grafik",
  bar: "Sütun",
  line: "Çizgi",
  pie: "Pasta",
  exportExcel: "Excel",
  exportPdf: "PDF",
  exportCsv: "CSV",
  savedReports: "Kayıtlı raporlar",
  newReport: "Yeni rapor",
  save: "Kaydet",
  saveAs: "Farklı kaydet",
  delete: "Sil",
  deleteConfirm: "Rapor silinsin mi?",
  reportName: "Rapor adı",
  description: "Açıklama",
  shared: "Herkesle paylaş",
  sharedBy: "Paylaşan",
  title: "Rapor başlığı",
  empty: "Rapor boş",
  emptyHint: "Soldaki alanları Satırlar, Sütunlar ve Değerler alanlarına sürükle.",
  rowsInfo: (matched, total, mode) =>
    `${matched.toLocaleString("tr-TR")} / ${total.toLocaleString("tr-TR")} kayıt · ${mode === "database" ? "veritabanında hesaplandı" : "bellekte hesaplandı"}`,
  saved: "Rapor kaydedildi.",
  deleted: "Rapor silindi.",
  noData: "Veri yok",
  aggregates: {
    Sum: "Toplam",
    Count: "Adet",
    CountDistinct: "Farklı değer sayısı",
    Average: "Ortalama",
    Min: "En küçük",
    Max: "En büyük",
    Median: "Ortanca",
    Percentile: "Yüzdelik",
    StdDev: "Standart sapma (örneklem)",
    StdDevP: "Standart sapma (anakütle)",
    Var: "Varyans (örneklem)",
    VarP: "Varyans (anakütle)",
    First: "İlk",
    Last: "Son",
  },
  displays: {
    Value: "Değer",
    PercentOfRowTotal: "Satır toplamına oranı",
    PercentOfColumnTotal: "Sütun toplamına oranı",
    PercentOfGrandTotal: "Genel toplama oranı",
    PercentOfParentRow: "Üst satır grubuna oranı",
    PercentOfParentColumn: "Üst sütun grubuna oranı",
    RunningTotal: "Kümülatif toplam",
    DifferenceFromPrevious: "Öncekinden fark",
    PercentDifferenceFromPrevious: "Öncekinden % fark",
    Rank: "Sıra",
  },
  groupings: {
    None: "Yok",
    Date: "Gün",
    Year: "Yıl",
    YearQuarter: "Yıl / çeyrek",
    YearMonth: "Yıl / ay",
    YearWeek: "Yıl / hafta",
    Quarter: "Çeyrek",
    Month: "Ay",
    DayOfWeek: "Haftanın günü",
    DayOfMonth: "Ayın günü",
    Hour: "Saat",
  },
  sorts: {
    KeyAscending: "A → Z",
    KeyDescending: "Z → A",
    MeasureAscending: "Değere göre artan",
    MeasureDescending: "Değere göre azalan",
  },
  operators: {
    Equal: "Eşittir",
    NotEqual: "Eşit değil",
    LessThan: "Küçüktür",
    LessThanOrEqual: "Küçük eşit",
    GreaterThan: "Büyüktür",
    GreaterThanOrEqual: "Büyük eşit",
    Between: "Arasında",
    In: "Şunlardan biri",
    NotIn: "Şunlar dışında",
    Contains: "İçerir",
    StartsWith: "İle başlar",
    IsNull: "Boş",
    IsNotNull: "Boş değil",
  },
};
