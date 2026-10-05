import { Link } from "react-router";
import { PackageIcon, ShoppingCartIcon } from "lucide-react";
import { toast } from "sonner";

import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardFooter } from "@/components/ui/card";
import { formatMoney } from "@/lib/format";
import { useStore } from "@/lib/store";
import type { StoreProduct } from "@/lib/types";

/** Ürün görseli olmadığından kategori adından türetilen renkli bir yer tutucu. */
export function ProductImage({ name, category, className = "h-36" }: { name: string; category?: string | null; className?: string }) {
  const hue = [...(category ?? name)].reduce((sum, c) => sum + c.charCodeAt(0), 0) % 360;
  return (
    <div
      className={`flex items-center justify-center rounded-t-xl ${className}`}
      style={{ background: `linear-gradient(135deg, oklch(0.93 0.05 ${hue}), oklch(0.85 0.08 ${(hue + 40) % 360}))` }}
    >
      <PackageIcon className="size-10 text-black/30" />
    </div>
  );
}

export function StockBadge({ stock }: { stock: number }) {
  if (stock === 0) return <Badge variant="destructive">Tükendi</Badge>;
  if (stock < 10) return <Badge variant="warning">Son {stock} adet</Badge>;
  return <Badge variant="success">Stokta</Badge>;
}

export function ProductCard({ product }: { product: StoreProduct }) {
  const { add } = useStore();

  function addToCart() {
    add({ productId: product.id, name: product.name, unitPrice: product.unitPrice, maxQuantity: product.unitsInStock });
    toast.success(`${product.name} sepete eklendi.`);
  }

  return (
    <Card className="gap-0 overflow-hidden py-0 transition-shadow hover:shadow-md">
      <Link to={`/products/${product.id}`}>
        <ProductImage name={product.name} category={product.categoryName} />
      </Link>
      <CardContent className="space-y-1 p-4">
        <p className="text-muted-foreground text-xs">{product.categoryName ?? "Kategorisiz"}</p>
        <Link to={`/products/${product.id}`} className="line-clamp-1 font-medium hover:underline">
          {product.name}
        </Link>
        <p className="text-muted-foreground line-clamp-1 text-xs">{product.quantityPerUnit}</p>
      </CardContent>
      <CardFooter className="flex items-center justify-between gap-2 px-4 pb-4">
        <div className="space-y-1">
          <div className="text-lg font-semibold">{formatMoney(product.unitPrice)}</div>
          <StockBadge stock={product.unitsInStock} />
        </div>
        <Button size="sm" onClick={addToCart} disabled={product.unitsInStock === 0}>
          <ShoppingCartIcon /> Ekle
        </Button>
      </CardFooter>
    </Card>
  );
}
