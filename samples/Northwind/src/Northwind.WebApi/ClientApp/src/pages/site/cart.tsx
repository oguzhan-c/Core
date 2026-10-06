import { useState } from "react";
import { Link, useNavigate } from "react-router";
import { MinusIcon, PlusIcon, ShoppingBagIcon, Trash2Icon } from "lucide-react";
import { toast } from "sonner";

import { Field } from "@/components/common/field";
import { EmptyState } from "@/components/common/query-state";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardFooter, CardHeader, CardTitle } from "@/components/ui/card";
import { Checkbox } from "@/components/ui/checkbox";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Separator } from "@/components/ui/separator";
import { errorMessage, isApiError } from "@/lib/api";
import { useAuth } from "@/lib/auth";
import { formatMoney } from "@/lib/format";
import { FREE_SHIPPING_THRESHOLD, shippingFor, useStore } from "@/lib/store";
import type { Address } from "@/lib/types";
import { useCheckoutMutation } from "@/services/storefront";

const emptyAddress: Address = { street: "", city: "", region: "", postalCode: "", country: "" };

export function CartPage() {
  const { items, subtotal, setQuantity, remove, clear } = useStore();
  const { user, isCustomer } = useAuth();
  const navigate = useNavigate();

  const [useSavedAddress, setUseSavedAddress] = useState(true);
  const [shipName, setShipName] = useState("");
  const [address, setAddress] = useState<Address>(emptyAddress);
  const [error, setError] = useState<string | null>(null);

  const shipping = shippingFor(subtotal);

  // Başarılı siparişte stoklar ve sipariş listeleri etiketlerle (invalidatesTags) yenilenir.
  const [checkout, { isLoading: checkingOut }] = useCheckoutMutation();

  async function placeOrder() {
    setError(null);
    try {
      const result = await checkout({
        lines: items.map((i) => ({ productId: i.productId, quantity: i.quantity })),
        shipTo: useSavedAddress ? null : { name: shipName || `${user?.firstName} ${user?.lastName}`, address },
      }).unwrap();

      clear();
      toast.success(`#${result.number} numaralı siparişin alındı.`);
      navigate(`/account/orders/${result.id}`);
    } catch (e) {
      // Kayıtlı adres yoksa teslimat formunu aç.
      if (isApiError(e) && e.code === "address_required") setUseSavedAddress(false);
      setError(errorMessage(e));
    }
  }

  if (items.length === 0) {
    return (
      <div className="mx-auto max-w-3xl px-4 py-16">
        <EmptyState
          title="Sepetin boş"
          description="Ürünleri keşfet ve beğendiklerini sepete ekle."
          action={
            <Button asChild className="mt-2">
              <Link to="/products">Ürünlere git</Link>
            </Button>
          }
        />
      </div>
    );
  }

  return (
    <div className="mx-auto grid max-w-6xl gap-6 px-4 py-8 lg:grid-cols-[1fr_380px]">
      <Card>
        <CardHeader>
          <CardTitle>Sepet</CardTitle>
          <CardDescription>Fiyatlar sipariş anında sunucuda yeniden hesaplanır.</CardDescription>
        </CardHeader>
        <CardContent className="divide-y">
          {items.map((item) => (
            <div key={item.productId} className="flex flex-wrap items-center gap-3 py-3">
              <div className="min-w-40 flex-1">
                <Link to={`/products/${item.productId}`} className="font-medium hover:underline">
                  {item.name}
                </Link>
                <p className="text-muted-foreground text-sm">{formatMoney(item.unitPrice)}</p>
              </div>
              <div className="flex items-center rounded-md border">
                <Button variant="ghost" size="icon-sm" onClick={() => setQuantity(item.productId, item.quantity - 1)} aria-label="Azalt">
                  <MinusIcon />
                </Button>
                <span className="w-10 text-center text-sm">{item.quantity}</span>
                <Button variant="ghost" size="icon-sm" onClick={() => setQuantity(item.productId, item.quantity + 1)} aria-label="Artır">
                  <PlusIcon />
                </Button>
              </div>
              <span className="w-24 text-right font-medium">{formatMoney(item.unitPrice * item.quantity)}</span>
              <Button variant="ghost" size="icon-sm" onClick={() => remove(item.productId)} aria-label="Kaldır">
                <Trash2Icon />
              </Button>
            </div>
          ))}
        </CardContent>
      </Card>

      <Card className="h-fit">
        <CardHeader>
          <CardTitle>Sipariş özeti</CardTitle>
        </CardHeader>
        <CardContent className="space-y-4">
          <div className="space-y-1 text-sm">
            <div className="flex justify-between">
              <span>Ara toplam</span>
              <span>{formatMoney(subtotal)}</span>
            </div>
            <div className="flex justify-between">
              <span>Kargo</span>
              <span>{shipping === 0 ? "Ücretsiz" : formatMoney(shipping)}</span>
            </div>
            {shipping > 0 && (
              <p className="text-muted-foreground text-xs">{formatMoney(FREE_SHIPPING_THRESHOLD - subtotal)} daha ekle, kargo ücretsiz olsun.</p>
            )}
            <Separator className="my-2" />
            <div className="flex justify-between text-base font-semibold">
              <span>Toplam</span>
              <span>{formatMoney(subtotal + shipping)}</span>
            </div>
          </div>

          {isCustomer && (
            <div className="space-y-3">
              <div className="flex items-center gap-2">
                <Checkbox id="saved" checked={useSavedAddress} onCheckedChange={(v) => setUseSavedAddress(v === true)} />
                <Label htmlFor="saved">Kayıtlı adresime gönder</Label>
              </div>
              {!useSavedAddress && (
                <div className="grid gap-3">
                  <Field label="Alıcı" htmlFor="shipName">
                    <Input id="shipName" value={shipName} placeholder={`${user?.firstName} ${user?.lastName}`} onChange={(e) => setShipName(e.target.value)} />
                  </Field>
                  <Field label="Adres" htmlFor="street">
                    <Input id="street" value={address.street} onChange={(e) => setAddress({ ...address, street: e.target.value })} />
                  </Field>
                  <div className="grid grid-cols-2 gap-3">
                    <Field label="Şehir" htmlFor="city">
                      <Input id="city" value={address.city} onChange={(e) => setAddress({ ...address, city: e.target.value })} />
                    </Field>
                    <Field label="Posta kodu" htmlFor="postal">
                      <Input id="postal" value={address.postalCode ?? ""} onChange={(e) => setAddress({ ...address, postalCode: e.target.value })} />
                    </Field>
                  </div>
                  <Field label="Ülke" htmlFor="country">
                    <Input id="country" value={address.country} onChange={(e) => setAddress({ ...address, country: e.target.value })} />
                  </Field>
                </div>
              )}
            </div>
          )}

          {error && (
            <Alert variant="destructive">
              <AlertDescription>{error}</AlertDescription>
            </Alert>
          )}
        </CardContent>
        <CardFooter className="flex-col gap-2">
          {isCustomer ? (
            <Button
              className="w-full"
              size="lg"
              disabled={checkingOut}
              onClick={placeOrder}
            >
              <ShoppingBagIcon /> Siparişi tamamla
            </Button>
          ) : user ? (
            <p className="text-muted-foreground text-center text-sm">
              Personel hesabıyla siteden sipariş verilemez. Siparişleri yönetim panelinden oluşturabilirsin.
            </p>
          ) : (
            <>
              <Button asChild className="w-full" size="lg">
                <Link to="/login?returnUrl=/cart">Giriş yap ve tamamla</Link>
              </Button>
              <Button asChild variant="link" size="sm">
                <Link to="/register">Hesabın yok mu? Kayıt ol</Link>
              </Button>
            </>
          )}
        </CardFooter>
      </Card>
    </div>
  );
}
