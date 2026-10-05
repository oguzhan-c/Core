import { useState } from "react";
import { Link, useNavigate, useParams } from "react-router";
import { keepPreviousData, useQuery } from "@tanstack/react-query";
import { ArrowLeftIcon, SearchIcon } from "lucide-react";

import { AuditHistory } from "@/components/common/audit-history";
import { DataPagination } from "@/components/common/data-pagination";
import { PageHeader } from "@/components/common/page-header";
import { EmptyState, QueryState } from "@/components/common/query-state";
import { OrderStatusBadge } from "@/components/common/status";
import { useDebounced } from "@/components/common/use-debounced";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import { api } from "@/lib/api";
import { useAuth } from "@/lib/auth";
import { formatAddress, formatDate, formatMoney } from "@/lib/format";
import type { Customer, CustomerListItem, OrderListItem, Paginate } from "@/lib/types";
import { cn } from "@/lib/utils";

export function AdminCustomersPage() {
  const navigate = useNavigate();
  const [index, setIndex] = useState(0);
  const [search, setSearch] = useState("");
  const [country, setCountry] = useState("");
  const debouncedSearch = useDebounced(search);
  const debouncedCountry = useDebounced(country);

  const customers = useQuery({
    queryKey: ["customers", { index, debouncedSearch, debouncedCountry }],
    queryFn: () =>
      api<Paginate<CustomerListItem>>("/api/customers", { query: { index, size: 15, search: debouncedSearch, country: debouncedCountry } }),
    placeholderData: keepPreviousData,
  });

  return (
    <>
      <PageHeader title="Müşteriler" description="Firma, kod ya da yetkili adına göre arama; ülke filtresi." />
      <div className="flex flex-col gap-3 sm:flex-row">
        <div className="relative flex-1">
          <SearchIcon className="text-muted-foreground absolute top-2.5 left-3 size-4" />
          <Input
            className="pl-9"
            placeholder="Firma, kod ya da yetkili…"
            value={search}
            onChange={(e) => {
              setSearch(e.target.value);
              setIndex(0);
            }}
          />
        </div>
        <Input
          className="sm:w-48"
          placeholder="Ülke (ör. Germany)"
          value={country}
          onChange={(e) => {
            setCountry(e.target.value);
            setIndex(0);
          }}
        />
      </div>

      <QueryState isLoading={customers.isLoading} error={customers.error} />
      {customers.data?.count === 0 && <EmptyState title="Müşteri bulunamadı" />}
      {!!customers.data?.count && (
        <Card className={cn("py-2", customers.isFetching && "opacity-70")}>
          <CardContent className="px-2">
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>Kod</TableHead>
                  <TableHead>Firma</TableHead>
                  <TableHead>Yetkili</TableHead>
                  <TableHead>Şehir</TableHead>
                  <TableHead>Ülke</TableHead>
                  <TableHead>Telefon</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {customers.data.items.map((c) => (
                  <TableRow key={c.id} className="cursor-pointer" onClick={() => navigate(`/admin/customers/${c.id}`)}>
                    <TableCell className="font-mono text-xs">{c.code}</TableCell>
                    <TableCell className="font-medium">{c.companyName}</TableCell>
                    <TableCell>{c.contactName}</TableCell>
                    <TableCell>{c.city}</TableCell>
                    <TableCell>{c.country}</TableCell>
                    <TableCell>{c.phone}</TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </CardContent>
        </Card>
      )}
      <DataPagination page={customers.data} onPageChange={setIndex} />
    </>
  );
}

export function AdminCustomerDetailPage() {
  const { id = "" } = useParams();
  const isAdmin = useAuth().hasRole("Admin");
  const [index, setIndex] = useState(0);

  const customer = useQuery({ queryKey: ["customers", "detail", id], queryFn: () => api<Customer>(`/api/customers/${id}`) });
  const orders = useQuery({
    queryKey: ["customers", id, "orders", index],
    queryFn: () => api<Paginate<OrderListItem>>(`/api/customers/${id}/orders`, { query: { index, size: 10 } }),
    placeholderData: keepPreviousData,
  });

  const c = customer.data;

  return (
    <>
      <Button asChild variant="ghost" size="sm" className="w-fit">
        <Link to="/admin/customers">
          <ArrowLeftIcon /> Müşteriler
        </Link>
      </Button>
      <QueryState isLoading={customer.isLoading} error={customer.error} />
      {c && (
        <>
          <PageHeader title={c.companyName} description={`Müşteri kodu ${c.code}`} />
          <div className="grid gap-4 lg:grid-cols-[320px_1fr]">
            <Card className="h-fit">
              <CardHeader>
                <CardTitle>Bilgiler</CardTitle>
              </CardHeader>
              <CardContent className="grid gap-3 text-sm">
                <Info label="Yetkili" value={[c.contactName, c.contactTitle].filter(Boolean).join(" · ") || "—"} />
                <Info label="Adres" value={formatAddress(c.address)} />
                <Info label="Telefon" value={c.phone ?? "—"} />
                <Info label="Faks" value={c.fax ?? "—"} />
              </CardContent>
            </Card>
            <Card>
              <CardHeader>
                <CardTitle>Siparişler</CardTitle>
              </CardHeader>
              <CardContent className="space-y-3 px-2">
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
                    {orders.data?.items.map((o) => (
                      <TableRow key={o.id}>
                        <TableCell>
                          <Link to={`/admin/orders/${o.id}`} className="font-medium hover:underline">
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
                <DataPagination page={orders.data} onPageChange={setIndex} />
              </CardContent>
            </Card>
          </div>
          {isAdmin && <AuditHistory entityType="Customer" entityId={c.id} />}
        </>
      )}
    </>
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
