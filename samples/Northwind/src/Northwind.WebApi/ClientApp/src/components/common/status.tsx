import { Badge } from "@/components/ui/badge";
import type { AuditAction, OrderStatus, OutboxStatus } from "@/lib/types";

const orderLabels: Record<OrderStatus, string> = { Placed: "Hazırlanıyor", Shipped: "Kargoda", Cancelled: "İptal" };

export function OrderStatusBadge({ status }: { status: OrderStatus }) {
  const variant = status === "Shipped" ? "success" : status === "Cancelled" ? "destructive" : "warning";
  return <Badge variant={variant}>{orderLabels[status]}</Badge>;
}

const outboxLabels: Record<OutboxStatus, string> = { Pending: "Bekliyor", Processed: "Yayınlandı", Failed: "Hatalı" };

export function OutboxStatusBadge({ status }: { status: OutboxStatus }) {
  const variant = status === "Processed" ? "success" : status === "Failed" ? "destructive" : "warning";
  return <Badge variant={variant}>{outboxLabels[status]}</Badge>;
}

const auditLabels: Record<AuditAction, string> = { Created: "Oluşturuldu", Updated: "Güncellendi", Deleted: "Silindi" };

export function AuditActionBadge({ action }: { action: AuditAction }) {
  const variant = action === "Created" ? "success" : action === "Deleted" ? "destructive" : "secondary";
  return <Badge variant={variant}>{auditLabels[action]}</Badge>;
}
