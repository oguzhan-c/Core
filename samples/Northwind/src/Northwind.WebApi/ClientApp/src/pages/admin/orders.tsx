import { useState } from "react";
import { Link, useNavigate, useParams, useSearchParams } from "react-router";
import { ArrowLeftIcon, BanIcon, TruckIcon } from "lucide-react";
import { toast } from "sonner";

import { AuditHistory } from "@/components/common/audit-history";
import { ConfirmDialog } from "@/components/common/confirm-dialog";
import { DataPagination } from "@/components/common/data-pagination";
import { Field } from "@/components/common/field";
import { PageHeader } from "@/components/common/page-header";
import { EmptyState, QueryState } from "@/components/common/query-state";
import { OrderStatusBadge } from "@/components/common/status";
import { Button } from "@/components/ui/button";
import { Card, CardContent } from "@/components/ui/card";
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import { Tabs, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { errorMessage } from "@/lib/api";
import { useAuth } from "@/lib/auth";
import { formatDate, formatMoney } from "@/lib/format";
import type { OrderStatus } from "@/lib/types";
import { cn } from "@/lib/utils";
import { OrderSummary } from "@/pages/admin/order-summary";
import { useGetShippersQuery } from "@/services/catalog";
import { useCancelOrderMutation, useGetOrderQuery, useGetOrdersQuery, useShipOrderMutation } from "@/services/sales";

export function AdminOrdersPage() {
  const navigate = useNavigate();
  const [params, setParams] = useSearchParams();
  const status = (params.get("status") as OrderStatus | null) ?? undefined;
  const from = params.get("from") ?? "";
  const to = params.get("to") ?? "";
  const index = Number(params.get("page") ?? 0);

  const update = (changes: Record<string, string | undefined>) => {
    const next = new URLSearchParams(params);
    for (const [key, value] of Object.entries(changes)) {
      if (value) next.set(key, value);
      else next.delete(key);
    }
    if (!("page" in changes)) next.delete("page");
    setParams(next);
  };

  const orders = useGetOrdersQuery({ index, size: 15, status, from, to });

  return (
    <>
      <PageHeader title="Siparişler" description="Duruma ve tarih aralığına göre filtrelenebilir; sayfalama sunucuda." />
      <div className="flex flex-col gap-3 lg:flex-row lg:items-center lg:justify-between">
        <Tabs value={status ?? "all"} onValueChange={(v) => update({ status: v === "all" ? undefined : v })}>
          <TabsList>
            <TabsTrigger value="all">Tümü</TabsTrigger>
            <TabsTrigger value="Placed">Hazırlanıyor</TabsTrigger>
            <TabsTrigger value="Shipped">Kargoda</TabsTrigger>
            <TabsTrigger value="Cancelled">İptal</TabsTrigger>
          </TabsList>
        </Tabs>
        <div className="flex items-center gap-2">
          <Input type="date" className="w-40" value={from} onChange={(e) => update({ from: e.target.value || undefined })} aria-label="Başlangıç" />
          <span className="text-muted-foreground">–</span>
          <Input type="date" className="w-40" value={to} onChange={(e) => update({ to: e.target.value || undefined })} aria-label="Bitiş" />
        </div>
      </div>

      <QueryState isLoading={orders.isLoading} error={orders.error} />
      {orders.data?.count === 0 && <EmptyState title="Sipariş bulunamadı" description="Northwind siparişleri 1996–1998 arasındadır." />}
      {!!orders.data?.count && (
        <Card className={cn("py-2", orders.isFetching && "opacity-70")}>
          <CardContent className="px-2">
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>No</TableHead>
                  <TableHead>Müşteri</TableHead>
                  <TableHead>Tarih</TableHead>
                  <TableHead>Kargo</TableHead>
                  <TableHead>Durum</TableHead>
                  <TableHead className="text-right">Tutar</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {orders.data.items.map((o) => (
                  <TableRow key={o.id} className="cursor-pointer" onClick={() => navigate(`/admin/orders/${o.id}`)}>
                    <TableCell className="font-medium">#{o.number}</TableCell>
                    <TableCell className="max-w-56 truncate">{o.customerName}</TableCell>
                    <TableCell>{formatDate(o.orderedAt)}</TableCell>
                    <TableCell>{formatDate(o.shippedAt)}</TableCell>
                    <TableCell>
                      <OrderStatusBadge status={o.status} />
                    </TableCell>
                    <TableCell className="text-right">{formatMoney(o.total)}</TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </CardContent>
        </Card>
      )}
      <DataPagination page={orders.data} onPageChange={(i) => update({ page: i ? String(i) : undefined })} />
    </>
  );
}

export function AdminOrderDetailPage() {
  const { id = "" } = useParams();
  const { hasRole } = useAuth();
  const [shipOpen, setShipOpen] = useState(false);
  const [cancelOpen, setCancelOpen] = useState(false);

  // İptal/kargo; sipariş, stok, rapor, outbox ve değişiklik geçmişi etiketlerini geçersiz kılar (services/sales.ts).
  const order = useGetOrderQuery(id);
  const [cancelOrder] = useCancelOrderMutation();

  async function cancel() {
    try {
      await cancelOrder(id).unwrap();
      toast.success("Sipariş iptal edildi; OrderCancelled event'i ürünleri aynı transaction'da stoğa geri koydu.");
    } catch (e) {
      toast.error(errorMessage(e));
    }
  }

  const o = order.data;

  return (
    <>
      <Button asChild variant="ghost" size="sm" className="w-fit">
        <Link to="/admin/orders">
          <ArrowLeftIcon /> Siparişler
        </Link>
      </Button>
      <QueryState isLoading={order.isLoading} error={order.error} />
      {o && (
        <>
          <PageHeader
            title={`Sipariş #${o.number}`}
            description={
              <Link to={`/admin/customers/${o.customerId}`} className="hover:underline">
                {o.customerName}
              </Link>
            }
            actions={
              o.status === "Placed" && (
                <>
                  {hasRole("Warehouse", "Admin") && (
                    <Button onClick={() => setShipOpen(true)}>
                      <TruckIcon /> Kargoya ver
                    </Button>
                  )}
                  {hasRole("Sales", "Admin") && (
                    <Button variant="outline" onClick={() => setCancelOpen(true)}>
                      <BanIcon /> İptal et
                    </Button>
                  )}
                </>
              )
            }
          />
          <OrderSummary order={o} />
          {hasRole("Admin") && <AuditHistory entityType="Order" entityId={o.id} />}
          {shipOpen && <ShipDialog orderId={o.id} onClose={() => setShipOpen(false)} />}
          <ConfirmDialog
            open={cancelOpen}
            onOpenChange={setCancelOpen}
            title="Sipariş iptal edilsin mi?"
            description="Ürünler stoğa geri konur."
            confirmText="İptal et"
            destructive
            onConfirm={cancel}
          />
        </>
      )}
    </>
  );
}

function ShipDialog({ orderId, onClose }: { orderId: string; onClose: () => void }) {
  const shippers = useGetShippersQuery();
  const [shipperId, setShipperId] = useState<string>("");
  const [shipOrder, { isLoading: shipping }] = useShipOrderMutation();

  async function ship() {
    try {
      await shipOrder({ id: orderId, shipperId }).unwrap();
      toast.success("Kargoya verildi. OrderShipped event'i outbox'a yazıldı; bildirim e-postası birkaç saniye içinde gider.");
      onClose();
    } catch (e) {
      toast.error(errorMessage(e));
    }
  }

  return (
    <Dialog open onOpenChange={(open) => !open && onClose()}>
      <DialogContent className="sm:max-w-sm">
        <DialogHeader>
          <DialogTitle>Kargoya ver</DialogTitle>
          <DialogDescription>Kargo firmasını seç.</DialogDescription>
        </DialogHeader>
        <Field label="Kargo firması">
          <Select value={shipperId} onValueChange={setShipperId}>
            <SelectTrigger className="w-full">
              <SelectValue placeholder="Seç" />
            </SelectTrigger>
            <SelectContent>
              {shippers.data?.map((s) => (
                <SelectItem key={s.id} value={s.id}>
                  {s.companyName}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        </Field>
        <DialogFooter>
          <Button variant="outline" onClick={onClose}>
            Vazgeç
          </Button>
          <Button onClick={ship} disabled={!shipperId || shipping}>
            <TruckIcon /> Kargoya ver
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
