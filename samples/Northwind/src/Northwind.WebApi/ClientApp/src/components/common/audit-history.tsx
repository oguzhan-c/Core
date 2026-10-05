import { useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { HistoryIcon } from "lucide-react";

import { AuditChanges } from "@/components/common/audit-changes";
import { DataPagination } from "@/components/common/data-pagination";
import { EmptyState, QueryState } from "@/components/common/query-state";
import { AuditActionBadge } from "@/components/common/status";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { api } from "@/lib/api";
import { formatDateTime } from "@/lib/format";
import type { AuditLog, Paginate } from "@/lib/types";

/** Bir kaydın değişiklik geçmişi (yalnızca yöneticiler görebilir; diğerlerine 403 döner). */
export function AuditHistory({ entityType, entityId }: { entityType: string; entityId: string }) {
  const [index, setIndex] = useState(0);
  const query = useQuery({
    queryKey: ["audit-logs", entityType, entityId, index],
    queryFn: () => api<Paginate<AuditLog>>("/api/audit-logs", { query: { entityType, entityId, index, size: 10 } }),
    retry: false,
  });

  return (
    <Card>
      <CardHeader>
        <CardTitle className="flex items-center gap-2">
          <HistoryIcon className="size-4" /> Değişiklik geçmişi
        </CardTitle>
        <CardDescription>Audit trail: kim, ne zaman, hangi alanı neyden neye değiştirdi.</CardDescription>
      </CardHeader>
      <CardContent className="space-y-4">
        <QueryState isLoading={query.isLoading} error={query.error} rows={3} />
        {query.data && query.data.count === 0 && <EmptyState title="Kayıt yok" />}
        <ol className="space-y-3">
          {query.data?.items.map((log) => (
            <li key={log.id} className="rounded-md border p-3">
              <div className="mb-2 flex flex-wrap items-center gap-2 text-sm">
                <AuditActionBadge action={log.action} />
                <span className="text-muted-foreground">{formatDateTime(log.timestamp)}</span>
                {log.userId && <span className="text-muted-foreground text-xs">kullanıcı {log.userId.slice(0, 8)}…</span>}
              </div>
              <AuditChanges changes={log.changes} />
            </li>
          ))}
        </ol>
        <DataPagination page={query.data} onPageChange={setIndex} />
      </CardContent>
    </Card>
  );
}
