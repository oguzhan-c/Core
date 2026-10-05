import { useState } from "react";
import { useSearchParams } from "react-router";
import { keepPreviousData, useQuery } from "@tanstack/react-query";
import { SearchIcon } from "lucide-react";

import { DataPagination } from "@/components/common/data-pagination";
import { EmptyState, QueryState } from "@/components/common/query-state";
import { useDebounced } from "@/components/common/use-debounced";
import { Input } from "@/components/ui/input";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { api } from "@/lib/api";
import { useStore } from "@/lib/store";
import type { Paginate, StoreCategory, StoreProduct, StoreProductSort } from "@/lib/types";
import { cn } from "@/lib/utils";
import { ProductCard } from "@/pages/site/product-card";

export function CatalogPage() {
  const { tenant } = useStore();
  const [params, setParams] = useSearchParams();
  const [search, setSearch] = useState(params.get("q") ?? "");
  const debouncedSearch = useDebounced(search);

  const category = params.get("category") ?? undefined;
  const sort = (params.get("sort") as StoreProductSort | null) ?? "Name";
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

  const categories = useQuery({
    queryKey: ["store", tenant, "categories"],
    queryFn: () => api<StoreCategory[]>(`/api/store/${tenant}/categories`),
  });

  const products = useQuery({
    queryKey: ["store", tenant, "products", { category, sort, index, q: debouncedSearch }],
    queryFn: () =>
      api<Paginate<StoreProduct>>(`/api/store/${tenant}/products`, {
        query: { categoryId: category, sort, index, size: 12, search: debouncedSearch },
      }),
    placeholderData: keepPreviousData,
  });

  return (
    <div className="mx-auto grid max-w-7xl gap-8 px-4 py-8 md:grid-cols-[220px_1fr]">
      <aside className="space-y-1">
        <p className="text-muted-foreground mb-2 text-xs font-medium tracking-wide uppercase">Kategoriler</p>
        <button
          className={cn("w-full rounded-md px-2 py-1.5 text-left text-sm", !category ? "bg-accent font-medium" : "hover:bg-accent/60")}
          onClick={() => update({ category: undefined })}
        >
          Tümü
        </button>
        {categories.data?.map((c) => (
          <button
            key={c.id}
            className={cn(
              "flex w-full items-center justify-between rounded-md px-2 py-1.5 text-left text-sm",
              category === c.id ? "bg-accent font-medium" : "hover:bg-accent/60"
            )}
            onClick={() => update({ category: c.id })}
          >
            {c.name}
            <span className="text-muted-foreground text-xs">{c.productCount}</span>
          </button>
        ))}
      </aside>

      <div className="space-y-5">
        <div className="flex flex-col gap-3 sm:flex-row">
          <div className="relative flex-1">
            <SearchIcon className="text-muted-foreground absolute top-2.5 left-3 size-4" />
            <Input
              className="pl-9"
              placeholder="Ürün ara…"
              value={search}
              onChange={(e) => {
                setSearch(e.target.value);
                update({ q: e.target.value || undefined });
              }}
            />
          </div>
          <Select value={sort} onValueChange={(value) => update({ sort: value === "Name" ? undefined : value })}>
            <SelectTrigger className="w-full sm:w-48">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              <SelectItem value="Name">Ada göre</SelectItem>
              <SelectItem value="PriceAsc">Fiyat: artan</SelectItem>
              <SelectItem value="PriceDesc">Fiyat: azalan</SelectItem>
            </SelectContent>
          </Select>
        </div>

        <QueryState isLoading={products.isLoading} error={products.error} rows={4} />
        {products.data?.count === 0 && <EmptyState title="Ürün bulunamadı" description="Aramayı ya da kategoriyi değiştir." />}

        <div className={cn("grid grid-cols-1 gap-4 sm:grid-cols-2 xl:grid-cols-3", products.isFetching && "opacity-70")}>
          {products.data?.items.map((p) => (
            <ProductCard key={p.id} product={p} />
          ))}
        </div>

        <DataPagination page={products.data} onPageChange={(i) => update({ page: i ? String(i) : undefined })} />
      </div>
    </div>
  );
}
