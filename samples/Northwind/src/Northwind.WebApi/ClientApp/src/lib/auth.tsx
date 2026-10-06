import { useCallback, useMemo } from "react";

import { useAppDispatch } from "@/app/hooks";
import type { Role, TwoFactorPrompt, UserProfile } from "@/lib/types";
import { getPasskey } from "@/lib/webauthn";
import {
  authApi,
  useCompleteTwoFactorMutation,
  useGetMeQuery,
  useLoginMutation,
  useLogoutMutation,
  usePasskeyLoginMutation,
  usePasskeyLoginOptionsMutation,
  useResendTwoFactorCodeMutation,
  type LoginRequest,
} from "@/services/auth";

export type { LoginRequest } from "@/services/auth";

export const staffRoles: Role[] = ["Admin", "Sales", "Warehouse"];

/** Şifre ile giriş sonucu: ya oturum açıldı ya da ikinci adım (kod) isteniyor. */
export type LoginOutcome = { user: UserProfile; twoFactor?: undefined } | { user?: undefined; twoFactor: TwoFactorPrompt };

/**
 * Oturum durumu ve işlemleri. Kullanıcı bilgisi RTK Query önbelleğindeki /api/auth/me sonucudur (tüm bileşenler aynı
 * önbelleği paylaşır, istek bir kez gider). Giriş/çıkış bu önbelleği günceller.
 */
export function useAuth() {
  const dispatch = useAppDispatch();
  const me = useGetMeQuery();

  const [loginMutation] = useLoginMutation();
  const [completeTwoFactorMutation] = useCompleteTwoFactorMutation();
  const [resendTwoFactorCodeMutation] = useResendTwoFactorCodeMutation();
  const [passkeyOptionsMutation] = usePasskeyLoginOptionsMutation();
  const [passkeyLoginMutation] = usePasskeyLoginMutation();
  const [logoutMutation] = useLogoutMutation();

  const login = useCallback(
    async (request: LoginRequest): Promise<LoginOutcome> => {
      const response = await loginMutation(request).unwrap();
      return response.twoFactor ? { twoFactor: response.twoFactor } : { user: response.user! };
    },
    [loginMutation]
  );

  const completeTwoFactor = useCallback((code: string) => completeTwoFactorMutation(code).unwrap(), [completeTwoFactorMutation]);

  const resendTwoFactorCode = useCallback(() => resendTwoFactorCodeMutation().unwrap(), [resendTwoFactorCodeMutation]);

  /** Şifresiz giriş: cihazdaki passkey ile (seçenekler → cihaz imzası → doğrulama). */
  const loginWithPasskey = useCallback(
    async (tenant: string) => {
      const options = await passkeyOptionsMutation().unwrap();
      const credential = await getPasskey(options);
      return passkeyLoginMutation({ tenant, credential }).unwrap();
    },
    [passkeyOptionsMutation, passkeyLoginMutation]
  );

  const logout = useCallback(async () => {
    await logoutMutation();
  }, [logoutMutation]);

  /** Doğrulama gibi akışlar giriş yapmış kullanıcıyı döndürdüğünde. */
  const setUser = useCallback(
    (user: UserProfile | null) => dispatch(authApi.util.upsertQueryData("getMe", undefined, user)),
    [dispatch]
  );

  return useMemo(() => {
    const user = me.data ?? null;
    const hasRole = (...roles: Role[]) => !!user && roles.some((r) => user.roles.includes(r));
    /** Sunucudaki kuralla aynı: Admin her şeyi geçer; "orders.*" joker olarak eşleşir. */
    const hasPermission = (permission: string) =>
      !!user &&
      (user.roles.includes("Admin") ||
        (user.permissions ?? []).some(
          (p) => p === "*" || p === permission || (p.endsWith(".*") && permission.startsWith(p.slice(0, -1)))
        ));
    return {
      user,
      isLoading: me.isLoading,
      isStaff: hasRole(...staffRoles),
      isCustomer: hasRole("Customer"),
      hasRole,
      hasPermission,
      login,
      completeTwoFactor,
      resendTwoFactorCode,
      loginWithPasskey,
      logout,
      setUser,
    };
  }, [me.data, me.isLoading, login, completeTwoFactor, resendTwoFactorCode, loginWithPasskey, logout, setUser]);
}
