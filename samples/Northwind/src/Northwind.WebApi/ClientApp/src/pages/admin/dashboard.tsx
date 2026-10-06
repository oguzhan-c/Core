import type * as React from "react";
import { Link } from "react-router";
import { AlertTriangleIcon, BoxesIcon, DollarSignIcon, ReceiptIcon, SendIcon, UsersIcon } from "lucide-react";
import { Bar, BarChart, CartesianGrid, ResponsiveContainer, Tooltip, XAxis, YAxis } from "recharts";
import { toast } from "sonner";

import { PageHeader } from "@/components/common/page-header";
import { QueryState } from "@/components/common/query-state";
import { OrderStatusBadge } from "@/components/common/status";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import { errorMessage } from "@/lib/api";
import { useAuth } from "@/lib/auth";
import { formatDate, formatMoney, formatNumber } from "@/lib/format";
import { useGetDashboardQuery, useRunReorderReportMutation } from "@/services/admin";
import { useGetProductsToReorderQuery } from "@/services/catalog";
import { useGetSalesByCategoryQuery, useGetTopCustomersQuery } from "@/services/sales";

export function AdminDashboardPage() {
  const { hasRole } = useAuth();
  const canSeeReports = hasRole("Sales", "Admin");

  const dashboard = useGetDashboardQuery();
  // Raporlar yalnızca satış/yönetici için: yetkisi yoksa istek hiç gönderilmez (skip).
  const sales = useGetSalesByCategoryQuery(undefined, { skip: !canSeeReports });
  const top = useGetTopCustomersQuery({ count: 5 }, { skip: !canSeeReports });
  const reorder = useGetProductsToReorderQuery();

  const [runReorderReport, { isLoading: reportQueued }] = useRunReorderReportMutation();

  async function runReport() {
    try {
      await runReorderReport().unwrap();
      toast.success("Rapor kuyruğa alındı; arka planda hazırlanıp e-postayla gönderilecek.");
    } catch (e) {
      toast.error(errorMessage(e));
    }
  }

  const d = dashboard.data;

  return (
    <>
      <PageHeader title="Özet" description="Mağazanın genel durumu." />
      <QueryState isLoading={dashboard.isLoading} error={dashboard.error} rows={2} />

      {d && (
        <div className="grid gap-4 sm:grid-cols-2 xl:grid-cols-4">
          <Stat icon={DollarSignIcon} label="Toplam ciro" value={formatMoney(d.totalRevenue)} hint="İptal edilenler hariç" />
          <Stat icon={ReceiptIcon} label="Açık sipariş" value={formatNumber(d.openOrderCount)} hint="Kargoya verilmeyi bekliyor" to="/admin/orders?status=Placed" />
          <Stat icon={UsersIcon} label="Müşteri" value={formatNumber(d.customerCount)} to="/admin/customers" />
          <Stat
            icon={BoxesIcon}
            label="Satıştaki ürün"
            value={formatNumber(d.productCount)}
            hint={`${d.productsToReorder} ürün yeniden sipariş bekliyor`}
            to="/admin/products"
          />
        </div>
      )}

      <div className="grid gap-4 lg:grid-cols-[1.5fr_1fr]">
        {hasRole("Sales", "Admin") && (
          <Card>
            <CardHeader>
              <CardTitle>Kategorilere göre satış</CardTitle>
              <CardDescription>Rapor 5 dakika önbellekte tutulur; sipariş verilince temizlenir.</CardDescription>
            </CardHeader>
            <CardContent className="h-72">
              {sales.data && (
                <ResponsiveContainer width="100%" height="100%">
                  <BarChart data={sales.data} margin={{ left: 8, right: 8 }}>
                    <CartesianGrid vertical={false} strokeDasharray="3 3" className="stroke-border" />
                    <XAxis dataKey="category" tickLine={false} axisLine={false} fontSize={11} interval={0} angle={-20} textAnchor="end" height={50} />
                    <YAxis tickLine={false} axisLine={false} fontSize={11} tickFormatter={(v: number) => `${Math.round(v / 1000)}K`} />
                    <Tooltip formatter={(v) => formatMoney(Number(v))} cursor={{ fill: "var(--accent)" }} />
                    <Bar dataKey="revenue" name="Ciro" fill="var(--chart-1)" radius={[4, 4, 0, 0]} />
                  </BarChart>
                </ResponsiveContainer>
              )}
            </CardContent>
          </Card>
        )}

        {hasRole("Sales", "Admin") && (
          <Card>
            <CardHeader>
              <CardTitle>En iyi müşteriler</CardTitle>
            </CardHeader>
            <CardContent className="space-y-3">
              {top.data?.map((c, i) => (
                <Link key={c.customerId} to={`/admin/customers/${c.customerId}`} className="hover:bg-accent flex items-center gap-3 rounded-md p-1">
                  <span className="bg-primary/10 text-primary flex size-7 items-center justify-center rounded-full text-xs font-semibold">{i + 1}</span>
                  <div className="min-w-0 flex-1">
                    <p className="truncate text-sm font-medium">{c.companyName}</p>
                    <p className="text-muted-foreground text-xs">
                      {c.country} · {c.orders} sipariş
                    </p>
                  </div>
                  <span className="text-sm font-medium">{formatMoney(c.revenue)}</span>
                </Link>
              ))}
            </CardContent>
          </Card>
        )}
      </div>

      <div className="grid gap-4 lg:grid-cols-2">
        <Card>
          <CardHeader>
            <CardTitle>Son siparişler</CardTitle>
          </CardHeader>
          <CardContent className="px-2">
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>No</TableHead>
                  <TableHead>Müşteri</TableHead>
                  <TableHead>Durum</TableHead>
                  <TableHead className="text-right">Tutar</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {d?.recentOrders.map((o) => (
                  <TableRow key={o.id}>
                    <TableCell>
                      <Link to={`/admin/orders/${o.id}`} className="font-medium hover:underline">
                        #{o.number}
                      </Link>
                      <div className="text-muted-foreground text-xs">{formatDate(o.orderedAt)}</div>
                    </TableCell>
                    <TableCell className="max-w-40 truncate">{o.customerName}</TableCell>
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

        <Card>
          <CardHeader>
            <CardTitle className="flex items-center gap-2">
              <AlertTriangleIcon className="size-4 text-amber-500" /> Yeniden sipariş verilmeli
            </CardTitle>
            <CardDescription>Stok + yoldaki miktar, yeniden sipariş seviyesinin altında.</CardDescription>
          </CardHeader>
          <CardContent className="space-y-2">
            {reorder.data?.slice(0, 8).map((p) => (
              <div key={p.id} className="flex items-center justify-between text-sm">
                <span className="truncate">{p.name}</span>
                <Badge variant={p.unitsInStock === 0 ? "destructive" : "warning"}>
                  stok {p.unitsInStock} / seviye {p.reorderLevel}
                </Badge>
              </div>
            ))}
            {reorder.data?.length === 0 && <p className="text-muted-foreground text-sm">Her şey yolunda.</p>}
            {hasRole("Admin") && (
              <Button variant="outline" size="sm" className="mt-2" disabled={reportQueued} onClick={runReport}>
                <SendIcon /> Raporu şimdi e-postayla gönder
              </Button>
            )}
          </CardContent>
        </Card>
      </div>
    </>
  );
}

function Stat({
  icon: Icon,
  label,
  value,
  hint,
  to,
}: {
  icon: React.ComponentType<{ className?: string }>;
  label: string;
  value: string;
  hint?: string;
  to?: string;
}) {
  const content = (
    <Card className="hover:border-primary/50 h-full gap-2 py-5 transition-colors">
      <CardHeader className="flex flex-row items-center justify-between px-5">
        <CardDescription>{label}</CardDescription>
        <Icon className="text-muted-foreground size-4" />
      </CardHeader>
      <CardContent className="px-5">
        <div className="text-2xl font-semibold">{value}</div>
        {hint && <p className="text-muted-foreground text-xs">{hint}</p>}
      </CardContent>
    </Card>
  );
  return to ? <Link to={to}>{content}</Link> : content;
}
