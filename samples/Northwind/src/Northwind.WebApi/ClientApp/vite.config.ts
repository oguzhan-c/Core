import path from "node:path";
import tailwindcss from "@tailwindcss/vite";
import react from "@vitejs/plugin-react";
import { defineConfig } from "vite";

// Geliştirme: `npm run dev` (5173) → /api, /swagger ... istekleri .NET uygulamasına (5180) aktarılır;
// cookie'ler aynı adresten geldiği için oturum sorunsuz çalışır.
// Yayın: `npm run build` çıktıyı ../wwwroot'a yazar; .NET uygulaması dosyaları kendisi sunar.
const backend = process.env.BACKEND_URL ?? "http://localhost:5180";

export default defineConfig({
  plugins: [react(), tailwindcss()],
  resolve: {
    alias: { "@": path.resolve(__dirname, "./src") },
  },
  server: {
    port: 5173,
    proxy: {
      "/api": backend,
      "/openapi": backend,
      "/swagger": backend,
      "/scalar": backend,
    },
  },
  build: {
    outDir: "../wwwroot",
    emptyOutDir: true,
    rollupOptions: {
      output: {
        // Kütüphaneler ayrı dosyalarda: uygulama değişince tarayıcı önbelleği korunur.
        manualChunks: {
          react: ["react", "react-dom", "react-router"],
          query: ["@tanstack/react-query"],
          ui: ["radix-ui", "lucide-react", "sonner"],
          charts: ["recharts"],
        },
      },
    },
  },
});
