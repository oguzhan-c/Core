import type { ReactNode } from "react";
import { createBrowserRouter, Navigate, RouterProvider, useLocation } from "react-router";

import { Skeleton } from "@/components/ui/skeleton";
import { AdminLayout } from "@/layouts/admin-layout";
import { SiteLayout } from "@/layouts/site-layout";
import { useAuth } from "@/lib/auth";
import type { Role } from "@/lib/types";
import { AccountSecurityPage } from "@/pages/account/security";
import { AdminAuditLogsPage } from "@/pages/admin/audit-logs";
import { AdminCategoriesPage } from "@/pages/admin/categories";
import { AdminCustomerDetailPage, AdminCustomersPage } from "@/pages/admin/customers";
import { AdminDashboardPage } from "@/pages/admin/dashboard";
import { FilterLabPage } from "@/pages/admin/filter-lab";
import { AdminOrderDetailPage, AdminOrdersPage } from "@/pages/admin/orders";
import { AdminOutboxPage } from "@/pages/admin/outbox";
import { AdminProductsPage } from "@/pages/admin/products";
import { AdminUsersPage } from "@/pages/admin/users";
import { LoginPage } from "@/pages/auth/login";
import { RegisterPage } from "@/pages/auth/register";
import { VerifyEmailPage } from "@/pages/auth/verify-email";
import { CartPage } from "@/pages/site/cart";
import { CatalogPage } from "@/pages/site/catalog";
import { HomePage } from "@/pages/site/home";
import { MailboxPage } from "@/pages/site/mailbox";
import { MyOrderDetailPage, MyOrdersPage } from "@/pages/site/my-orders";
import { NotFoundPage } from "@/pages/site/not-found";
import { ProductDetailPage } from "@/pages/site/product-detail";

/** Giriş ve rol kontrolü. Asıl yetki kontrolü sunucudadır; bu yalnızca doğru sayfaya yönlendirir. */
function RequireRole({ roles, children }: { roles: Role[]; children: ReactNode }) {
  const { user, isLoading, hasRole } = useAuth();
  const location = useLocation();

  if (isLoading) return <Skeleton className="m-6 h-40" />;
  if (!user) return <Navigate to={`/login?returnUrl=${encodeURIComponent(location.pathname + location.search)}`} replace />;
  if (!hasRole(...roles, "Admin")) return <Navigate to="/" replace />;
  return children;
}

const router = createBrowserRouter([
  {
    element: <SiteLayout />,
    children: [
      { index: true, element: <HomePage /> },
      { path: "products", element: <CatalogPage /> },
      { path: "products/:id", element: <ProductDetailPage /> },
      { path: "cart", element: <CartPage /> },
      {
        path: "account/orders",
        element: (
          <RequireRole roles={["Customer"]}>
            <MyOrdersPage />
          </RequireRole>
        ),
      },
      {
        path: "account/orders/:id",
        element: (
          <RequireRole roles={["Customer"]}>
            <MyOrderDetailPage />
          </RequireRole>
        ),
      },
      {
        path: "account/security",
        element: (
          <RequireRole roles={["Customer", "Sales", "Warehouse"]}>
            <AccountSecurityPage />
          </RequireRole>
        ),
      },
      { path: "login", element: <LoginPage /> },
      { path: "register", element: <RegisterPage /> },
      { path: "verify-email", element: <VerifyEmailPage /> },
      { path: "dev/mailbox", element: <MailboxPage /> },
      { path: "*", element: <NotFoundPage /> },
    ],
  },
  {
    path: "admin",
    element: (
      <RequireRole roles={["Sales", "Warehouse"]}>
        <AdminLayout />
      </RequireRole>
    ),
    children: [
      { index: true, element: <AdminDashboardPage /> },
      { path: "products", element: <AdminProductsPage /> },
      { path: "categories", element: <AdminCategoriesPage /> },
      { path: "customers", element: <AdminCustomersPage /> },
      { path: "customers/:id", element: <AdminCustomerDetailPage /> },
      { path: "orders", element: <AdminOrdersPage /> },
      { path: "orders/:id", element: <AdminOrderDetailPage /> },
      { path: "filter-lab", element: <FilterLabPage /> },
      { path: "audit-logs", element: <AdminAuditLogsPage /> },
      { path: "outbox", element: <AdminOutboxPage /> },
      { path: "users", element: <AdminUsersPage /> },
    ],
  },
]);

export function App() {
  return <RouterProvider router={router} />;
}
