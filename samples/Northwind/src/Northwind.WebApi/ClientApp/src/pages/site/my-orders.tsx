import { useState } from "react";
import { Link, useParams } from "react-router";
import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { ArrowLeftIcon } from "lucide-react";
import { toast } from "sonner";

import { ConfirmDialog } from "@/components/common/confirm-dialog";
import { DataPagination } from "@/components/common/data-pagination";
import { PageHeader } from "@/components/common/page-header";
import { EmptyState, QueryState } from "@/components/common/query-state";
import { OrderStatusBadge } from "@/components/common/status";
import { OrderSummary } from "@/pages/admin/order-summary";
import { Button } from "@/components/ui/button";
import { Card, CardContent } from "@/components/ui/card";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import { api, errorMessage } from "@/lib/api";
import { formatDate, formatMoney } from "@/lib/format";
import type { Order, OrderListItem, Paginate } from "@/lib/types";

export function MyOrdersPage() {
  const [index, setIndex] = useState(0);
  const orders = useQuery({
    queryKey: ["my-orders", index],
    queryFn: () => api<Paginate<OrderListItem>>("/api/store/my/orders", { query: { index, size: 10 } }),
    placeholderData: keepPreviousData,
  });

  return (
    <div className="mx-auto max-w-5xl space-y-6 px-4 py-8">
      <PageHeader title="Siparişlerim" description="Siparişlerinin durumu; kargoya verilen siparişler için bildirim e-postası gönderilir." />
      <QueryState isLoading={orders.isLoading} error={orders.error} />
      {orders.data?.count === 0 && (
        <EmptyState
          title="Henüz siparişin yok"
          action={
            <Button asChild className="mt-2">
              <Link to="/products">Alışverişe başla</Link>
            </Button>
          }
        />
      )}
      {!!orders.data?.count && (
        <Card className="py-2">
          <CardContent className="px-2">
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>No</TableHead>
                  <TableHead>Tarih</TableHead>
                  <TableHead>Durum</TableHead>
                  <TableHead className="text-right">Tutar</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {orders.data.items.map((o) => (
                  <TableRow key={o.id}>
                    <TableCell>
                      <Link to={`/account/orders/${o.id}`} className="font-medium hover:underline">
                        #{o.number}
                      </Link>
                    </TableCell>
                    <TableCell>{formatDate(o.orderedAt)}</TableCell>
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
      <DataPagination page={orders.data} onPageChange={setIndex} />
    </div>
  );
}

export function MyOrderDetailPage() {
  const { id } = useParams();
  const queryClient = useQueryClient();
  const [confirmOpen, setConfirmOpen] = useState(false);

  const order = useQuery({ queryKey: ["my-orders", "detail", id], queryFn: () => api<Order>(`/api/store/my/orders/${id}`) });

  const cancel = useMutation({
    mutationFn: () => api(`/api/store/my/orders/${id}/cancel`, { method: "POST" }),
    onSuccess: () => {
      toast.success("Sipariş iptal edildi; ürünler stoğa geri kondu.");
      queryClient.invalidateQueries({ queryKey: ["my-orders"] });
      queryClient.invalidateQueries({ queryKey: ["store"] });
    },
    onError: (e) => toast.error(errorMessage(e)),
  });

  return (
    <div className="mx-auto max-w-5xl space-y-6 px-4 py-8">
      <Button asChild variant="ghost" size="sm">
        <Link to="/account/orders">
          <ArrowLeftIcon /> Siparişlerim
        </Link>
      </Button>
      <QueryState isLoading={order.isLoading} error={order.error} />
      {order.data && (
        <>
          <PageHeader
            title={`Sipariş #${order.data.number}`}
            description={formatDate(order.data.orderedAt)}
            actions={
              order.data.status === "Placed" && (
                <Button variant="outline" onClick={() => setConfirmOpen(true)}>
                  Siparişi iptal et
                </Button>
              )
            }
          />
          <OrderSummary order={order.data} />
          <ConfirmDialog
            open={confirmOpen}
            onOpenChange={setConfirmOpen}
            title="Sipariş iptal edilsin mi?"
            description="Ürünler stoğa geri konur. Bu işlem geri alınamaz."
            confirmText="İptal et"
            destructive
            onConfirm={() => cancel.mutateAsync()}
          />
        </>
      )}
    </div>
  );
}

