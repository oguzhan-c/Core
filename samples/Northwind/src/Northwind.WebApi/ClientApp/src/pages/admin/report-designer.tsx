// Rapor tasarımcısı: Core'daki @can-core/reporting-react paketi + sunucuda MapCanReporting("/api/reporting").
// Hangi veri kaynaklarının görüneceğine sunucu karar verir (reports.sales / reports.inventory yetkileri, Admin hepsi).
import { createReportingClient, ReportDesigner } from "@can-core/reporting-react";
import "@can-core/reporting-react/styles.css";
import { useSearchParams } from "react-router";
import { toast } from "sonner";

import { PageHeader } from "@/components/common/page-header";

let refreshing: Promise<boolean> | null = null;

/** Cookie'li istek; 401 gelirse oturumu bir kez yenileyip tekrar dener (RTK Query tabanıyla aynı davranış). */
async function fetchWithRefresh(input: string, init: RequestInit): Promise<Response> {
  const send = () => fetch(input, { credentials: "same-origin", ...init });
  const response = await send();
  if (response.status !== 401) return response;

  refreshing ??= fetch("/api/auth/refresh", { method: "POST", credentials: "same-origin" })
    .then((r) => r.ok)
    .catch(() => false)
    .finally(() => setTimeout(() => (refreshing = null), 0));
  return (await refreshing) ? send() : response;
}

// Bileşen dışında bir kez oluşturulur (her çizimde yeni istemci yüklemeleri tekrarlatır).
const client = createReportingClient({ baseUrl: "/api/reporting", fetch: fetchWithRefresh });

const notify = (message: string, kind: "success" | "error") => (kind === "error" ? toast.error(message) : toast.success(message));

export function AdminReportDesignerPage() {
  const [params, setParams] = useSearchParams();

  return (
    <div className="space-y-6">
      <PageHeader
        title="Rapor tasarımcısı"
        description="Alanları satır, sütun ve değerlere sürükleyin; sonuç anında hesaplanır. Raporu kaydedip ekiple paylaşabilir, Excel/PDF/CSV indirebilirsiniz."
      />
      <ReportDesigner
        client={client}
        locale="tr-TR"
        currency="TRY"
        initialReportId={params.get("report") ?? undefined}
        onNotify={notify}
        onReportChange={(report) =>
          setParams(
            (current) => {
              const next = new URLSearchParams(current);
              if (report) next.set("report", report.id);
              else next.delete("report");
              return next;
            },
            { replace: true },
          )
        }
      />
    </div>
  );
}
