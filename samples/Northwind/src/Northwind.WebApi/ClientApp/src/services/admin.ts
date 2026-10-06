import { baseApi, params } from "@/lib/api";
import type { Dashboard, Mail, OutboxMessage, OutboxStatus, Paginate, UserListItem } from "@/lib/types";

/** Yönetim: panel özeti, outbox, kullanıcılar, arka plan işleri ve geliştirme posta kutusu. */
export const adminApi = baseApi.injectEndpoints({
  endpoints: (build) => ({
    getDashboard: build.query<Dashboard, void>({
      query: () => "/api/admin/dashboard",
      providesTags: ["Dashboard"],
    }),

    getOutboxMessages: build.query<Paginate<OutboxMessage>, { index: number; size: number; status?: OutboxStatus }>({
      query: (args) => ({ url: "/api/admin/outbox", params: params({ ...args }) }),
      providesTags: ["Outbox"],
    }),

    retryOutboxMessage: build.mutation<void, string>({
      query: (id) => ({ url: `/api/admin/outbox/${id}/retry`, method: "POST" }),
      invalidatesTags: ["Outbox"],
    }),

    runReorderReport: build.mutation<void, void>({
      query: () => ({ url: "/api/admin/jobs/reorder-report", method: "POST" }),
    }),

    getUsers: build.query<Paginate<UserListItem>, { index: number; size: number; search?: string; role?: string }>({
      query: (args) => ({ url: "/api/admin/users", params: params({ ...args }) }),
      providesTags: ["User"],
    }),

    getMailbox: build.query<Mail[], void>({
      query: () => "/api/dev/mailbox",
      providesTags: ["Mailbox"],
    }),
  }),
});

export const {
  useGetDashboardQuery,
  useGetOutboxMessagesQuery,
  useRetryOutboxMessageMutation,
  useRunReorderReportMutation,
  useGetUsersQuery,
  useGetMailboxQuery,
} = adminApi;
