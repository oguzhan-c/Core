import type { ThunkDispatch, UnknownAction } from "@reduxjs/toolkit";

import { baseApi, isApiError, userScopedTags, type ApiError } from "@/lib/api";
import type { AuthFeatures, LoginResponse, RegisterResult, UserProfile } from "@/lib/types";
import type { RequestOptionsJson } from "@/lib/webauthn";

export interface LoginRequest {
  tenant: string;
  email: string;
  password: string;
}

export interface RegisterRequest {
  tenant: string;
  email: string;
  password: string;
  firstName: string;
  lastName: string;
  companyName: string;
  phone: string | null;
}

/**
 * Oturum işlemleri. Kullanıcı bilgisi sunucudan okunur (/api/auth/me): token HttpOnly cookie'de olduğu için tarayıcıda
 * saklanan bir kullanıcı bilgisi yoktur. 401 → giriş yapılmamış (null).
 */
export const authApi = baseApi.injectEndpoints({
  endpoints: (build) => ({
    getMe: build.query<UserProfile | null, void>({
      async queryFn(_arg, _api, _extra, baseQuery) {
        const result = await baseQuery("/api/auth/me");
        if (result.error) {
          const error = result.error as ApiError;
          return isApiError(error) && error.status === 401 ? { data: null } : { error };
        }
        return { data: result.data as UserProfile };
      },
      providesTags: ["Me"],
    }),

    getAuthFeatures: build.query<AuthFeatures, void>({
      query: () => "/api/auth/features",
      keepUnusedDataFor: 3600,
    }),

    login: build.mutation<LoginResponse, LoginRequest>({
      query: (body) => ({ url: "/api/auth/login", method: "POST", body }),
      async onQueryStarted(_arg, { dispatch, queryFulfilled }) {
        const { data } = await queryFulfilled.catch(() => ({ data: null }));
        if (data?.user) signedIn(dispatch, data.user);
      },
    }),

    completeTwoFactor: build.mutation<UserProfile, string>({
      query: (code) => ({ url: "/api/auth/login/two-factor", method: "POST", body: { code } }),
      onQueryStarted: signInWhenFulfilled,
    }),

    resendTwoFactorCode: build.mutation<void, void>({
      query: () => ({ url: "/api/auth/login/two-factor/resend", method: "POST" }),
    }),

    passkeyLoginOptions: build.mutation<RequestOptionsJson, void>({
      query: () => ({ url: "/api/auth/passkey/options", method: "POST" }),
    }),

    passkeyLogin: build.mutation<UserProfile, { tenant: string; credential: Record<string, unknown> }>({
      query: (body) => ({ url: "/api/auth/passkey/login", method: "POST", body }),
      onQueryStarted: signInWhenFulfilled,
    }),

    register: build.mutation<RegisterResult, RegisterRequest>({
      query: (body) => ({ url: "/api/auth/register", method: "POST", body }),
    }),

    verifyEmail: build.mutation<UserProfile, { tenant: string; email: string; code: string }>({
      query: (body) => ({ url: "/api/auth/verify-email", method: "POST", body }),
      onQueryStarted: signInWhenFulfilled,
    }),

    resendVerificationCode: build.mutation<void, { tenant: string; email: string }>({
      query: (body) => ({ url: "/api/auth/resend-code", method: "POST", body }),
    }),

    logout: build.mutation<void, void>({
      query: () => ({ url: "/api/auth/logout", method: "POST" }),
      // Sunucu yanıt vermese de tarayıcı tarafında çıkış yapılmış sayılır.
      async onQueryStarted(_arg, { dispatch, queryFulfilled }) {
        try {
          await queryFulfilled;
        } catch {
          // yok say
        } finally {
          dispatch(authApi.util.upsertQueryData("getMe", undefined, null));
        }
      },
    }),
  }),
});

// eslint-disable-next-line @typescript-eslint/no-explicit-any
type Dispatch = ThunkDispatch<any, any, UnknownAction>;

/** Yeni oturum: kullanıcıyı önbelleğe yaz, önceki kullanıcıya ait verileri yeniden yüklet. */
function signedIn(dispatch: Dispatch, user: UserProfile) {
  dispatch(baseApi.util.invalidateTags([...userScopedTags]));
  dispatch(authApi.util.upsertQueryData("getMe", undefined, user));
}

/** Kullanıcı döndüren giriş adımları başarılı olunca oturumu açar. */
async function signInWhenFulfilled(_arg: unknown, { dispatch, queryFulfilled }: { dispatch: Dispatch; queryFulfilled: Promise<{ data: UserProfile }> }) {
  try {
    const { data } = await queryFulfilled;
    signedIn(dispatch, data);
  } catch {
    // hata bileşende gösterilir
  }
}

export const {
  useGetMeQuery,
  useGetAuthFeaturesQuery,
  useLoginMutation,
  useCompleteTwoFactorMutation,
  useResendTwoFactorCodeMutation,
  usePasskeyLoginOptionsMutation,
  usePasskeyLoginMutation,
  useRegisterMutation,
  useVerifyEmailMutation,
  useResendVerificationCodeMutation,
  useLogoutMutation,
} = authApi;
