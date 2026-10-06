import { baseApi, params } from "@/lib/api";
import type {
  AuditLog,
  Customer,
  CustomerListItem,
  Order,
  OrderListItem,
  OrderStatus,
  Paginate,
  SalesByCategory,
  TopCustomer,
} from "@/lib/types";

export interface OrderListArgs {
  index: number;
  size: number;
  status?: OrderStatus;
  customerId?: string;
  from?: string;
  to?: string;
}

/** Sipariş durumu değişince: siparişler, stoklar (iptal), raporlar, outbox (kargo bildirimi), geçmiş. */
const afterOrderChange = ["Order", "MyOrder", "Product", "StoreCatalog", "Report", "Dashboard", "Outbox", "AuditLog"] as const;

/** Müşteriler, siparişler, raporlar ve değişiklik geçmişi. */
export const salesApi = baseApi.injectEndpoints({
  endpoints: (build) => ({
    // ------------------------------------------------------------ müşteriler
    getCustomers: build.query<Paginate<CustomerListItem>, { index: number; size: number; search?: string; country?: string }>({
      query: (args) => ({ url: "/api/customers", params: params({ ...args }) }),
      providesTags: ["Customer"],
    }),

    getCustomer: build.query<Customer, string>({
      query: (id) => `/api/customers/${id}`,
      providesTags: ["Customer"],
    }),

    getCustomerOrders: build.query<Paginate<OrderListItem>, { id: string; index: number; size: number }>({
      query: ({ id, ...page }) => ({ url: `/api/customers/${id}/orders`, params: page }),
      providesTags: ["Order"],
    }),

    // ------------------------------------------------------------ siparişler
    getOrders: build.query<Paginate<OrderListItem>, OrderListArgs>({
      query: (args) => ({ url: "/api/orders", params: params({ ...args }) }),
      providesTags: ["Order"],
    }),

    getOrder: build.query<Order, string>({
      query: (id) => `/api/orders/${id}`,
      providesTags: ["Order"],
    }),

    shipOrder: build.mutation<void, { id: string; shipperId: string }>({
      query: ({ id, shipperId }) => ({ url: `/api/orders/${id}/ship`, method: "POST", body: { shipperId } }),
      invalidatesTags: [...afterOrderChange],
    }),

    cancelOrder: build.mutation<void, string>({
      query: (id) => ({ url: `/api/orders/${id}/cancel`, method: "POST" }),
      invalidatesTags: [...afterOrderChange],
    }),

    // ------------------------------------------------------------ raporlar
    getSalesByCategory: build.query<SalesByCategory[], void>({
      query: () => "/api/reports/sales-by-category",
      providesTags: ["Report"],
    }),

    getTopCustomers: build.query<TopCustomer[], { count: number }>({
      query: (args) => ({ url: "/api/reports/top-customers", params: args }),
      providesTags: ["Report"],
    }),

    // ------------------------------------------------------------ değişiklik geçmişi
    getAuditLogs: build.query<Paginate<AuditLog>, { index: number; size: number; entityType?: string; entityId?: string }>({
      query: (args) => ({ url: "/api/audit-logs", params: params({ ...args }) }),
      providesTags: ["AuditLog"],
    }),
  }),
});

export const {
  useGetCustomersQuery,
  useGetCustomerQuery,
  useGetCustomerOrdersQuery,
  useGetOrdersQuery,
  useGetOrderQuery,
  useShipOrderMutation,
  useCancelOrderMutation,
  useGetSalesByCategoryQuery,
  useGetTopCustomersQuery,
  useGetAuditLogsQuery,
} = salesApi;
