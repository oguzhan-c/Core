# @can-core/reporting-react

Can.Core.Reporting için React arayüzü. Bağımlılığı yalnızca React (19+); grafikler SVG, stiller düz CSS.

```tsx
import { createReportingClient, ReportDesigner } from "@can-core/reporting-react";
import "@can-core/reporting-react/styles.css";

// bileşen dışında bir kez oluştur
const client = createReportingClient({
  baseUrl: "/api/reporting",          // sunucuda app.MapCanReporting("/api/reporting")
  fetch: myFetchWithTokenRefresh,     // isteğe bağlı: 401'de oturum yenileme vb.
});

export function ReportsPage() {
  return <ReportDesigner client={client} locale="tr-TR" currency="TRY" onNotify={(message, kind) => console.log(kind, message)} />;
}
```

| Bileşen | |
|---|---|
| `ReportDesigner` | Tam tasarımcı: kaynak seçimi, sürükle-bırak alanlar, ayarlar, tablo/grafik, dışa aktarma, kayıtlı raporlar |
| `PivotTable` | `ReportResult`'ı çapraz tablo olarak çizer (çok düzeyli sütun başlıkları, girintili satır grupları, daraltma) |
| `ReportChart` | Sütun / çizgi / pasta (SVG); kategori = en alt satırlar, seri = en alt sütunlar |
| `createReportingClient` | Uçların tipli istemcisi (`sources`, `run`, `export`, `savedReports` ...) |

Tema: `.cr-designer, .cr-table-wrap, .cr-chart { --cr-bg; --cr-fg; --cr-muted; --cr-border; --cr-panel; --cr-accent;
--cr-accent-fg; --cr-danger; --cr-header; --cr-group; --cr-total; --cr-radius; --cr-chart-1..8 }`. `.dark` sınıfı ve
`prefers-color-scheme` için koyu varsayılanlar vardır.

Çeviri: `labels={{ run: "Run", rows: "Rows", aggregates: { ...turkishLabels.aggregates, Sum: "Sum" } }}`.

Uygulamaya kurulum (monorepo dışından yayınlanmadan):

```jsonc
// package.json
"@can-core/reporting-react": "file:../../clients/reporting-react"
```
```ini
# .npmrc — paket kopya olarak kurulsun (symlink değil): React ve tipler uygulamanınkinden çözülür
install-links=true
```

Paket kaynak (TypeScript) olarak dağıtılır; Vite/esbuild doğrudan derler. Tip kontrolü: `npm install && npm run typecheck`.
