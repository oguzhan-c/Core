import { createContext, useCallback, useContext, useMemo, type ReactNode } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";

import { api, ApiError } from "@/lib/api";
import type { Role, UserProfile } from "@/lib/types";

export const staffRoles: Role[] = ["Admin", "Sales", "Warehouse"];

export interface LoginRequest {
  tenant: string;
  email: string;
  password: string;
}

interface AuthContextValue {
  user: UserProfile | null;
  isLoading: boolean;
  isStaff: boolean;
  isCustomer: boolean;
  hasRole: (...roles: Role[]) => boolean;
  login: (request: LoginRequest) => Promise<UserProfile>;
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

  const login = useCallback(
    async (request: LoginRequest) => {
      const user = await api<UserProfile>("/api/auth/login", { method: "POST", body: request });
      // Başka kullanıcının önbelleğe alınmış verileri görünmesin.
      queryClient.removeQueries({ predicate: (q) => q.queryKey[0] !== "auth" && q.queryKey[0] !== "store" });
      setUser(user);
      return user;
    },
    [queryClient, setUser]
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
      logout,
      setUser,
    };
  }, [me.data, me.isLoading, login, logout, setUser]);

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth() {
  const context = useContext(AuthContext);
  if (!context) throw new Error("useAuth, AuthProvider içinde kullanılmalı.");
  return context;
}
