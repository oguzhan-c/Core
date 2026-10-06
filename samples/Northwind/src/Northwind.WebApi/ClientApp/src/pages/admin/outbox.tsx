import { useState } from "react";
import { InfoIcon, RefreshCwIcon, RotateCcwIcon, SendIcon } from "lucide-react";
import { toast } from "sonner";

import { DataPagination } from "@/components/common/data-pagination";
import { PageHeader } from "@/components/common/page-header";
import { EmptyState, QueryState } from "@/components/common/query-state";
import { OutboxStatusBadge } from "@/components/common/status";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Card, CardContent } from "@/components/ui/card";
import { Dialog, DialogContent, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import { Tabs, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { errorMessage } from "@/lib/api";
import { formatDateTime } from "@/lib/format";
import type { OutboxMessage, OutboxStatus } from "@/lib/types";
import { useGetOutboxMessagesQuery, useRetryOutboxMessageMutation, useRunReorderReportMutation } from "@/services/admin";

export function AdminOutboxPage() {
  const [index, setIndex] = useState(0);
  const [status, setStatus] = useState<OutboxStatus | "all">("all");
  const [selected, setSelected] = useState<OutboxMessage | null>(null);

  const messages = useGetOutboxMessagesQuery(
    { index, size: 20, status: status === "all" ? undefined : status },
    { pollingInterval: 5000 } // işlemci 5 saniyede bir çalışır
  );

  // Yeniden deneme "Outbox" etiketini geçersiz kılar: liste kendiliğinden yenilenir.
  const [retryMessage] = useRetryOutboxMessageMutation();
  const [runReorderReport, { isLoading: reportQueued }] = useRunReorderReportMutation();

  async function retry(id: string) {
    try {
      await retryMessage(id).unwrap();
      toast.success("Mesaj yeniden denenecek.");
    } catch (e) {
      toast.error(errorMessage(e));
    }
  }

  async function runReport() {
    try {
      await runReorderReport().unwrap();
      toast.success("Yeniden sipariş raporu kuyruğa alındı (IBackgroundJobQueue).");
    } catch (e) {
      toast.error(errorMessage(e));
    }
  }

  return (
    <>
      <PageHeader
        title="Outbox & arka plan işleri"
        description="Kalıcı event'ler (IIntegrationEvent) önce bu tabloya yazılır, sonra arka planda yayınlanır."
        actions={
          <>
            <Button variant="outline" onClick={() => messages.refetch()}>
              <RefreshCwIcon /> Yenile
            </Button>
            <Button onClick={runReport} disabled={reportQueued}>
              <SendIcon /> Raporu şimdi çalıştır
            </Button>
          </>
        }
      />

      <Alert>
        <InfoIcon />
        <AlertTitle>Nasıl denenir?</AlertTitle>
        <AlertDescription>
          Bir siparişi kargoya ver ya da bir ürünü satıştan kaldır: kayıt yeni bir "Bekliyor" mesajı oluşturur, birkaç saniye içinde
          "Yayınlandı" olur ve bildirim e-postası geliştirme posta kutusuna düşer. Handler hata verirse mesaj "Hatalı" kalır ve tekrar denenir.
        </AlertDescription>
      </Alert>

      <Tabs
        value={status}
        onValueChange={(v) => {
          setStatus(v as OutboxStatus | "all");
          setIndex(0);
        }}
      >
        <TabsList>
          <TabsTrigger value="all">Tümü</TabsTrigger>
          <TabsTrigger value="Pending">Bekliyor</TabsTrigger>
          <TabsTrigger value="Processed">Yayınlandı</TabsTrigger>
          <TabsTrigger value="Failed">Hatalı</TabsTrigger>
        </TabsList>
      </Tabs>

      <QueryState isLoading={messages.isLoading} error={messages.error} />
      {messages.data?.count === 0 && <EmptyState title="Mesaj yok" />}
      {!!messages.data?.count && (
        <Card className="py-2">
          <CardContent className="px-2">
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>Event</TableHead>
                  <TableHead>Oluştu</TableHead>
                  <TableHead>Yayınlandı</TableHead>
                  <TableHead className="text-right">Deneme</TableHead>
                  <TableHead>Durum</TableHead>
                  <TableHead />
                </TableRow>
              </TableHeader>
              <TableBody>
                {messages.data.items.map((m) => (
                  <TableRow key={m.id}>
                    <TableCell>
                      <button className="font-medium hover:underline" onClick={() => setSelected(m)}>
                        {m.eventType}
                      </button>
                      {m.lastError && <div className="text-destructive line-clamp-1 max-w-80 text-xs">{m.lastError}</div>}
                    </TableCell>
                    <TableCell>{formatDateTime(m.occurredAt)}</TableCell>
                    <TableCell>{formatDateTime(m.processedAt)}</TableCell>
                    <TableCell className="text-right">{m.attempts}</TableCell>
                    <TableCell>
                      <OutboxStatusBadge status={m.status} />
                    </TableCell>
                    <TableCell className="text-right">
                      {m.status === "Failed" && (
                        <Button variant="outline" size="sm" onClick={() => retry(m.id)}>
                          <RotateCcwIcon /> Tekrar dene
                        </Button>
                      )}
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </CardContent>
        </Card>
      )}
      <DataPagination page={messages.data} onPageChange={setIndex} />

      {selected && (
        <Dialog open onOpenChange={(open) => !open && setSelected(null)}>
          <DialogContent className="sm:max-w-2xl">
            <DialogHeader>
              <DialogTitle>{selected.eventType}</DialogTitle>
            </DialogHeader>
            <div className="space-y-3 text-sm">
              <p className="text-muted-foreground">EventId: {selected.eventId}</p>
              <pre className="bg-muted overflow-x-auto rounded-md p-3 text-xs">{JSON.stringify(JSON.parse(selected.payload), null, 2)}</pre>
              {selected.lastError && <pre className="bg-destructive/10 text-destructive max-h-60 overflow-auto rounded-md p-3 text-xs">{selected.lastError}</pre>}
            </div>
          </DialogContent>
        </Dialog>
      )}
    </>
  );
}
