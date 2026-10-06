import { useMemo } from "react";
import { createSlice, type PayloadAction } from "@reduxjs/toolkit";

import { useAppDispatch, useAppSelector } from "@/app/hooks";
import { useAuth } from "@/lib/auth";

// Mağaza seçimi ve sepet (Redux slice). Sepet mağaza başına tutulur ve tarayıcıda (localStorage) kalıcıdır; sipariş
// verilirken fiyatlar sunucuda yeniden hesaplanır (istemcideki fiyata güvenilmez).

export interface CartItem {
  productId: string;
  name: string;
  unitPrice: number;
  quantity: number;
  maxQuantity: number;
}

export interface StorefrontState {
  /** Giriş yapmamış ziyaretçinin seçtiği mağaza. Giriş yapınca token'daki mağaza geçerlidir. */
  selectedTenant: string;
  carts: Record<string, CartItem[]>;
}

export const FREE_SHIPPING_THRESHOLD = 500;
export const SHIPPING_FEE = 15;

const tenantKey = "nw.tenant";
const cartPrefix = "nw.cart.";

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

function loadInitialState(): StorefrontState {
  const carts: Record<string, CartItem[]> = {};
  try {
    for (let i = 0; i < localStorage.length; i++) {
      const key = localStorage.key(i);
      if (key?.startsWith(cartPrefix)) carts[key.slice(cartPrefix.length)] = read<CartItem[]>(key, []);
    }
  } catch {
    // localStorage kullanılamıyor
  }
  return { selectedTenant: read(tenantKey, "northwind"), carts };
}

/** Store değiştiğinde çağrılır (app/store.ts). */
export function persistStorefront(state: StorefrontState) {
  write(tenantKey, state.selectedTenant);
  for (const [tenant, items] of Object.entries(state.carts)) write(cartPrefix + tenant, items);
}

type ItemPayload = { tenant: string; item: Omit<CartItem, "quantity">; quantity: number };

export const storefrontSlice = createSlice({
  name: "storefront",
  initialState: loadInitialState,
  reducers: {
    tenantSelected(state, action: PayloadAction<string>) {
      state.selectedTenant = action.payload;
    },
    itemAdded(state, { payload: { tenant, item, quantity } }: PayloadAction<ItemPayload>) {
      const cart = (state.carts[tenant] ??= []);
      const existing = cart.find((i) => i.productId === item.productId);
      if (existing) {
        existing.maxQuantity = item.maxQuantity;
        existing.quantity = Math.min(existing.quantity + quantity, item.maxQuantity);
      } else {
        cart.push({ ...item, quantity: Math.min(quantity, item.maxQuantity) });
      }
    },
    quantityChanged(state, { payload }: PayloadAction<{ tenant: string; productId: string; quantity: number }>) {
      const item = state.carts[payload.tenant]?.find((i) => i.productId === payload.productId);
      if (item) item.quantity = Math.max(1, Math.min(payload.quantity, item.maxQuantity));
    },
    itemRemoved(state, { payload }: PayloadAction<{ tenant: string; productId: string }>) {
      state.carts[payload.tenant] = (state.carts[payload.tenant] ?? []).filter((i) => i.productId !== payload.productId);
    },
    cartCleared(state, action: PayloadAction<string>) {
      state.carts[action.payload] = [];
    },
  },
});

const { tenantSelected, itemAdded, quantityChanged, itemRemoved, cartCleared } = storefrontSlice.actions;
const emptyCart: CartItem[] = [];

/** Aktif mağaza ve onun sepeti. */
export function useStore() {
  const { user } = useAuth();
  const dispatch = useAppDispatch();
  const selectedTenant = useAppSelector((s) => s.storefront.selectedTenant);
  const tenant = user?.tenant ?? selectedTenant;
  const items = useAppSelector((s) => s.storefront.carts[tenant] ?? emptyCart);

  return useMemo(
    () => ({
      tenant,
      /** Giriş yapmış kullanıcı mağazasını değiştiremez (token'daki mağaza geçerlidir). */
      canChangeTenant: !user,
      setTenant: (value: string) => dispatch(tenantSelected(value)),
      items,
      count: items.reduce((sum, i) => sum + i.quantity, 0),
      subtotal: items.reduce((sum, i) => sum + i.unitPrice * i.quantity, 0),
      add: (item: Omit<CartItem, "quantity">, quantity = 1) => dispatch(itemAdded({ tenant, item, quantity })),
      setQuantity: (productId: string, quantity: number) => dispatch(quantityChanged({ tenant, productId, quantity })),
      remove: (productId: string) => dispatch(itemRemoved({ tenant, productId })),
      clear: () => dispatch(cartCleared(tenant)),
    }),
    [tenant, user, items, dispatch]
  );
}

export const shippingFor = (subtotal: number) => (subtotal === 0 || subtotal >= FREE_SHIPPING_THRESHOLD ? 0 : SHIPPING_FEE);
