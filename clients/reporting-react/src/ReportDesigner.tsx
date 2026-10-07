import { useCallback, useEffect, useMemo, useRef, useState, type DragEvent, type KeyboardEvent, type ReactNode } from "react";
import { downloadFile, type ReportingClient } from "./client";
import {
  addField,
  allFields,
  dimensionCaption,
  filterSummary,
  isRunnable,
  measureCaption,
  moveItem,
  normalize,
  prepare,
  removeCalculatedField,
  removeItem,
  type DesignerDefinition,
  type Zone,
} from "./designer-model";
import { turkishLabels, type ReportingLabels } from "./labels";
import { PivotTable } from "./PivotTable";
import { ReportChart, type ReportChartType } from "./ReportChart";
import { ItemSettings } from "./ItemSettings";
import type { ReportExportFormat, ReportField, ReportResult, ReportSource, SavedReport, SavedReportSummary } from "./types";

export interface ReportDesignerProps {
  client: ReportingClient;
  /** Metinlerin bir kısmı ya da tamamı (çeviri). */
  labels?: Partial<ReportingLabels>;
  /** Sayı/tarih biçimi için dil (ör. "tr-TR"). */
  locale?: string;
  /** "C" biçimi için para birimi. */
  currency?: string;
  /** Açılışta yüklenecek kayıtlı rapor. */
  initialReportId?: string;
  /** Açılışta seçilecek veri kaynağı (yoksa ilki). */
  initialSource?: string;
  /** Bildirimler (kaydedildi, hata ...). Verilmezse tasarımcının içinde gösterilir. */
  onNotify?: (message: string, kind: "success" | "error") => void;
  /** Kayıtlı rapor yüklenince/değişince (adres çubuğunu güncellemek için). */
  onReportChange?: (report: SavedReportSummary | null) => void;
  className?: string;
}

type DragPayload = { field: string } | { zone: Zone; index: number };

const DRAG_TYPE = "application/x-can-report";

const zones: Zone[] = ["filters", "rows", "columns", "measures"];

/** role="button" öğelerde Enter/Boşluk (sürüklenebilir öğeler Firefox'ta <button> olamıyor). */
function activate(event: KeyboardEvent<HTMLElement>, action: () => void) {
  if (event.key === "Enter" || event.key === " ") {
    event.preventDefault();
    action();
  }
}

function Modal({ title, children, onClose }: { title: string; children: ReactNode; onClose: () => void }) {
  useEffect(() => {
    const onKey = (e: globalThis.KeyboardEvent) => e.key === "Escape" && onClose();
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [onClose]);
  return (
    <div className="cr-modal-backdrop" onMouseDown={(e) => e.target === e.currentTarget && onClose()}>
      <div className="cr-modal" role="dialog" aria-modal="true" aria-label={title}>
        <h3 className="cr-modal-title">{title}</h3>
        {children}
      </div>
    </div>
  );
}

/**
 * Pivot rapor tasarımcısı: veri kaynağı seç, alanları Satırlar / Sütunlar / Değerler / Filtreler'e sürükle, sonuç
 * tablo ya da grafik olarak anında görünür; Excel/PDF/CSV indir, raporu kaydet ve paylaş. Tüm hesap sunucuda yapılır.
 */
export function ReportDesigner({
  client,
  labels: labelOverrides,
  locale,
  currency,
  initialReportId,
  initialSource,
  onNotify,
  onReportChange,
  className,
}: ReportDesignerProps) {
  const labels = useMemo<ReportingLabels>(() => ({ ...turkishLabels, ...labelOverrides }), [labelOverrides]);

  const [sources, setSources] = useState<ReportSource[]>([]);
  const [sourceName, setSourceName] = useState<string>("");
  const [sourceFields, setSourceFields] = useState<ReportField[]>([]);
  const [definition, setDefinition] = useState<DesignerDefinition>(() => normalize());
  const [result, setResult] = useState<ReportResult | null>(null);
  const [running, setRunning] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [status, setStatus] = useState<string | null>(null);
  const [autoRun, setAutoRun] = useState(true);
  const [view, setView] = useState<"table" | "chart">("table");
  const [chartType, setChartType] = useState<ReportChartType>("bar");
  const [chartMeasure, setChartMeasure] = useState(0);
  const [editing, setEditing] = useState<{ zone: Zone; index: number } | null>(null);
  const [fieldMenu, setFieldMenu] = useState<string | null>(null);
  const [search, setSearch] = useState("");
  const [dropTarget, setDropTarget] = useState<string | null>(null);
  const [saved, setSaved] = useState<SavedReportSummary[]>([]);
  const [current, setCurrent] = useState<SavedReport | null>(null);
  const [saveDialog, setSaveDialog] = useState<"save" | "saveAs" | null>(null);
  const [calcDialog, setCalcDialog] = useState(false);
  const [exporting, setExporting] = useState<ReportExportFormat | null>(null);
  const runController = useRef<AbortController | null>(null);
  const pendingDefinition = useRef<DesignerDefinition | null>(null);

  const fields = useMemo(() => allFields(sourceFields, definition), [sourceFields, definition]);

  // geri çağrılar ref'te: üst bileşen her çizimde yeni fonksiyon verse de yüklemeler tekrarlanmaz
  const onNotifyRef = useRef(onNotify);
  const onReportChangeRef = useRef(onReportChange);
  useEffect(() => {
    onNotifyRef.current = onNotify;
    onReportChangeRef.current = onReportChange;
  });

  const notify = useCallback((message: string, kind: "success" | "error") => {
    const handler = onNotifyRef.current;
    if (handler) handler(message, kind);
    else if (kind === "error") setError(message);
    else setStatus(message);
  }, []);

  const failed = useCallback((e: unknown) => notify(e instanceof Error ? e.message : String(e), "error"), [notify]);

  // ------------------------------------------------------------------ yükleme

  const loadSaved = useCallback(() => {
    client.savedReports().then(setSaved).catch(failed);
  }, [client, failed]);

  useEffect(() => {
    const controller = new AbortController();
    client
      .sources(controller.signal)
      .then((list) => {
        setSources(list);
        setSourceName((currentName) => currentName || (list.find((s) => s.name === initialSource) ?? list[0])?.name || "");
      })
      .catch((e) => !controller.signal.aborted && failed(e));
    loadSaved();
    return () => controller.abort();
  }, [client, initialSource, loadSaved, failed]);

  useEffect(() => {
    if (!sourceName) return;
    const controller = new AbortController();
    client
      .source(sourceName, controller.signal)
      .then((detail) => {
        setSourceFields(detail.fields);
        // kayıtlı rapor yüklenirken tanım kaynak alanlarından sonra uygulanır
        if (pendingDefinition.current) {
          setDefinition(pendingDefinition.current);
          pendingDefinition.current = null;
        }
      })
      .catch((e) => !controller.signal.aborted && failed(e));
    return () => controller.abort();
  }, [client, sourceName, failed]);

  const openReport = useCallback(
    async (id: string) => {
      try {
        const report = await client.savedReport(id);
        setCurrent(report);
        setEditing(null);
        setResult(null);
        const next = normalize(report.definition);
        if (report.dataSource === sourceName) setDefinition(next);
        else {
          pendingDefinition.current = next;
          setSourceName(report.dataSource);
        }
        onReportChangeRef.current?.(report);
      } catch (e) {
        failed(e);
      }
    },
    [client, sourceName, failed],
  );

  const initialLoaded = useRef(false);
  useEffect(() => {
    if (initialReportId && !initialLoaded.current) {
      initialLoaded.current = true;
      void openReport(initialReportId);
    }
  }, [initialReportId, openReport]);

  // ------------------------------------------------------------------ çalıştırma

  const run = useCallback(async () => {
    if (!sourceName || !isRunnable(definition)) {
      setResult(null);
      return;
    }
    runController.current?.abort();
    const controller = new AbortController();
    runController.current = controller;
    setRunning(true);
    setError(null);
    try {
      const value = await client.run(prepare(definition, sourceName, fields, labels), controller.signal);
      if (!controller.signal.aborted) {
        setResult(value);
        setChartMeasure((m) => (m < value.measures.length ? m : 0));
      }
    } catch (e) {
      if (!controller.signal.aborted) {
        setResult(null);
        setError(e instanceof Error ? e.message : String(e));
      }
    } finally {
      if (runController.current === controller) setRunning(false);
    }
  }, [client, definition, sourceName, fields, labels]);

  // tanım değişince (yazarken her tuşta değil, 350 ms sonra) yeniden hesapla
  const runRef = useRef(run);
  useEffect(() => {
    runRef.current = run;
  });
  const hasFields = sourceFields.length > 0;
  useEffect(() => {
    if (!autoRun || !hasFields) return;
    const timer = setTimeout(() => void runRef.current(), 350);
    return () => clearTimeout(timer);
  }, [autoRun, hasFields, definition, sourceName]);

  useEffect(() => () => runController.current?.abort(), []);

  const exportAs = async (format: ReportExportFormat) => {
    if (!sourceName || !isRunnable(definition)) return;
    setExporting(format);
    try {
      const file = await client.export({
        definition: prepare(definition, sourceName, fields, labels),
        format,
        title: definition.title || current?.name || sources.find((s) => s.name === sourceName)?.caption,
        subtitle: definition.filters.length > 0 ? definition.filters.map((f) => filterSummary(f, fields, labels)).join(" · ") : null,
      });
      downloadFile(file);
    } catch (e) {
      failed(e);
    } finally {
      setExporting(null);
    }
  };

  // ------------------------------------------------------------------ düzenleme

  const update = (next: DesignerDefinition) => {
    setDefinition(next);
    setStatus(null);
  };

  const changeSource = (name: string) => {
    setSourceName(name);
    setSourceFields([]);
    setDefinition(normalize());
    setResult(null);
    setEditing(null);
    setCurrent(null);
    onReportChangeRef.current?.(null);
  };

  const newReport = () => {
    setDefinition(normalize());
    setResult(null);
    setEditing(null);
    setCurrent(null);
    setError(null);
    onReportChangeRef.current?.(null);
  };

  const addTo = (zone: Zone, field: ReportField, index?: number) => {
    const next = addField(definition, zone, field, index);
    update(next);
    setFieldMenu(null);
    if (zone === "filters") setEditing({ zone, index: index ?? next.filters.length - 1 });
  };

  const onDragStart = (event: DragEvent, payload: DragPayload) => {
    event.dataTransfer.setData(DRAG_TYPE, JSON.stringify(payload));
    event.dataTransfer.effectAllowed = "move";
  };

  const onDrop = (event: DragEvent, zone: Zone, index?: number) => {
    event.preventDefault();
    event.stopPropagation();
    setDropTarget(null);
    const raw = event.dataTransfer.getData(DRAG_TYPE);
    if (!raw) return;
    const payload = JSON.parse(raw) as DragPayload;
    if ("field" in payload) {
      const field = fields.find((f) => f.name === payload.field);
      if (field) addTo(zone, field, index);
    } else {
      update(moveItem(definition, payload.zone, payload.index, zone, index, fields));
      setEditing(null);
    }
  };

  const allowDrop = (event: DragEvent, target: string) => {
    if (!event.dataTransfer.types.includes(DRAG_TYPE)) return;
    event.preventDefault();
    event.dataTransfer.dropEffect = "move";
    if (dropTarget !== target) setDropTarget(target);
  };

  const zoneTitle: Record<Zone, string> = { filters: labels.filters, rows: labels.rows, columns: labels.columns, measures: labels.values };

  const chipLabel = (zone: Zone, index: number): string => {
    if (zone === "measures") return measureCaption(definition.measures[index], fields, labels);
    if (zone === "filters") return filterSummary(definition.filters[index], fields, labels);
    return dimensionCaption(definition[zone][index], fields, labels);
  };

  // ------------------------------------------------------------------ kaydetme

  const saveReport = async (name: string, description: string, isShared: boolean, asNew: boolean) => {
    const request = { name, description: description || null, isShared, definition: { ...definition, dataSource: sourceName } };
    try {
      const report = !asNew && current?.canEdit ? await client.updateReport(current.id, request) : await client.createReport(request);
      setCurrent(report);
      setSaveDialog(null);
      loadSaved();
      onReportChangeRef.current?.(report);
      notify(labels.saved, "success");
    } catch (e) {
      failed(e);
    }
  };

  const deleteReport = async () => {
    if (!current || !window.confirm(labels.deleteConfirm)) return;
    try {
      await client.deleteReport(current.id);
      loadSaved();
      newReport();
      notify(labels.deleted, "success");
    } catch (e) {
      failed(e);
    }
  };

  // ------------------------------------------------------------------ görünüm

  const visibleFields = fields.filter((f) => {
    const text = `${f.caption ?? ""} ${f.name}`.toLocaleLowerCase(locale);
    return text.includes(search.toLocaleLowerCase(locale));
  });

  const editingItem = editing && definition[editing.zone][editing.index] ? editing : null;

  return (
    <div className={["cr-designer", className].filter(Boolean).join(" ")}>
      {/* ------------------------------------------------ üst çubuk */}
      <div className="cr-toolbar">
        <label className="cr-inline">
          <span className="cr-label">{labels.source}</span>
          <select className="cr-input" value={sourceName} onChange={(e) => changeSource(e.target.value)}>
            {!sourceName && <option value="">{labels.chooseSource}</option>}
            {sources.map((s) => (
              <option key={s.name} value={s.name}>
                {s.caption}
              </option>
            ))}
          </select>
        </label>
        <label className="cr-inline">
          <span className="cr-label">{labels.savedReports}</span>
          <select
            className="cr-input"
            value={current?.id ?? ""}
            onChange={(e) => (e.target.value ? void openReport(e.target.value) : newReport())}
          >
            <option value="">{labels.newReport}</option>
            {saved.map((r) => (
              <option key={r.id} value={r.id}>
                {r.name}
                {r.isShared && !r.isOwner && r.ownerName ? ` · ${r.ownerName}` : ""}
              </option>
            ))}
          </select>
        </label>
        <div className="cr-toolbar-group">
          <button type="button" className="cr-button" onClick={newReport}>
            {labels.newReport}
          </button>
          <button type="button" className="cr-button cr-primary" disabled={!sourceName || !isRunnable(definition)} onClick={() => setSaveDialog(current?.canEdit ? "save" : "saveAs")}>
            {labels.save}
          </button>
          {current && (
            <button type="button" className="cr-button" onClick={() => setSaveDialog("saveAs")}>
              {labels.saveAs}
            </button>
          )}
          {current?.canEdit && (
            <button type="button" className="cr-button cr-danger" onClick={() => void deleteReport()}>
              {labels.delete}
            </button>
          )}
        </div>
        <div className="cr-toolbar-group cr-push">
          {(["Xlsx", "Pdf", "Csv"] as const).map((format) => (
            <button
              key={format}
              type="button"
              className="cr-button"
              disabled={!result || exporting !== null}
              onClick={() => void exportAs(format)}
            >
              {exporting === format ? "…" : format === "Xlsx" ? labels.exportExcel : format === "Pdf" ? labels.exportPdf : labels.exportCsv}
            </button>
          ))}
        </div>
      </div>

      <div className="cr-body">
        {/* ------------------------------------------------ alan listesi */}
        <aside className="cr-fields">
          <div className="cr-panel-title">{labels.fields}</div>
          <input className="cr-input" type="search" placeholder={labels.searchFields} value={search} onChange={(e) => setSearch(e.target.value)} />
          <ul className="cr-field-list">
            {visibleFields.map((field) => (
              <li key={field.name} className="cr-field-item">
                <div
                  role="button"
                  tabIndex={0}
                  aria-haspopup="menu"
                  aria-expanded={fieldMenu === field.name}
                  className="cr-field"
                  draggable
                  onDragStart={(e) => onDragStart(e, { field: field.name })}
                  onClick={() => setFieldMenu(fieldMenu === field.name ? null : field.name)}
                  onKeyDown={(e) => activate(e, () => setFieldMenu(fieldMenu === field.name ? null : field.name))}
                  title={field.name}
                >
                  <span className={`cr-type cr-type-${field.type.toLowerCase()}`} aria-hidden="true">
                    {field.isCalculated ? "ƒ" : field.type === "Number" ? "#" : field.type === "Date" ? "◷" : field.type === "Boolean" ? "✓" : "A"}
                  </span>
                  {field.caption || field.name}
                </div>
                {field.isCalculated && (
                  <button type="button" className="cr-icon" aria-label={labels.remove} onClick={() => update(removeCalculatedField(definition, field.name))}>
                    ×
                  </button>
                )}
                {fieldMenu === field.name && (
                  <div className="cr-field-menu" role="menu">
                    {zones.map((zone) => (
                      <button key={zone} type="button" role="menuitem" className="cr-menu-item" onClick={() => addTo(zone, field)}>
                        {zoneTitle[zone]}
                      </button>
                    ))}
                  </div>
                )}
              </li>
            ))}
          </ul>
          <button type="button" className="cr-button cr-block" disabled={!sourceName} onClick={() => setCalcDialog(true)}>
            + {labels.calculatedField}
          </button>
        </aside>

        <section className="cr-main">
          {/* ------------------------------------------------ bölgeler */}
          <div className="cr-zones">
            {zones.map((zone) => (
              <div
                key={zone}
                className={["cr-zone", dropTarget === zone ? "cr-drop" : ""].join(" ")}
                onDragOver={(e) => allowDrop(e, zone)}
                onDragLeave={() => setDropTarget(null)}
                onDrop={(e) => onDrop(e, zone)}
              >
                <div className="cr-zone-title">{zoneTitle[zone]}</div>
                <div className="cr-chips">
                  {definition[zone].length === 0 && <span className="cr-muted cr-small">{labels.dropHere}</span>}
                  {definition[zone].map((_, index) => {
                    const active = editingItem?.zone === zone && editingItem.index === index;
                    return (
                      <span
                        key={index}
                        className={["cr-chip", active ? "cr-chip-active" : "", dropTarget === `${zone}-${index}` ? "cr-drop" : ""].join(" ")}
                        draggable
                        onDragStart={(e) => onDragStart(e, { zone, index })}
                        onDragOver={(e) => allowDrop(e, `${zone}-${index}`)}
                        onDrop={(e) => onDrop(e, zone, index)}
                      >
                        <span
                          role="button"
                          tabIndex={0}
                          aria-pressed={active}
                          className="cr-chip-label"
                          onClick={() => setEditing(active ? null : { zone, index })}
                          onKeyDown={(e) => activate(e, () => setEditing(active ? null : { zone, index }))}
                          title={labels.settings}
                        >
                          {chipLabel(zone, index)}
                        </span>
                        <button type="button" className="cr-icon" aria-label={labels.remove} onClick={() => { update(removeItem(definition, zone, index)); setEditing(null); }}>
                          ×
                        </button>
                      </span>
                    );
                  })}
                </div>
              </div>
            ))}
          </div>

          {editingItem && (
            <ItemSettings
              key={`${editingItem.zone}-${editingItem.index}`}
              definition={definition}
              zone={editingItem.zone}
              index={editingItem.index}
              fields={fields}
              labels={labels}
              onChange={update}
              onClose={() => setEditing(null)}
            />
          )}

          {/* ------------------------------------------------ seçenekler */}
          <details className="cr-options">
            <summary>{labels.options}</summary>
            <div className="cr-options-body">
              <label className="cr-inline cr-grow">
                <span className="cr-label">{labels.title}</span>
                <input className="cr-input" value={definition.title ?? ""} onChange={(e) => update({ ...definition, title: e.target.value || null })} />
              </label>
              <label className="cr-check">
                <input type="checkbox" checked={definition.showSubtotals ?? true} onChange={(e) => update({ ...definition, showSubtotals: e.target.checked })} />
                {labels.showSubtotals}
              </label>
              <label className="cr-check">
                <input type="checkbox" checked={definition.showRowGrandTotal ?? true} onChange={(e) => update({ ...definition, showRowGrandTotal: e.target.checked })} />
                {labels.showRowGrandTotal}
              </label>
              <label className="cr-check">
                <input type="checkbox" checked={definition.showColumnGrandTotal ?? true} onChange={(e) => update({ ...definition, showColumnGrandTotal: e.target.checked })} />
                {labels.showColumnGrandTotal}
              </label>
              <FilterExpression
                value={definition.filterExpression ?? ""}
                label={labels.filterExpression}
                hint={labels.expressionHint}
                onChange={(value) => update({ ...definition, filterExpression: value || null })}
              />
            </div>
          </details>

          {/* ------------------------------------------------ sonuç */}
          <div className="cr-result-bar">
            <div className="cr-tabs" role="tablist">
              <button type="button" role="tab" aria-selected={view === "table"} className={view === "table" ? "cr-tab cr-tab-active" : "cr-tab"} onClick={() => setView("table")}>
                {labels.table}
              </button>
              <button type="button" role="tab" aria-selected={view === "chart"} className={view === "chart" ? "cr-tab cr-tab-active" : "cr-tab"} onClick={() => setView("chart")}>
                {labels.chart}
              </button>
            </div>
            {view === "chart" && result && (
              <>
                <select className="cr-input" value={chartType} onChange={(e) => setChartType(e.target.value as ReportChartType)}>
                  <option value="bar">{labels.bar}</option>
                  <option value="line">{labels.line}</option>
                  <option value="pie">{labels.pie}</option>
                </select>
                {result.measures.length > 1 && (
                  <select className="cr-input" value={chartMeasure} onChange={(e) => setChartMeasure(Number(e.target.value))}>
                    {result.measures.map((m, i) => (
                      <option key={m.name} value={i}>
                        {m.caption}
                      </option>
                    ))}
                  </select>
                )}
              </>
            )}
            <span className="cr-muted cr-small cr-push">
              {running ? labels.running : result ? labels.rowsInfo(result.matchedRowCount, result.sourceRowCount, result.mode) : ""}
            </span>
            <label className="cr-check cr-small">
              <input type="checkbox" checked={autoRun} onChange={(e) => setAutoRun(e.target.checked)} />
              {labels.autoRun}
            </label>
            <button type="button" className="cr-button" disabled={running || !isRunnable(definition)} onClick={() => void run()}>
              {labels.run}
            </button>
          </div>

          {error && (
            <div className="cr-alert" role="alert">
              {error}
            </div>
          )}
          {status && !error && <div className="cr-status">{status}</div>}

          <div className={running ? "cr-result cr-busy" : "cr-result"} aria-busy={running}>
            {!isRunnable(definition) ? (
              <div className="cr-empty">
                <strong>{labels.empty}</strong>
                <span className="cr-muted">{labels.emptyHint}</span>
              </div>
            ) : result ? (
              view === "table" ? (
                <PivotTable result={result} locale={locale} currency={currency} />
              ) : (
                <ReportChart result={result} type={chartType} measure={chartMeasure} locale={locale} currency={currency} noDataLabel={labels.noData} />
              )
            ) : null}
          </div>
        </section>
      </div>

      {saveDialog && (
        <SaveDialog
          labels={labels}
          initial={saveDialog === "save" && current ? current : { name: saveDialog === "saveAs" && current ? `${current.name} (2)` : definition.title ?? "", description: current?.description ?? "", isShared: false }}
          onCancel={() => setSaveDialog(null)}
          onSave={(name, description, isShared) => void saveReport(name, description, isShared, saveDialog === "saveAs")}
        />
      )}

      {calcDialog && (
        <CalculatedFieldDialog
          labels={labels}
          existing={fields.map((f) => f.name.toLowerCase())}
          onCancel={() => setCalcDialog(false)}
          onAdd={(name, expression, caption) => {
            update({ ...definition, calculatedFields: [...definition.calculatedFields, { name, expression, caption: caption || null }] });
            setCalcDialog(false);
          }}
        />
      )}
    </div>
  );
}

function FilterExpression({ value, label, hint, onChange }: { value: string; label: string; hint: string; onChange: (value: string) => void }) {
  const [text, setText] = useState(value);
  useEffect(() => setText(value), [value]);
  return (
    <label className="cr-inline cr-grow">
      <span className="cr-label">{label}</span>
      <input
        className="cr-input cr-mono"
        value={text}
        placeholder={hint}
        onChange={(e) => setText(e.target.value)}
        onBlur={() => text !== value && onChange(text.trim())}
        onKeyDown={(e) => e.key === "Enter" && onChange(text.trim())}
      />
    </label>
  );
}

function SaveDialog({
  labels,
  initial,
  onCancel,
  onSave,
}: {
  labels: ReportingLabels;
  initial: { name: string; description?: string | null; isShared: boolean };
  onCancel: () => void;
  onSave: (name: string, description: string, isShared: boolean) => void;
}) {
  const [name, setName] = useState(initial.name);
  const [description, setDescription] = useState(initial.description ?? "");
  const [isShared, setIsShared] = useState(initial.isShared);
  return (
    <Modal title={labels.save} onClose={onCancel}>
      <form
        className="cr-form"
        onSubmit={(e) => {
          e.preventDefault();
          if (name.trim()) onSave(name.trim(), description.trim(), isShared);
        }}
      >
        <label className="cr-field-row">
          <span className="cr-label">{labels.reportName}</span>
          <input className="cr-input" value={name} maxLength={200} required autoFocus onChange={(e) => setName(e.target.value)} />
        </label>
        <label className="cr-field-row">
          <span className="cr-label">{labels.description}</span>
          <textarea className="cr-input" value={description} maxLength={1000} rows={3} onChange={(e) => setDescription(e.target.value)} />
        </label>
        <label className="cr-check">
          <input type="checkbox" checked={isShared} onChange={(e) => setIsShared(e.target.checked)} />
          {labels.shared}
        </label>
        <div className="cr-actions">
          <button type="button" className="cr-button" onClick={onCancel}>
            {labels.cancel}
          </button>
          <button type="submit" className="cr-button cr-primary" disabled={!name.trim()}>
            {labels.save}
          </button>
        </div>
      </form>
    </Modal>
  );
}

function CalculatedFieldDialog({
  labels,
  existing,
  onCancel,
  onAdd,
}: {
  labels: ReportingLabels;
  existing: string[];
  onCancel: () => void;
  onAdd: (name: string, expression: string, caption: string) => void;
}) {
  const [name, setName] = useState("");
  const [caption, setCaption] = useState("");
  const [expression, setExpression] = useState("");
  const validName = /^[A-Za-z_][A-Za-z0-9_]*$/.test(name) && !existing.includes(name.toLowerCase());
  return (
    <Modal title={labels.calculatedField} onClose={onCancel}>
      <form
        className="cr-form"
        onSubmit={(e) => {
          e.preventDefault();
          if (validName && expression.trim()) onAdd(name, expression.trim(), caption.trim());
        }}
      >
        <label className="cr-field-row">
          <span className="cr-label">{labels.calculatedFieldName}</span>
          <input className="cr-input cr-mono" value={name} required autoFocus onChange={(e) => setName(e.target.value)} aria-invalid={name.length > 0 && !validName} />
        </label>
        <label className="cr-field-row">
          <span className="cr-label">{labels.caption}</span>
          <input className="cr-input" value={caption} onChange={(e) => setCaption(e.target.value)} />
        </label>
        <label className="cr-field-row">
          <span className="cr-label">{labels.expression}</span>
          <textarea className="cr-input cr-mono" value={expression} rows={3} required placeholder={labels.expressionHint} onChange={(e) => setExpression(e.target.value)} />
        </label>
        <div className="cr-actions">
          <button type="button" className="cr-button" onClick={onCancel}>
            {labels.cancel}
          </button>
          <button type="submit" className="cr-button cr-primary" disabled={!validName || !expression.trim()}>
            {labels.add}
          </button>
        </div>
      </form>
    </Modal>
  );
}
