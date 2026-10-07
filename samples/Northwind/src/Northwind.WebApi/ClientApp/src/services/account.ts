import { baseApi } from "@/lib/api";
import type { AccountSecurity, OtpSetup, PasskeyInfo } from "@/lib/types";
import type { CreationOptionsJson } from "@/lib/webauthn";

/** Giriş yapmış kullanıcının hesap güvenliği: iki adımlı doğrulama ve passkey'ler. */
export const accountApi = baseApi.injectEndpoints({
  endpoints: (build) => ({
    getAccountSecurity: build.query<AccountSecurity, void>({
      query: () => "/api/account/security",
      providesTags: ["Security"],
    }),

    /** Her çağrı yeni gizli anahtar üretir (yarım kalan eski kurulum sunucuda silinir); bu yüzden mutation. */
    beginOtpSetup: build.mutation<OtpSetup, void>({
      query: () => ({ url: "/api/account/two-factor/otp/setup", method: "POST" }),
    }),

    enableOtp: build.mutation<void, string>({
      query: (code) => ({ url: "/api/account/two-factor/otp/enable", method: "POST", body: { code } }),
      invalidatesTags: ["Security"],
    }),

    enableEmailTwoFactor: build.mutation<void, void>({
      query: () => ({ url: "/api/account/two-factor/email/enable", method: "POST" }),
      invalidatesTags: ["Security"],
    }),

    disableTwoFactor: build.mutation<void, string>({
      query: (password) => ({ url: "/api/account/two-factor/disable", method: "POST", body: { password } }),
      invalidatesTags: ["Security"],
    }),

    passkeyRegistrationOptions: build.mutation<CreationOptionsJson, void>({
      query: () => ({ url: "/api/account/passkeys/options", method: "POST" }),
    }),

    addPasskey: build.mutation<PasskeyInfo, { name: string; credential: Record<string, unknown> }>({
      query: (body) => ({ url: "/api/account/passkeys", method: "POST", body }),
      invalidatesTags: ["Security"],
    }),

    renamePasskey: build.mutation<void, { id: string; name: string }>({
      query: ({ id, name }) => ({ url: `/api/account/passkeys/${id}`, method: "PUT", body: { name } }),
      invalidatesTags: ["Security"],
    }),

    unlinkExternalLogin: build.mutation<void, string>({
      query: (provider) => ({ url: `/api/account/external-logins/${encodeURIComponent(provider)}`, method: "DELETE" }),
      invalidatesTags: ["Security"],
    }),

    deletePasskey: build.mutation<void, string>({
      query: (id) => ({ url: `/api/account/passkeys/${id}`, method: "DELETE" }),
      invalidatesTags: ["Security"],
    }),
  }),
});

export const {
  useGetAccountSecurityQuery,
  useBeginOtpSetupMutation,
  useEnableOtpMutation,
  useEnableEmailTwoFactorMutation,
  useDisableTwoFactorMutation,
  usePasskeyRegistrationOptionsMutation,
  useAddPasskeyMutation,
  useRenamePasskeyMutation,
  useDeletePasskeyMutation,
  useUnlinkExternalLoginMutation,
} = accountApi;
