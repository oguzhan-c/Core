import { createContext, useCallback, useContext, useMemo, useState, type ReactNode } from "react";

import { useAuth } from "@/lib/auth";

// Mağaza seçimi ve sepet. Sepet tarayıcıda (localStorage) mağaza başına tutulur; sipariş verilirken
// fiyatlar sunucuda yeniden hesaplanır (istemcideki fiyata güvenilmez).

export interface CartItem {
  productId: string;
  name: string;
  unitPrice: number;
  quantity: number;
  maxQuantity: number;
}

interface StoreContextValue {
  tenant: string;
  /** Giriş yapmış kullanıcı mağazasını değiştiremez (token'daki mağaza geçerlidir). */
  canChangeTenant: boolean;
  setTenant: (tenant: string) => void;
  items: CartItem[];
  count: number;
  subtotal: number;
  add: (item: Omit<CartItem, "quantity">, quantity?: number) => void;
  setQuantity: (productId: string, quantity: number) => void;
  remove: (productId: string) => void;
  clear: () => void;
}

const StoreContext = createContext<StoreContextValue | null>(null);

export const FREE_SHIPPING_THRESHOLD = 500;
export const SHIPPING_FEE = 15;

const tenantKey = "nw.tenant";
const cartKey = (tenant: string) => `nw.cart.${tenant}`;

function read<T>(key: string, fallback: T): T {
  try {
    const raw = localStorage.getItem(key);
    return raw ? (JSON.parse(raw) as T) : fallback;
  } catch {
    return fallback;
  }
}

function write(key: string, value: unknown) {
  try {
    localStorage.setItem(key, JSON.stringify(value));
  } catch {
    // gizli pencere ya da kota: sepet yalnızca bu oturumda kalır
  }
}

export function StoreProvider({ children }: { children: ReactNode }) {
  const { user } = useAuth();
  const [selectedTenant, setSelectedTenant] = useState(() => read(tenantKey, "northwind"));
  const tenant = user?.tenant ?? selectedTenant;
  // Sepetler mağaza başına; ilk erişimde localStorage'dan okunur, her değişiklikte yazılır.
  const [carts, setCarts] = useState<Record<string, CartItem[]>>({});
  const items = useMemo(() => carts[tenant] ?? read<CartItem[]>(cartKey(tenant), []), [carts, tenant]);

  const update = useCallback(
    (change: (current: CartItem[]) => CartItem[]) =>
      setCarts((previous) => {
        const next = change(previous[tenant] ?? read<CartItem[]>(cartKey(tenant), []));
        write(cartKey(tenant), next);
        return { ...previous, [tenant]: next };
      }),
    [tenant]
  );

  const setTenant = useCallback((value: string) => {
    setSelectedTenant(value);
    write(tenantKey, value);
  }, []);

  const add = useCallback(
    (item: Omit<CartItem, "quantity">, quantity = 1) =>
      update((current) => {
        const existing = current.find((i) => i.productId === item.productId);
        if (existing) {
          return current.map((i) =>
            i.productId === item.productId ? { ...i, maxQuantity: item.maxQuantity, quantity: Math.min(i.quantity + quantity, item.maxQuantity) } : i
          );
        }
        return [...current, { ...item, quantity: Math.min(quantity, item.maxQuantity) }];
      }),
    [update]
  );

  const setQuantity = useCallback(
    (productId: string, quantity: number) =>
      update((current) =>
        current.map((i) => (i.productId === productId ? { ...i, quantity: Math.max(1, Math.min(quantity, i.maxQuantity)) } : i))
      ),
    [update]
  );

  const remove = useCallback((productId: string) => update((current) => current.filter((i) => i.productId !== productId)), [update]);
  const clear = useCallback(() => update(() => []), [update]);

  const value = useMemo<StoreContextValue>(
    () => ({
      tenant,
      canChangeTenant: !user,
      setTenant,
      items,
      count: items.reduce((sum, i) => sum + i.quantity, 0),
      subtotal: items.reduce((sum, i) => sum + i.unitPrice * i.quantity, 0),
      add,
      setQuantity,
      remove,
      clear,
    }),
    [tenant, user, setTenant, items, add, setQuantity, remove, clear]
  );

  return <StoreContext.Provider value={value}>{children}</StoreContext.Provider>;
}

export function useStore() {
  const context = useContext(StoreContext);
  if (!context) throw new Error("useStore, StoreProvider içinde kullanılmalı.");
  return context;
}

export const shippingFor = (subtotal: number) => (subtotal === 0 || subtotal >= FREE_SHIPPING_THRESHOLD ? 0 : SHIPPING_FEE);
