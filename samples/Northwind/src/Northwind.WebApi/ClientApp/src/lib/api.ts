// Backend ile konuşan küçük fetch sarmalayıcısı.
// - Cookie'ler (HttpOnly JWT) tarayıcı tarafından otomatik gönderilir; token'a JavaScript'ten hiç dokunulmaz.
// - 401 alınırsa bir kez /api/auth/refresh denenir (eşzamanlı istekler tek yenilemeyi bekler), sonra istek tekrarlanır.
// - Hatalar ProblemDetails'ten ApiError'a çevrilir (status, code, alan hataları).

export interface ProblemDetails {
  title?: string;
  detail?: string;
  status?: number;
  code?: string;
  errors?: Record<string, string[]>;
}

export class ApiError extends Error {
  readonly status: number;
  readonly code?: string;
  readonly errors?: Record<string, string[]>;

  constructor(status: number, problem: ProblemDetails) {
    super(problem.detail || problem.title || `İstek başarısız (${status})`);
    this.status = status;
    this.code = problem.code;
    this.errors = problem.errors;
  }

  /** Alan hatalarını tek metinde toplar (doğrulama hataları için). */
  get description(): string {
    if (!this.errors) return this.message;
    return Object.values(this.errors).flat().join(" ");
  }
}

type QueryValue = string | number | boolean | null | undefined;

export interface RequestOptions {
  method?: "GET" | "POST" | "PUT" | "DELETE";
  body?: unknown;
  query?: Record<string, QueryValue>;
  signal?: AbortSignal;
}

// Bu adreslerde 401 "oturum yok" demektir; yenileme denenmez.
const noRefresh = ["/api/auth/login", "/api/auth/refresh", "/api/auth/logout", "/api/auth/register", "/api/auth/verify-email", "/api/auth/resend-code"];

let refreshing: Promise<boolean> | null = null;

function refreshSession(): Promise<boolean> {
  refreshing ??= fetch("/api/auth/refresh", { method: "POST", credentials: "same-origin" })
    .then((r) => r.ok)
    .catch(() => false)
    .finally(() => {
      // Aynı anda gelen 401'ler aynı yenilemeyi kullansın, sonrakiler yenisini başlatsın.
      setTimeout(() => (refreshing = null), 0);
    });
  return refreshing;
}

export function buildUrl(path: string, query?: Record<string, QueryValue>): string {
  if (!query) return path;
  const params = new URLSearchParams();
  for (const [key, value] of Object.entries(query)) {
    if (value !== undefined && value !== null && value !== "") params.set(key, String(value));
  }
  const qs = params.toString();
  return qs ? `${path}?${qs}` : path;
}

export async function api<T>(path: string, options: RequestOptions = {}): Promise<T> {
  const url = buildUrl(path, options.query);
  const send = () =>
    fetch(url, {
      method: options.method ?? (options.body === undefined ? "GET" : "POST"),
      credentials: "same-origin",
      headers: options.body === undefined ? undefined : { "Content-Type": "application/json" },
      body: options.body === undefined ? undefined : JSON.stringify(options.body),
      signal: options.signal,
    });

  let response = await send();

  if (response.status === 401 && !noRefresh.includes(path) && (await refreshSession())) {
    response = await send();
  }

  if (!response.ok) {
    let problem: ProblemDetails = {};
    try {
      problem = (await response.json()) as ProblemDetails;
    } catch {
      // gövde yok ya da JSON değil
    }
    throw new ApiError(response.status, problem);
  }

  const text = await response.text();
  return (text ? JSON.parse(text) : undefined) as T;
}

/** Hata mesajını kullanıcıya gösterilecek metne çevirir. */
export function errorMessage(error: unknown): string {
  if (error instanceof ApiError) return error.description;
  if (error instanceof Error) return error.message;
  return "Beklenmeyen bir hata oluştu.";
}
