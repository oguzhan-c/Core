import { useState, type FormEvent } from "react";
import { Link, useNavigate, useSearchParams } from "react-router";
import { LogInIcon } from "lucide-react";
import { toast } from "sonner";

import { Field } from "@/components/common/field";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { ApiError, errorMessage } from "@/lib/api";
import { staffRoles, useAuth } from "@/lib/auth";
import { useStore } from "@/lib/store";
import { AuthCard } from "@/pages/auth/auth-card";
import { TenantSelect } from "@/pages/auth/tenant-select";

const demoAccounts = [
  { email: "admin", role: "Yönetici — her şey" },
  { email: "sales", role: "Satış — müşteri, sipariş, rapor" },
  { email: "warehouse", role: "Depo — stok, kargo" },
  { email: "customer", role: "Müşteri (yalnızca northwind) — site" },
];

export function LoginPage() {
  const { login } = useAuth();
  const { tenant: storeTenant, setTenant } = useStore();
  const navigate = useNavigate();
  const [params] = useSearchParams();

  const [tenant, setTenantValue] = useState(storeTenant);
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [pending, setPending] = useState(false);

  async function submit(e: FormEvent) {
    e.preventDefault();
    setError(null);
    setPending(true);
    try {
      const user = await login({ tenant, email, password });
      setTenant(tenant);
      toast.success(`Hoş geldin ${user.firstName}!`);
      const returnUrl = params.get("returnUrl");
      navigate(returnUrl ?? (user.roles.some((r) => staffRoles.includes(r)) ? "/admin" : "/"));
    } catch (err) {
      if (err instanceof ApiError && err.code === "email_not_confirmed") {
        navigate(`/verify-email?tenant=${encodeURIComponent(tenant)}&email=${encodeURIComponent(email)}`);
        toast.info("Önce e-posta adresini doğrula.");
        return;
      }
      setError(errorMessage(err));
    } finally {
      setPending(false);
    }
  }

  return (
    <AuthCard
      title="Giriş yap"
      description="Mağazanı seç ve hesabınla giriş yap. Oturum bilgisi yalnızca güvenli (HttpOnly) cookie'de tutulur."
      footer={
        import.meta.env.DEV && (
          <Card className="gap-3 py-4">
            <CardHeader className="px-4">
              <CardTitle className="text-sm">Demo hesaplar</CardTitle>
              <CardDescription className="text-xs">
                Şifre: <code>appsettings.Development.json</code> → <code>Seed:DemoUserPassword</code>. E-posta: <code>&lt;rol&gt;@&lt;mağaza&gt;.local</code>
              </CardDescription>
            </CardHeader>
            <CardContent className="grid gap-1 px-4 text-xs">
              {demoAccounts.map((a) => (
                <button
                  key={a.email}
                  type="button"
                  className="hover:bg-accent flex justify-between rounded px-2 py-1 text-left"
                  onClick={() => setEmail(`${a.email}@${tenant}.local`)}
                >
                  <code>
                    {a.email}@{tenant}.local
                  </code>
                  <span className="text-muted-foreground">{a.role}</span>
                </button>
              ))}
            </CardContent>
          </Card>
        )
      }
    >
      <form onSubmit={submit} className="grid gap-4">
        <TenantSelect value={tenant} onChange={setTenantValue} />
        <Field label="E-posta" htmlFor="email">
          <Input id="email" type="email" autoComplete="username" required value={email} onChange={(e) => setEmail(e.target.value)} />
        </Field>
        <Field label="Şifre" htmlFor="password">
          <Input
            id="password"
            type="password"
            autoComplete="current-password"
            required
            value={password}
            onChange={(e) => setPassword(e.target.value)}
          />
        </Field>
        {error && (
          <Alert variant="destructive">
            <AlertDescription>{error}</AlertDescription>
          </Alert>
        )}
        <Button type="submit" disabled={pending}>
          <LogInIcon /> Giriş yap
        </Button>
        <p className="text-muted-foreground text-center text-sm">
          Hesabın yok mu?{" "}
          <Link to="/register" className="text-primary hover:underline">
            Kayıt ol
          </Link>
        </p>
      </form>
    </AuthCard>
  );
}
