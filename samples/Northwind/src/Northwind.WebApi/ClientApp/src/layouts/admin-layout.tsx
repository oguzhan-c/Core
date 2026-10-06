import { useState } from "react";
import { Link, NavLink, Outlet } from "react-router";
import {
  BoxesIcon,
  FilterIcon,
  FolderTreeIcon,
  HistoryIcon,
  LayoutDashboardIcon,
  MenuIcon,
  RadioTowerIcon,
  ReceiptIcon,
  StoreIcon,
  TimerIcon,
  UsersIcon,
  type LucideIcon,
} from "lucide-react";

import { Button } from "@/components/ui/button";
import { Sheet, SheetContent, SheetTitle } from "@/components/ui/sheet";
import { Logo } from "@/layouts/site-layout";
import { UserMenu } from "@/layouts/user-menu";
import { useAuth } from "@/lib/auth";
import type { Role } from "@/lib/types";
import { cn } from "@/lib/utils";

interface NavItem {
  to: string;
  label: string;
  icon: LucideIcon;
  roles?: Role[];
  end?: boolean;
  /** Sunucu sayfası: yeni sekmede, tam sayfa yüklemeyle açılır. */
  external?: boolean;
}

const sections: { title: string; items: NavItem[] }[] = [
  {
    title: "Genel",
    items: [{ to: "/admin", label: "Özet", icon: LayoutDashboardIcon, end: true }],
  },
  {
    title: "Katalog",
    items: [
      { to: "/admin/products", label: "Ürünler", icon: BoxesIcon },
      { to: "/admin/categories", label: "Kategoriler", icon: FolderTreeIcon },
    ],
  },
  {
    title: "Satış",
    items: [
      { to: "/admin/orders", label: "Siparişler", icon: ReceiptIcon },
      { to: "/admin/customers", label: "Müşteriler", icon: StoreIcon },
      { to: "/admin/filter-lab", label: "Filtre laboratuvarı", icon: FilterIcon },
    ],
  },
  {
    title: "Sistem",
    items: [
      { to: "/admin/audit-logs", label: "Değişiklik geçmişi", icon: HistoryIcon, roles: ["Admin"] },
      { to: "/admin/outbox", label: "Outbox & işler", icon: RadioTowerIcon, roles: ["Admin"] },
      // Sunucu sayfası (SPA değil); yalnızca Hangfire:Enabled açıkken vardır.
      { to: "/hangfire", label: "Hangfire", icon: TimerIcon, roles: ["Admin"], external: true },
      { to: "/admin/users", label: "Kullanıcılar", icon: UsersIcon, roles: ["Admin"] },
    ],
  },
];

function Navigation({ onNavigate }: { onNavigate?: () => void }) {
  const { hasRole } = useAuth();

  return (
    <nav className="flex flex-col gap-5 p-3">
      {sections.map((section) => {
        const items = section.items.filter((i) => !i.roles || hasRole(...i.roles));
        if (items.length === 0) return null;
        return (
          <div key={section.title} className="space-y-1">
            <p className="text-muted-foreground px-2 text-xs font-medium tracking-wide uppercase">{section.title}</p>
            {items.map((item) =>
              item.external ? (
                <a
                  key={item.to}
                  href={item.to}
                  target="_blank"
                  rel="noreferrer"
                  onClick={onNavigate}
                  className="text-sidebar-foreground/80 hover:bg-sidebar-accent/60 flex items-center gap-2 rounded-md px-2 py-1.5 text-sm transition-colors"
                >
                  <item.icon className="size-4" />
                  {item.label}
                </a>
              ) : (
                <NavLink
                  key={item.to}
                  to={item.to}
                  end={item.end}
                  onClick={onNavigate}
                  className={({ isActive }) =>
                    cn(
                      "flex items-center gap-2 rounded-md px-2 py-1.5 text-sm transition-colors",
                      isActive
                        ? "bg-sidebar-accent text-sidebar-accent-foreground font-medium"
                        : "text-sidebar-foreground/80 hover:bg-sidebar-accent/60"
                    )
                  }
                >
                  <item.icon className="size-4" />
                  {item.label}
                </NavLink>
              )
            )}
          </div>
        );
      })}
    </nav>
  );
}

export function AdminLayout() {
  const [open, setOpen] = useState(false);
  const { user } = useAuth();

  return (
    <div className="flex min-h-svh">
      <aside className="bg-sidebar text-sidebar-foreground hidden w-60 shrink-0 border-r md:block">
        <div className="flex h-14 items-center border-b px-4">
          <Logo />
        </div>
        <Navigation />
      </aside>

      <Sheet open={open} onOpenChange={setOpen}>
        <SheetContent side="left" className="bg-sidebar w-64 p-0">
          <SheetTitle className="flex h-14 items-center border-b px-4">Northwind</SheetTitle>
          <Navigation onNavigate={() => setOpen(false)} />
        </SheetContent>
      </Sheet>

      <div className="flex min-w-0 flex-1 flex-col">
        <header className="bg-background sticky top-0 z-30 flex h-14 items-center gap-3 border-b px-4">
          <Button variant="ghost" size="icon" className="md:hidden" onClick={() => setOpen(true)} aria-label="Menü">
            <MenuIcon />
          </Button>
          <span className="text-muted-foreground text-sm">
            Yönetim paneli · <span className="text-foreground font-medium">{user?.tenantName}</span>
          </span>
          <div className="ml-auto flex items-center gap-2">
            <Button asChild variant="ghost" size="sm">
              <Link to="/">Mağazaya git</Link>
            </Button>
            <UserMenu />
          </div>
        </header>
        <main className="mx-auto w-full max-w-7xl flex-1 space-y-6 p-4 md:p-6">
          <Outlet />
        </main>
      </div>
    </div>
  );
}
