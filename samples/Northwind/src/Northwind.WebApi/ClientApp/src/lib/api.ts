// RTK Query temeli: tek bir API dilimi (createApi), özellik dosyaları buna endpoint ekler (services/*).
// - Cookie'ler (HttpOnly JWT) tarayıcı tarafından otomatik gönderilir; token'a JavaScript'ten hiç dokunulmaz.
// - 401 alınırsa bir kez /api/auth/refresh denenir (eşzamanlı istekler tek yenilemeyi bekler), sonra istek tekrarlanır.
// - Hatalar sunucunun ProblemDetails'inden serileştirilebilir ApiError nesnesine çevrilir (status, code, alan hataları).
import {
  createApi,
  fetchBaseQuery,
  type BaseQueryFn,
  type FetchArgs,
  type FetchBaseQueryError,
} from "@reduxjs/toolkit/query/react";

export interface ProblemDetails {
  title?: string;
  detail?: string;
  status?: number;
  code?: string;
  errors?: Record<string, string[]>;
  /** Birden fazla iş hatası olduğunda hepsi (ör. sepetteki birkaç ürünün stoğu yetmedi). */
  details?: { code: string; description: string }[];
}

/** RTK Query hatası. Düz nesnedir (Redux state'inde saklanabilsin); `status` 0 ise sunucuya ulaşılamadı. */
export interface ApiError {
  status: number;
  message: string;
  code?: string;
  errors?: Record<string, string[]>;
  details?: { code: string; description: string }[];
}

export function isApiError(error: unknown): error is ApiError {
  return typeof error === "object" && error !== null && typeof (error as ApiError).status === "number" && typeof (error as ApiError).message === "string";
}

/** Hata mesajını kullanıcıya gösterilecek metne çevirir (alan hataları ve birden fazla iş hatası birleştirilir). */
export function errorMessage(error: unknown): string {
  if (isApiError(error)) {
    if (error.errors) return Object.values(error.errors).flat().join(" ");
    if (error.details?.length) return error.details.map((d) => d.description).join(" ");
    return error.message;
  }
  if (error instanceof Error) return error.message;
  return "Beklenmeyen bir hata oluştu.";
}

function toApiError(error: FetchBaseQueryError): ApiError {
  if (typeof error.status === "number") {
    const problem = (error.data ?? {}) as ProblemDetails;
    return {
      status: error.status,
      message: problem.detail || problem.title || `İstek başarısız (${error.status})`,
      code: problem.code,
      errors: problem.errors,
      details: problem.details,
    };
  }

  return { status: 0, message: error.status === "PARSING_ERROR" ? "Sunucu yanıtı okunamadı." : "Sunucuya ulaşılamadı." };
}

/** Sorgu parametrelerinden boş olanları (undefined, null, "") atar. */
export function params(values: Record<string, string | number | boolean | null | undefined>) {
  const result: Record<string, string | number | boolean> = {};
  for (const [key, value] of Object.entries(values)) {
    if (value !== undefined && value !== null && value !== "") result[key] = value;
  }
  return result;
}

const rawBaseQuery = fetchBaseQuery({ baseUrl: "/", credentials: "same-origin" });

// Bu adreslerde 401 "oturum yok/geçersiz" demektir; yenileme denenmez.
const noRefresh = new Set([
  "/api/auth/login",
  "/api/auth/login/two-factor",
  "/api/auth/login/two-factor/resend",
  "/api/auth/passkey/options",
  "/api/auth/passkey/login",
  "/api/auth/refresh",
  "/api/auth/logout",
  "/api/auth/register",
  "/api/auth/verify-email",
  "/api/auth/resend-code",
]);

let refreshing: Promise<boolean> | null = null;

const baseQuery: BaseQueryFn<string | FetchArgs, unknown, ApiError> = async (args, api, extraOptions) => {
  const url = typeof args === "string" ? args : args.url;
  let result = await rawBaseQuery(args, api, extraOptions);

  if (result.error?.status === 401 && !noRefresh.has(url)) {
    // Aynı anda gelen 401'ler tek yenilemeyi bekler.
    refreshing ??= Promise.resolve(rawBaseQuery({ url: "/api/auth/refresh", method: "POST" }, api, extraOptions))
      .then((r) => !r.error)
      .finally(() => setTimeout(() => (refreshing = null), 0));

    if (await refreshing) {
      result = await rawBaseQuery(args, api, extraOptions);
    } else if (url !== "/api/auth/me") {
      // Oturum yenilenemedi: kullanıcı bilgisini yeniden sor (null döner, arayüz çıkış yapmış duruma geçer).
      api.dispatch(baseApi.util.invalidateTags(["Me"]));
    }
  }

  return result.error ? { error: toApiError(result.error), meta: result.meta } : { data: result.data, meta: result.meta };
};

/** Kullanıcıya özel veriler: giriş/çıkışta yeniden yüklenir (başka kullanıcının önbelleği görünmesin). */
export const userScopedTags = [
  "Category",
  "Product",
  "Supplier",
  "Shipper",
  "Customer",
  "Order",
  "Report",
  "Dashboard",
  "AuditLog",
  "Outbox",
  "User",
  "Security",
  "MyOrder",
] as const;

export const baseApi = createApi({
  reducerPath: "api",
  baseQuery,
  tagTypes: ["Me", "StoreCatalog", "Mailbox", ...userScopedTags],
  // Bileşen yeniden açıldığında 30 sn'den eski veriyi tazele; daha yenisi önbellekten gelir.
  refetchOnMountOrArgChange: 30,
  endpoints: () => ({}),
});
