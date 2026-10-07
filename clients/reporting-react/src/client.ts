import type {
  ReportDefinition,
  ReportExportRequest,
  ReportResult,
  ReportSource,
  ReportSourceDetail,
  SavedReport,
  SavedReportSummary,
  SaveReportRequest,
} from "./types";

/** Sunucunun ProblemDetails yanıtından üretilen hata. */
export class ReportingError extends Error {
  readonly status: number;
  readonly code?: string;
  readonly errors?: Record<string, string[]>;

  constructor(status: number, message: string, code?: string, errors?: Record<string, string[]>) {
    super(message);
    this.name = "ReportingError";
    this.status = status;
    this.code = code;
    this.errors = errors;
  }
}

export interface ReportingClientOptions {
  /** Uçların kökü (sunucuda <c>MapCanReporting</c>'e verilen yol). Varsayılan <c>/api/reporting</c>. */
  baseUrl?: string;
  /**
   * İstekleri yapan fonksiyon. Oturum yenileme (401 → refresh → tekrar) gibi uygulamaya özgü davranış için kendi
   * fetch sarmalayıcını ver. Varsayılan: <c>fetch</c>, cookie'ler gönderilir (<c>credentials: "same-origin"</c>).
   */
  fetch?: (input: string, init: RequestInit) => Promise<Response>;
}

export interface ExportedFile {
  blob: Blob;
  fileName: string;
}

export interface ReportingClient {
  sources(signal?: AbortSignal): Promise<ReportSource[]>;
  source(name: string, signal?: AbortSignal): Promise<ReportSourceDetail>;
  run(definition: ReportDefinition, signal?: AbortSignal): Promise<ReportResult>;
  export(request: ReportExportRequest, signal?: AbortSignal): Promise<ExportedFile>;
  savedReports(signal?: AbortSignal): Promise<SavedReportSummary[]>;
  savedReport(id: string, signal?: AbortSignal): Promise<SavedReport>;
  createReport(request: SaveReportRequest): Promise<SavedReport>;
  updateReport(id: string, request: SaveReportRequest): Promise<SavedReport>;
  deleteReport(id: string): Promise<void>;
}

interface Problem {
  title?: string;
  detail?: string;
  code?: string;
  errors?: Record<string, string[]>;
}

async function toError(response: Response): Promise<ReportingError> {
  let problem: Problem = {};
  try {
    problem = (await response.json()) as Problem;
  } catch {
    // gövde yok ya da JSON değil
  }
  const fieldErrors = problem.errors ? Object.values(problem.errors).flat().join(" ") : "";
  const message = fieldErrors || problem.detail || problem.title || `İstek başarısız (${response.status})`;
  return new ReportingError(response.status, message, problem.code, problem.errors);
}

/** <c>Content-Disposition</c>'tan dosya adı (UTF-8 <c>filename*</c> öncelikli). */
export function fileNameFrom(header: string | null, fallback: string): string {
  if (!header) return fallback;
  const star = /filename\*\s*=\s*(?:UTF-8|utf-8)''([^;]+)/.exec(header);
  if (star) {
    try {
      return decodeURIComponent(star[1].trim().replace(/^"|"$/g, ""));
    } catch {
      // bozuk kodlama: düz ada düş
    }
  }
  const plain = /filename\s*=\s*"?([^";]+)"?/.exec(header);
  return plain ? plain[1].trim() : fallback;
}

/** Tarayıcıda dosyayı indirir. */
export function downloadFile(file: ExportedFile): void {
  const url = URL.createObjectURL(file.blob);
  const link = document.createElement("a");
  link.href = url;
  link.download = file.fileName;
  document.body.appendChild(link);
  link.click();
  link.remove();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}

export function createReportingClient(options: ReportingClientOptions = {}): ReportingClient {
  const base = (options.baseUrl ?? "/api/reporting").replace(/\/$/, "");
  const send = options.fetch ?? ((input: string, init: RequestInit) => fetch(input, { credentials: "same-origin", ...init }));

  async function request(path: string, init: RequestInit = {}): Promise<Response> {
    const headers = new Headers(init.headers);
    headers.set("Accept", "application/json");
    if (init.body !== undefined) headers.set("Content-Type", "application/json");
    const response = await send(base + path, { ...init, headers });
    if (!response.ok) throw await toError(response);
    return response;
  }

  async function json<T>(path: string, init?: RequestInit): Promise<T> {
    const response = await request(path, init);
    return (await response.json()) as T;
  }

  const body = (value: unknown) => JSON.stringify(value);

  return {
    sources: (signal) => json("/sources", { signal }),
    source: (name, signal) => json(`/sources/${encodeURIComponent(name)}`, { signal }),
    run: (definition, signal) => json("/run", { method: "POST", body: body(definition), signal }),
    async export(exportRequest, signal) {
      const response = await request("/export", { method: "POST", body: body(exportRequest), signal });
      const extension = exportRequest.format === "Xlsx" ? ".xlsx" : exportRequest.format === "Pdf" ? ".pdf" : ".csv";
      return {
        blob: await response.blob(),
        fileName: fileNameFrom(response.headers.get("Content-Disposition"), `rapor${extension}`),
      };
    },
    savedReports: (signal) => json("/saved", { signal }),
    savedReport: (id, signal) => json(`/saved/${encodeURIComponent(id)}`, { signal }),
    createReport: (saveRequest) => json("/saved", { method: "POST", body: body(saveRequest) }),
    updateReport: (id, saveRequest) => json(`/saved/${encodeURIComponent(id)}`, { method: "PUT", body: body(saveRequest) }),
    async deleteReport(id) {
      await request(`/saved/${encodeURIComponent(id)}`, { method: "DELETE" });
    },
  };
}
