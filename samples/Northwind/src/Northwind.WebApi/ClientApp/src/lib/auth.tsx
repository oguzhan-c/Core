import { createContext, useCallback, useContext, useMemo, type ReactNode } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";

import { api, ApiError } from "@/lib/api";
import type { LoginResponse, Role, TwoFactorPrompt, UserProfile } from "@/lib/types";
import { getPasskey, type RequestOptionsJson } from "@/lib/webauthn";

export const staffRoles: Role[] = ["Admin", "Sales", "Warehouse"];

export interface LoginRequest {
  tenant: string;
  email: string;
  password: string;
}

/** Şifre ile giriş sonucu: ya oturum açıldı ya da ikinci adım (kod) isteniyor. */
export type LoginOutcome = { user: UserProfile; twoFactor?: undefined } | { user?: undefined; twoFactor: TwoFactorPrompt };

interface AuthContextValue {
  user: UserProfile | null;
  isLoading: boolean;
  isStaff: boolean;
  isCustomer: boolean;
  hasRole: (...roles: Role[]) => boolean;
  login: (request: LoginRequest) => Promise<LoginOutcome>;
  /** İki adımlı girişin ikinci adımı (bekleyen giriş sunucunun HttpOnly cookie'sinde). */
  completeTwoFactor: (code: string) => Promise<UserProfile>;
  resendTwoFactorCode: () => Promise<void>;
  /** Şifresiz giriş: cihazdaki passkey ile. */
  loginWithPasskey: (tenant: string) => Promise<UserProfile>;
  logout: () => Promise<void>;
  /** Doğrulama gibi akışlar giriş yapmış kullanıcıyı döndürdüğünde. */
  setUser: (user: UserProfile | null) => void;
}

const AuthContext = createContext<AuthContextValue | null>(null);

export const meQueryKey = ["auth", "me"] as const;

/**
 * Oturum durumu sunucudan (/api/auth/me) okunur: token HttpOnly cookie'de olduğu için tarayıcıda saklanan bir
 * kullanıcı bilgisi yoktur. 401 → giriş yapılmamış.
 */
export function AuthProvider({ children }: { children: ReactNode }) {
  const queryClient = useQueryClient();

  const me = useQuery({
    queryKey: meQueryKey,
    queryFn: async () => {
      try {
        return await api<UserProfile>("/api/auth/me");
      } catch (error) {
        if (error instanceof ApiError && error.status === 401) return null;
        throw error;
      }
    },
    staleTime: 5 * 60_000,
    retry: false,
  });

  const setUser = useCallback((user: UserProfile | null) => queryClient.setQueryData(meQueryKey, user), [queryClient]);

  // Yeni oturum: başka kullanıcının önbelleğe alınmış verileri görünmesin.
  const signedIn = useCallback(
    (user: UserProfile) => {
      queryClient.removeQueries({ predicate: (q) => q.queryKey[0] !== "auth" && q.queryKey[0] !== "store" });
      setUser(user);
      return user;
    },
    [queryClient, setUser]
  );

  const login = useCallback(
    async (request: LoginRequest): Promise<LoginOutcome> => {
      const response = await api<LoginResponse>("/api/auth/login", { method: "POST", body: request });
      if (response.twoFactor) return { twoFactor: response.twoFactor };
      return { user: signedIn(response.user!) };
    },
    [signedIn]
  );

  const completeTwoFactor = useCallback(
    async (code: string) => signedIn(await api<UserProfile>("/api/auth/login/two-factor", { method: "POST", body: { code } })),
    [signedIn]
  );

  const resendTwoFactorCode = useCallback(async () => {
    await api("/api/auth/login/two-factor/resend", { method: "POST" });
  }, []);

  const loginWithPasskey = useCallback(
    async (tenant: string) => {
      const options = await api<RequestOptionsJson>("/api/auth/passkey/options", { method: "POST" });
      const credential = await getPasskey(options);
      return signedIn(await api<UserProfile>("/api/auth/passkey/login", { method: "POST", body: { tenant, credential } }));
    },
    [signedIn]
  );

  const logout = useCallback(async () => {
    try {
      await api("/api/auth/logout", { method: "POST" });
    } finally {
      queryClient.removeQueries({ predicate: (q) => q.queryKey[0] !== "auth" && q.queryKey[0] !== "store" });
      setUser(null);
    }
  }, [queryClient, setUser]);

  const value = useMemo<AuthContextValue>(() => {
    const user = me.data ?? null;
    const hasRole = (...roles: Role[]) => !!user && roles.some((r) => user.roles.includes(r));
    return {
      user,
      isLoading: me.isLoading,
      isStaff: hasRole(...staffRoles),
      isCustomer: hasRole("Customer"),
      hasRole,
      login,
      completeTwoFactor,
      resendTwoFactorCode,
      loginWithPasskey,
      logout,
      setUser,
    };
  }, [me.data, me.isLoading, login, completeTwoFactor, resendTwoFactorCode, loginWithPasskey, logout, setUser]);

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth() {
  const context = useContext(AuthContext);
  if (!context) throw new Error("useAuth, AuthProvider içinde kullanılmalı.");
  return context;
}
