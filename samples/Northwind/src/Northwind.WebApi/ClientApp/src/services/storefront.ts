import { baseApi, params } from "@/lib/api";
import type { Address, Order, OrderListItem, Paginate, PlaceOrderResult, StoreCategory, StoreProduct, StoreProductSort, StoreTenant } from "@/lib/types";

export interface StoreProductsArgs {
  tenant: string;
  categoryId?: string;
  sort?: StoreProductSort;
  search?: string;
  index?: number;
  size?: number;
}

export interface CheckoutRequest {
  lines: { productId: string; quantity: number }[];
  shipTo: { name: string; address: Address } | null;
}

/** Siparişle değişen veriler: stoklar (katalog), siparişler, raporlar. */
const afterOrderChange = ["StoreCatalog", "MyOrder", "Product", "Order", "Report", "Dashboard"] as const;

/** Mağaza sitesi: herkese açık katalog ve müşterinin kendi siparişleri. */
export const storefrontApi = baseApi.injectEndpoints({
  endpoints: (build) => ({
    getStoreTenants: build.query<StoreTenant[], void>({
      query: () => "/api/store/tenants",
      keepUnusedDataFor: 3600,
    }),

    getStoreCategories: build.query<StoreCategory[], string>({
      query: (tenant) => `/api/store/${encodeURIComponent(tenant)}/categories`,
      providesTags: ["StoreCatalog"],
    }),

    getStoreProducts: build.query<Paginate<StoreProduct>, StoreProductsArgs>({
      query: ({ tenant, ...rest }) => ({ url: `/api/store/${encodeURIComponent(tenant)}/products`, params: params({ ...rest }) }),
      providesTags: ["StoreCatalog"],
    }),

    getStoreProduct: build.query<StoreProduct, { tenant: string; id: string }>({
      query: ({ tenant, id }) => `/api/store/${encodeURIComponent(tenant)}/products/${id}`,
      providesTags: ["StoreCatalog"],
    }),

    checkout: build.mutation<PlaceOrderResult, CheckoutRequest>({
      query: (body) => ({ url: "/api/store/my/checkout", method: "POST", body }),
      invalidatesTags: [...afterOrderChange],
    }),

    getMyOrders: build.query<Paginate<OrderListItem>, { index: number; size: number }>({
      query: (page) => ({ url: "/api/store/my/orders", params: page }),
      providesTags: ["MyOrder"],
    }),

    getMyOrder: build.query<Order, string>({
      query: (id) => `/api/store/my/orders/${id}`,
      providesTags: ["MyOrder"],
    }),

    cancelMyOrder: build.mutation<void, string>({
      query: (id) => ({ url: `/api/store/my/orders/${id}/cancel`, method: "POST" }),
      invalidatesTags: [...afterOrderChange],
    }),
  }),
});

export const {
  useGetStoreTenantsQuery,
  useGetStoreCategoriesQuery,
  useGetStoreProductsQuery,
  useGetStoreProductQuery,
  useCheckoutMutation,
  useGetMyOrdersQuery,
  useGetMyOrderQuery,
  useCancelMyOrderMutation,
} = storefrontApi;
