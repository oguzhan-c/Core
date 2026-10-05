import { Link, useNavigate } from "react-router";
import { LayoutDashboardIcon, LogOutIcon, PackageIcon, StoreIcon, UserIcon } from "lucide-react";
import { toast } from "sonner";

import { Avatar, AvatarFallback } from "@/components/ui/avatar";
import { Button } from "@/components/ui/button";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { useAuth } from "@/lib/auth";
import { initials } from "@/lib/format";

export function UserMenu() {
  const { user, isStaff, isCustomer, logout } = useAuth();
  const navigate = useNavigate();

  if (!user) {
    return (
      <Button asChild size="sm" variant="outline">
        <Link to="/login">
          <UserIcon /> Giriş yap
        </Link>
      </Button>
    );
  }

  async function signOut() {
    await logout();
    toast.success("Çıkış yapıldı.");
    navigate("/");
  }

  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <Button variant="ghost" size="sm" className="gap-2 px-2">
          <Avatar className="size-7">
            <AvatarFallback className="bg-primary text-primary-foreground">{initials(user.firstName, user.lastName)}</AvatarFallback>
          </Avatar>
          <span className="hidden text-sm md:inline">{user.firstName}</span>
        </Button>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="end" className="w-60">
        <DropdownMenuLabel className="font-normal">
          <div className="font-medium">
            {user.firstName} {user.lastName}
          </div>
          <div className="text-muted-foreground text-xs">{user.email}</div>
          <div className="text-muted-foreground mt-1 text-xs">
            {user.tenantName} · {user.roles.join(", ")}
          </div>
        </DropdownMenuLabel>
        <DropdownMenuSeparator />
        {isStaff && (
          <DropdownMenuItem asChild>
            <Link to="/admin">
              <LayoutDashboardIcon /> Yönetim paneli
            </Link>
          </DropdownMenuItem>
        )}
        {isCustomer && (
          <DropdownMenuItem asChild>
            <Link to="/account/orders">
              <PackageIcon /> Siparişlerim
            </Link>
          </DropdownMenuItem>
        )}
        <DropdownMenuItem asChild>
          <Link to="/">
            <StoreIcon /> Mağaza
          </Link>
        </DropdownMenuItem>
        <DropdownMenuSeparator />
        <DropdownMenuItem onSelect={signOut}>
          <LogOutIcon /> Çıkış yap
        </DropdownMenuItem>
      </DropdownMenuContent>
    </DropdownMenu>
  );
}
