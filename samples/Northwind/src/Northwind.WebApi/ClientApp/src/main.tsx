import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { MutationCache, QueryCache, QueryClient, QueryClientProvider } from "@tanstack/react-query";

import { App } from "@/App";
import { Toaster } from "@/components/ui/sonner";
import { ApiError } from "@/lib/api";
import { AuthProvider, meQueryKey } from "@/lib/auth";
import "@/index.css";

// Oturum yenilenemezse (refresh token da geçersiz) kullanıcı çıkış yapmış sayılır.
function onApiError(error: unknown) {
  if (error instanceof ApiError && error.status === 401) queryClient.setQueryData(meQueryKey, null);
}

const queryClient = new QueryClient({
  queryCache: new QueryCache({ onError: onApiError }),
  mutationCache: new MutationCache({ onError: onApiError }),
  defaultOptions: {
    queries: {
      staleTime: 30_000,
      refetchOnWindowFocus: false,
      retry: (count, error) => !(error instanceof ApiError && error.status < 500) && count < 2,
    },
  },
});

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <QueryClientProvider client={queryClient}>
      <AuthProvider>
        <App />
        <Toaster position="top-right" />
      </AuthProvider>
    </QueryClientProvider>
  </StrictMode>
);
