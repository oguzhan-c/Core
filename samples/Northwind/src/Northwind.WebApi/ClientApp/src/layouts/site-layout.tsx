import { Link, NavLink, Outlet } from "react-router";
import { MailIcon, ShoppingCartIcon } from "lucide-react";

import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { UserMenu } from "@/layouts/user-menu";
import { useStore } from "@/lib/store";
import { cn } from "@/lib/utils";
import { useGetStoreTenantsQuery } from "@/services/storefront";

export function Logo({ className }: { className?: string }) {
  return (
    <Link to="/" className={cn("flex items-center gap-2 font-semibold", className)}>
      <img src="/favicon.svg" alt="" className="size-7" />
      <span>Northwind</span>
    </Link>
  );
}

export function SiteLayout() {
  const { tenant, setTenant, canChangeTenant, count } = useStore();
  const tenants = useGetStoreTenantsQuery();

  const navClass = ({ isActive }: { isActive: boolean }) =>
    cn("text-sm transition-colors hover:text-foreground", isActive ? "text-foreground font-medium" : "text-muted-foreground");

  return (
    <div className="flex min-h-svh flex-col">
      <header className="bg-background/80 sticky top-0 z-40 border-b backdrop-blur">
        <div className="mx-auto flex h-14 max-w-7xl items-center gap-6 px-4">
          <Logo />
          <nav className="hidden items-center gap-5 sm:flex">
            <NavLink to="/" end className={navClass}>
              Ana sayfa
            </NavLink>
            <NavLink to="/products" className={navClass}>
              Ürünler
            </NavLink>
          </nav>
          <div className="ml-auto flex items-center gap-2">
            {tenants.data && (
              <Select value={tenant} onValueChange={setTenant} disabled={!canChangeTenant}>
                <SelectTrigger size="sm" className="w-44" title={canChangeTenant ? "Mağaza" : "Giriş yaptığın mağaza"}>
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  {tenants.data.map((t) => (
                    <SelectItem key={t.identifier} value={t.identifier}>
                      {t.name}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            )}
            <Button asChild variant="ghost" size="sm" className="relative">
              <Link to="/cart" aria-label="Sepet">
                <ShoppingCartIcon />
                {count > 0 && <Badge className="absolute -top-1 -right-1 h-5 min-w-5 rounded-full px-1">{count}</Badge>}
              </Link>
            </Button>
            <UserMenu />
          </div>
        </div>
      </header>

      <main className="flex-1">
        <Outlet />
      </main>

      <footer className="text-muted-foreground border-t py-6 text-sm">
        <div className="mx-auto flex max-w-7xl flex-wrap items-center justify-between gap-2 px-4">
          <span>Northwind · Can.Core starter</span>
          <span className="flex items-center gap-4">
            {import.meta.env.DEV && (
              <Link to="/dev/mailbox" className="flex items-center gap-1 hover:underline">
                <MailIcon className="size-3.5" /> Geliştirme posta kutusu
              </Link>
            )}
            <a href="/swagger" className="hover:underline">
              API
            </a>
          </span>
        </div>
      </footer>
    </div>
  );
}
