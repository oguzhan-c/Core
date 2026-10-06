import { Link } from "react-router";
import { ArrowRightIcon, MailCheckIcon, RadioTowerIcon, ShieldCheckIcon, TruckIcon } from "lucide-react";

import { QueryState } from "@/components/common/query-state";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { useAuth } from "@/lib/auth";
import { FREE_SHIPPING_THRESHOLD, useStore } from "@/lib/store";
import { formatMoney } from "@/lib/format";
import { ProductCard } from "@/pages/site/product-card";
import { useGetStoreCategoriesQuery, useGetStoreProductsQuery, useGetStoreTenantsQuery } from "@/services/storefront";

export function HomePage() {
  const { tenant } = useStore();
  const { user } = useAuth();

  const tenants = useGetStoreTenantsQuery();
  const categories = useGetStoreCategoriesQuery(tenant);
  const featured = useGetStoreProductsQuery({ tenant, size: 8, sort: "PriceDesc" });

  const tenantName = tenants.data?.find((t) => t.identifier === tenant)?.name ?? tenant;

  return (
    <div>
      <section className="from-primary/10 via-background to-background border-b bg-gradient-to-b">
        <div className="mx-auto grid max-w-7xl gap-8 px-4 py-16 md:grid-cols-[1.4fr_1fr] md:items-center">
          <div className="space-y-5">
            <p className="text-primary text-sm font-medium">{tenantName}</p>
            <h1 className="text-4xl font-bold tracking-tight md:text-5xl">Dünyanın dört bir yanından gurme ürünler</h1>
            <p className="text-muted-foreground max-w-xl text-lg">
              Peynirden deniz ürünlerine, baharattan içeceklere — {formatMoney(FREE_SHIPPING_THRESHOLD)} üzeri siparişlerde kargo ücretsiz.
            </p>
            <div className="flex flex-wrap gap-3">
              <Button asChild size="lg">
                <Link to="/products">
                  Ürünleri keşfet <ArrowRightIcon />
                </Link>
              </Button>
              {!user && (
                <Button asChild size="lg" variant="outline">
                  <Link to="/register">Hesap oluştur</Link>
                </Button>
              )}
            </div>
          </div>
          <div className="grid grid-cols-2 gap-3">
            {[
              { icon: ShieldCheckIcon, title: "Güvenli oturum", text: "JWT yalnızca HttpOnly cookie'de" },
              { icon: MailCheckIcon, title: "E-posta doğrulama", text: "6 haneli kod ile kayıt" },
              { icon: RadioTowerIcon, title: "Kaybolmayan bildirim", text: "Outbox ile kargo e-postası" },
              { icon: TruckIcon, title: "Canlı stok", text: "Sipariş anında stoktan düşer" },
            ].map((f) => (
              <Card key={f.title} className="gap-2 py-4">
                <CardHeader className="px-4">
                  <f.icon className="text-primary size-5" />
                  <CardTitle className="text-sm">{f.title}</CardTitle>
                  <CardDescription className="text-xs">{f.text}</CardDescription>
                </CardHeader>
              </Card>
            ))}
          </div>
        </div>
      </section>

      <section className="mx-auto max-w-7xl space-y-4 px-4 py-12">
        <h2 className="text-xl font-semibold">Kategoriler</h2>
        <QueryState isLoading={categories.isLoading} error={categories.error} rows={2} />
        {categories.data?.length === 0 && <p className="text-muted-foreground text-sm">Bu mağazada henüz ürün yok.</p>}
        <div className="grid grid-cols-2 gap-3 sm:grid-cols-4">
          {categories.data?.map((c) => (
            <Link key={c.id} to={`/products?category=${c.id}`}>
              <Card className="hover:border-primary h-full gap-1 py-4 transition-colors">
                <CardContent className="px-4">
                  <p className="font-medium">{c.name}</p>
                  <p className="text-muted-foreground line-clamp-2 text-xs">{c.description}</p>
                  <p className="text-primary mt-2 text-xs">{c.productCount} ürün</p>
                </CardContent>
              </Card>
            </Link>
          ))}
        </div>
      </section>

      <section className="mx-auto max-w-7xl space-y-4 px-4 pb-16">
        <div className="flex items-center justify-between">
          <h2 className="text-xl font-semibold">Öne çıkanlar</h2>
          <Button asChild variant="link">
            <Link to="/products">Tümü</Link>
          </Button>
        </div>
        <QueryState isLoading={featured.isLoading} error={featured.error} rows={2} />
        <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 lg:grid-cols-4">
          {featured.data?.items.map((p) => (
            <ProductCard key={p.id} product={p} />
          ))}
        </div>
      </section>
    </div>
  );
}
