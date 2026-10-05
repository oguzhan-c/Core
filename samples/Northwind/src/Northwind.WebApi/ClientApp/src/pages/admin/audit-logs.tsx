import { useState } from "react";
import { keepPreviousData, useQuery } from "@tanstack/react-query";

import { AuditChanges } from "@/components/common/audit-changes";
import { DataPagination } from "@/components/common/data-pagination";
import { PageHeader } from "@/components/common/page-header";
import { EmptyState, QueryState } from "@/components/common/query-state";
import { AuditActionBadge } from "@/components/common/status";
import { useDebounced } from "@/components/common/use-debounced";
import { Card, CardContent } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import { api } from "@/lib/api";
import { formatDateTime } from "@/lib/format";
import type { AuditLog, Paginate } from "@/lib/types";
import { cn } from "@/lib/utils";

const entityTypes = ["Product", "Customer", "Order", "AppUser"];
const ALL = "all";

export function AdminAuditLogsPage() {
  const [index, setIndex] = useState(0);
  const [entityType, setEntityType] = useState(ALL);
  const [entityId, setEntityId] = useState("");
  const debouncedId = useDebounced(entityId);

  const logs = useQuery({
    queryKey: ["audit-logs", { index, entityType, debouncedId }],
    queryFn: () =>
      api<Paginate<AuditLog>>("/api/audit-logs", {
        query: { index, size: 20, entityType: entityType === ALL ? undefined : entityType, entityId: debouncedId.trim() },
      }),
    placeholderData: keepPreviousData,
  });

  return (
    <>
      <PageHeader
        title="Değişiklik geçmişi"
        description="[Audited] işaretli entity'lerin her değişikliği: kim, ne zaman, hangi alan neyden neye. Şifre ve token hash'leri yazılmaz."
      />
      <div className="flex flex-col gap-3 sm:flex-row">
        <Select
          value={entityType}
          onValueChange={(v) => {
            setEntityType(v);
            setIndex(0);
          }}
        >
          <SelectTrigger className="w-full sm:w-48">
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value={ALL}>Tüm kayıt tipleri</SelectItem>
            {entityTypes.map((t) => (
              <SelectItem key={t} value={t}>
                {t}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
        <Input
          className="sm:w-96"
          placeholder="Kayıt kimliği (Guid)"
          value={entityId}
          onChange={(e) => {
            setEntityId(e.target.value);
            setIndex(0);
          }}
        />
      </div>

      <QueryState isLoading={logs.isLoading} error={logs.error} />
      {logs.data?.count === 0 && <EmptyState title="Kayıt yok" />}
      {!!logs.data?.count && (
        <Card className={cn("py-2", logs.isFetching && "opacity-70")}>
          <CardContent className="px-2">
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>Zaman</TableHead>
                  <TableHead>Kayıt</TableHead>
                  <TableHead>İşlem</TableHead>
                  <TableHead>Değişiklikler</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {logs.data.items.map((log) => (
                  <TableRow key={log.id} className="align-top">
                    <TableCell className="text-xs">
                      {formatDateTime(log.timestamp)}
                      {log.userId && <div className="text-muted-foreground">kullanıcı {log.userId.slice(0, 8)}…</div>}
                    </TableCell>
                    <TableCell>
                      <div className="font-medium">{log.entityType}</div>
                      <button className="text-muted-foreground font-mono text-xs hover:underline" onClick={() => setEntityId(log.entityId)}>
                        {log.entityId.slice(0, 13)}…
                      </button>
                    </TableCell>
                    <TableCell>
                      <AuditActionBadge action={log.action} />
                    </TableCell>
                    <TableCell className="min-w-80 whitespace-normal">
                      <AuditChanges changes={log.changes} />
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </CardContent>
        </Card>
      )}
      <DataPagination page={logs.data} onPageChange={setIndex} />
    </>
  );
}
