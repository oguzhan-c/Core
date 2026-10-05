import { useState } from "react";
import { Link, useParams } from "react-router";
import { useQuery } from "@tanstack/react-query";
import { ArrowLeftIcon, MinusIcon, PlusIcon, ShoppingCartIcon } from "lucide-react";
import { toast } from "sonner";

import { QueryState } from "@/components/common/query-state";
import { Button } from "@/components/ui/button";
import { Separator } from "@/components/ui/separator";
import { api } from "@/lib/api";
import { formatMoney } from "@/lib/format";
import { useStore } from "@/lib/store";
import type { StoreProduct } from "@/lib/types";
import { ProductImage, StockBadge } from "@/pages/site/product-card";

export function ProductDetailPage() {
  const { id } = useParams();
  const { tenant, add } = useStore();
  const [quantity, setQuantity] = useState(1);

  const product = useQuery({
    queryKey: ["store", tenant, "product", id],
    queryFn: () => api<StoreProduct>(`/api/store/${tenant}/products/${id}`),
  });

  const p = product.data;

  return (
    <div className="mx-auto max-w-5xl space-y-6 px-4 py-8">
      <Button asChild variant="ghost" size="sm">
        <Link to="/products">
          <ArrowLeftIcon /> Ürünler
        </Link>
      </Button>
      <QueryState isLoading={product.isLoading} error={product.error} />
      {p && (
        <div className="grid gap-8 md:grid-cols-2">
          <ProductImage name={p.name} category={p.categoryName} className="h-72 rounded-xl" />
          <div className="space-y-4">
            <div>
              <p className="text-primary text-sm">{p.categoryName}</p>
              <h1 className="text-3xl font-bold">{p.name}</h1>
              <p className="text-muted-foreground">{p.quantityPerUnit}</p>
            </div>
            <div className="flex items-center gap-3">
              <span className="text-3xl font-semibold">{formatMoney(p.unitPrice)}</span>
              <StockBadge stock={p.unitsInStock} />
            </div>
            <Separator />
            <dl className="grid grid-cols-2 gap-2 text-sm">
              <dt className="text-muted-foreground">Tedarikçi</dt>
              <dd>{p.supplierName ?? "—"}</dd>
              <dt className="text-muted-foreground">Stok</dt>
              <dd>{p.unitsInStock} adet</dd>
            </dl>
            <div className="flex items-center gap-3">
              <div className="flex items-center rounded-md border">
                <Button variant="ghost" size="icon-sm" onClick={() => setQuantity((q) => Math.max(1, q - 1))} aria-label="Azalt">
                  <MinusIcon />
                </Button>
                <span className="w-10 text-center text-sm">{quantity}</span>
                <Button
                  variant="ghost"
                  size="icon-sm"
                  onClick={() => setQuantity((q) => Math.min(p.unitsInStock, q + 1))}
                  aria-label="Artır"
                >
                  <PlusIcon />
                </Button>
              </div>
              <Button
                disabled={p.unitsInStock === 0}
                onClick={() => {
                  add({ productId: p.id, name: p.name, unitPrice: p.unitPrice, maxQuantity: p.unitsInStock }, quantity);
                  toast.success(`${quantity} × ${p.name} sepete eklendi.`);
                }}
              >
                <ShoppingCartIcon /> Sepete ekle
              </Button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
