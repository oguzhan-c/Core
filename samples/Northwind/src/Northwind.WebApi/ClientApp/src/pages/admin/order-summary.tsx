import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Separator } from "@/components/ui/separator";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import { OrderStatusBadge } from "@/components/common/status";
import { formatAddress, formatDate, formatDateTime, formatMoney } from "@/lib/format";
import type { Order } from "@/lib/types";

/** Sipariş ayrıntısı: satırlar, tutarlar ve teslimat (hem site hem panel kullanır). */
export function OrderSummary({ order }: { order: Order }) {
  return (
    <div className="grid gap-4 lg:grid-cols-[1fr_320px]">
      <Card className="py-4">
        <CardHeader className="px-4">
          <CardTitle>Ürünler</CardTitle>
        </CardHeader>
        <CardContent className="px-2">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Ürün</TableHead>
                <TableHead className="text-right">Birim fiyat</TableHead>
                <TableHead className="text-right">Adet</TableHead>
                <TableHead className="text-right">İndirim</TableHead>
                <TableHead className="text-right">Tutar</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {order.lines.map((line) => (
                <TableRow key={line.productId}>
                  <TableCell className="font-medium">{line.productName}</TableCell>
                  <TableCell className="text-right">{formatMoney(line.unitPrice)}</TableCell>
                  <TableCell className="text-right">{line.quantity}</TableCell>
                  <TableCell className="text-right">{line.discount > 0 ? `%${Math.round(line.discount * 100)}` : "—"}</TableCell>
                  <TableCell className="text-right">{formatMoney(line.lineTotal)}</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
          <div className="ml-auto max-w-xs space-y-1 px-2 pt-4 text-sm">
            <div className="flex justify-between">
              <span className="text-muted-foreground">Ara toplam</span>
              <span>{formatMoney(order.subtotal)}</span>
            </div>
            <div className="flex justify-between">
              <span className="text-muted-foreground">Kargo</span>
              <span>{order.freight === 0 ? "Ücretsiz" : formatMoney(order.freight)}</span>
            </div>
            <Separator className="my-1" />
            <div className="flex justify-between font-semibold">
              <span>Toplam</span>
              <span>{formatMoney(order.total)}</span>
            </div>
          </div>
        </CardContent>
      </Card>

      <Card className="h-fit py-4">
        <CardContent className="space-y-3 px-4 text-sm">
          <div className="flex items-center justify-between">
            <span className="text-muted-foreground">Durum</span>
            <OrderStatusBadge status={order.status} />
          </div>
          <Info label="Müşteri" value={order.customerName} />
          <Info label="Sipariş tarihi" value={formatDateTime(order.orderedAt)} />
          {order.requiredDate && <Info label="İstenen tarih" value={formatDate(order.requiredDate)} />}
          {order.employeeName && <Info label="Satış temsilcisi" value={order.employeeName} />}
          <Separator />
          <Info label="Alıcı" value={order.shipName} />
          <Info label="Teslimat adresi" value={formatAddress(order.shipAddress)} />
          {order.shippedAt && (
            <>
              <Info label="Kargo firması" value={order.shipperName ?? "—"} />
              <Info label="Kargoya verildi" value={formatDateTime(order.shippedAt)} />
            </>
          )}
        </CardContent>
      </Card>
    </div>
  );
}

function Info({ label, value }: { label: string; value: string }) {
  return (
    <div>
      <p className="text-muted-foreground text-xs">{label}</p>
      <p>{value}</p>
    </div>
  );
}
