import { useMemo, useState, type ReactNode } from "react";
import { useMutation, useQuery } from "@tanstack/react-query";
import { BookOpenIcon, PlayIcon, RotateCcwIcon } from "lucide-react";

import { cleanFilter, emptyGroup, FilterBuilder, type FieldOption } from "@/components/common/filter-builder";
import { DataPagination } from "@/components/common/data-pagination";
import { PageHeader } from "@/components/common/page-header";
import { EmptyState } from "@/components/common/query-state";
import { OrderStatusBadge } from "@/components/common/status";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import { Tabs, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { api, errorMessage } from "@/lib/api";
import { formatDate, formatMoney } from "@/lib/format";
import type { CustomerListItem, DynamicQuery, Filter, OrderListItem, Paginate, Product, SearchExamples, Sort } from "@/lib/types";

type Entity = "products" | "customers" | "orders";

interface Column<T> {
  header: string;
  cell: (row: T) => ReactNode;
  className?: string;
}

interface EntityConfig<T> {
  label: string;
  endpoint: string;
  fields: FieldOption[];
  columns: Column<T>[];
}

const products: EntityConfig<Product> = {
  label: "Ürünler",
  endpoint: "/api/products/search",
  fields: [
    { value: "name", label: "Ad", type: "string" },
    { value: "unitPrice", label: "Fiyat", type: "number" },
    { value: "unitsInStock", label: "Stok", type: "number" },
    { value: "unitsOnOrder", label: "Yoldaki", type: "number" },
    { value: "reorderLevel", label: "Sipariş seviyesi", type: "number" },
    { value: "isDiscontinued", label: "Satıştan kalktı", type: "boolean" },
    { value: "quantityPerUnit", label: "Satış birimi", type: "string" },
    { value: "createdAt", label: "Oluşturma", type: "date" },
  ],
  columns: [
    { header: "Ad", cell: (p) => <span className="font-medium">{p.name}</span> },
    { header: "Birim", cell: (p) => p.quantityPerUnit },
    { header: "Fiyat", cell: (p) => formatMoney(p.unitPrice), className: "text-right" },
    { header: "Stok", cell: (p) => p.unitsInStock, className: "text-right" },
    { header: "Durum", cell: (p) => (p.isDiscontinued ? <Badge variant="secondary">Kalktı</Badge> : <Badge variant="success">Satışta</Badge>) },
  ],
};

const customers: EntityConfig<CustomerListItem> = {
  label: "Müşteriler",
  endpoint: "/api/customers/search",
  fields: [
    { value: "companyName", label: "Firma", type: "string" },
    { value: "code", label: "Kod", type: "string" },
    { value: "contactName", label: "Yetkili", type: "string" },
    { value: "contactTitle", label: "Yetkili unvanı", type: "string" },
    { value: "address.city", label: "Şehir (address.city)", type: "string" },
    { value: "address.country", label: "Ülke (address.country)", type: "string" },
    { value: "phone", label: "Telefon", type: "string" },
    { value: "fax", label: "Faks", type: "string" },
  ],
  columns: [
    { header: "Kod", cell: (c) => <span className="font-mono text-xs">{c.code}</span> },
    { header: "Firma", cell: (c) => <span className="font-medium">{c.companyName}</span> },
    { header: "Yetkili", cell: (c) => c.contactName },
    { header: "Şehir", cell: (c) => c.city },
    { header: "Ülke", cell: (c) => c.country },
  ],
};

const orders: EntityConfig<OrderListItem> = {
  label: "Siparişler",
  endpoint: "/api/orders/search",
  fields: [
    { value: "number", label: "Numara", type: "number" },
    { value: "status", label: "Durum", type: "enum", options: ["Placed", "Shipped", "Cancelled"] },
    { value: "orderedAt", label: "Sipariş tarihi", type: "date" },
    { value: "shippedAt", label: "Kargo tarihi", type: "date" },
    { value: "freight", label: "Kargo ücreti", type: "number" },
    { value: "shipName", label: "Alıcı", type: "string" },
    { value: "shipAddress.city", label: "Teslimat şehri", type: "string" },
    { value: "shipAddress.country", label: "Teslimat ülkesi", type: "string" },
  ],
  columns: [
    { header: "No", cell: (o) => <span className="font-medium">#{o.number}</span> },
    { header: "Müşteri", cell: (o) => o.customerName, className: "max-w-56 truncate" },
    { header: "Tarih", cell: (o) => formatDate(o.orderedAt) },
    { header: "Durum", cell: (o) => <OrderStatusBadge status={o.status} /> },
    { header: "Tutar", cell: (o) => formatMoney(o.total), className: "text-right" },
  ],
};

const entities: Record<Entity, EntityConfig<any>> = { products, customers, orders };

/** Backend'in dinamik sorgu (DynamicQuery) desteğini görsel olarak denemek için. */
export function FilterLabPage() {
  const [entity, setEntity] = useState<Entity>("products");
  const [filter, setFilter] = useState<Filter>(emptyGroup());
  const [sort, setSort] = useState<Sort>({ field: "name", dir: "asc" });
  const [index, setIndex] = useState(0);

  const config = entities[entity];
  const examples = useQuery({ queryKey: ["search-examples"], queryFn: () => api<SearchExamples>("/api/search/examples"), staleTime: Infinity });

  const body = useMemo<DynamicQuery>(
    () => ({ filter: cleanFilter(filter, config.fields), sort: sort.field ? [sort] : null }),
    [filter, sort, config.fields]
  );

  const search = useMutation({
    mutationFn: (page: number) => api<Paginate<unknown>>(config.endpoint, { method: "POST", body, query: { index: page, size: 10 } }),
  });

  function selectEntity(next: Entity) {
    setEntity(next);
    setFilter(emptyGroup());
    setSort({ field: entities[next].fields[0].value, dir: "asc" });
    setIndex(0);
    search.reset();
  }

  function loadExample(example: SearchExamples["examples"][number]) {
    const target = (Object.keys(entities) as Entity[]).find((e) => example.endpoint.includes(`/${e}/`)) ?? "products";
    setEntity(target);
    const root = example.body.filter;
    setFilter(!root ? emptyGroup() : root.field ? { logic: "and", filters: [root] } : root);
    setSort(example.body.sort?.[0] ?? { field: entities[target].fields[0].value, dir: "asc" });
    setIndex(0);
    search.reset();
  }

  const run = (page = 0) => {
    setIndex(page);
    search.mutate(page);
  };

  return (
    <>
      <PageHeader
        title="Filtre laboratuvarı"
        description="İç içe VE/VEYA gruplarıyla filtre kur, sunucuya DynamicQuery olarak gönder. Alanlar gerçek property'lere karşı doğrulanır, değerler SQL parametresi olur."
        actions={
          <DropdownMenu>
            <DropdownMenuTrigger asChild>
              <Button variant="outline">
                <BookOpenIcon /> Örnekler
              </Button>
            </DropdownMenuTrigger>
            <DropdownMenuContent align="end" className="w-96">
              <DropdownMenuLabel>GET /api/search/examples</DropdownMenuLabel>
              {examples.data?.examples.map((ex) => (
                <DropdownMenuItem key={ex.title} onSelect={() => loadExample(ex)} className="flex-col items-start gap-0">
                  <span>{ex.title}</span>
                  <span className="text-muted-foreground text-xs">{ex.endpoint}</span>
                </DropdownMenuItem>
              ))}
            </DropdownMenuContent>
          </DropdownMenu>
        }
      />

      <Tabs value={entity} onValueChange={(v) => selectEntity(v as Entity)}>
        <TabsList>
          {(Object.keys(entities) as Entity[]).map((e) => (
            <TabsTrigger key={e} value={e}>
              {entities[e].label}
            </TabsTrigger>
          ))}
        </TabsList>
      </Tabs>

      <div className="grid gap-4 xl:grid-cols-[1fr_380px]">
        <Card>
          <CardHeader>
            <CardTitle>Filtre</CardTitle>
            <CardDescription>"Grup" ile parantez aç: ör. satışta olan VE (adında sauce geçen VEYA (fiyatı 10-30 VE stoğu 20'den az)).</CardDescription>
          </CardHeader>
          <CardContent className="space-y-4">
            <FilterBuilder value={filter} onChange={setFilter} fields={config.fields} />
            <div className="flex flex-wrap items-center gap-2">
              <span className="text-sm">Sırala:</span>
              <Select value={sort.field} onValueChange={(field) => setSort({ ...sort, field })}>
                <SelectTrigger size="sm" className="w-48">
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  {config.fields.map((f) => (
                    <SelectItem key={f.value} value={f.value}>
                      {f.label}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
              <Select value={sort.dir} onValueChange={(dir) => setSort({ ...sort, dir: dir as Sort["dir"] })}>
                <SelectTrigger size="sm" className="w-32">
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem value="asc">Artan</SelectItem>
                  <SelectItem value="desc">Azalan</SelectItem>
                </SelectContent>
              </Select>
              <div className="ml-auto flex gap-2">
                <Button variant="outline" onClick={() => selectEntity(entity)}>
                  <RotateCcwIcon /> Temizle
                </Button>
                <Button onClick={() => run(0)} disabled={search.isPending}>
                  <PlayIcon /> Çalıştır
                </Button>
              </div>
            </div>
          </CardContent>
        </Card>

        <Card>
          <CardHeader>
            <CardTitle>İstek</CardTitle>
            <CardDescription>
              <code>POST {config.endpoint}?index={index}&amp;size=10</code>
            </CardDescription>
          </CardHeader>
          <CardContent>
            <pre className="bg-muted max-h-96 overflow-auto rounded-md p-3 text-xs">{JSON.stringify(body, null, 2)}</pre>
          </CardContent>
        </Card>
      </div>

      {search.error && (
        <Alert variant="destructive">
          <AlertDescription>{errorMessage(search.error)}</AlertDescription>
        </Alert>
      )}

      {search.data && (
        <Card className="py-2">
          <CardContent className="space-y-3 px-2">
            {search.data.count === 0 ? (
              <EmptyState title="Eşleşen kayıt yok" />
            ) : (
              <Table>
                <TableHeader>
                  <TableRow>
                    {config.columns.map((c) => (
                      <TableHead key={c.header} className={c.className}>
                        {c.header}
                      </TableHead>
                    ))}
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {search.data.items.map((row, i) => (
                    <TableRow key={i}>
                      {config.columns.map((c) => (
                        <TableCell key={c.header} className={c.className}>
                          {c.cell(row)}
                        </TableCell>
                      ))}
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            )}
            <DataPagination page={search.data} onPageChange={run} />
          </CardContent>
        </Card>
      )}
    </>
  );
}
