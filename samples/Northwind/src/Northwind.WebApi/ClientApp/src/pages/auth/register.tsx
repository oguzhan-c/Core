import { useState, type ChangeEvent, type FormEvent } from "react";
import { Link, useNavigate } from "react-router";
import { UserPlusIcon } from "lucide-react";
import { toast } from "sonner";

import { Field } from "@/components/common/field";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { api, ApiError, errorMessage } from "@/lib/api";
import { useStore } from "@/lib/store";
import type { RegisterResult } from "@/lib/types";
import { AuthCard } from "@/pages/auth/auth-card";
import { TenantSelect } from "@/pages/auth/tenant-select";

export function RegisterPage() {
  const { tenant: storeTenant, setTenant } = useStore();
  const navigate = useNavigate();

  const [tenant, setTenantValue] = useState(storeTenant);
  const [form, setForm] = useState({ firstName: "", lastName: "", companyName: "", phone: "", email: "", password: "" });
  const [errors, setErrors] = useState<Record<string, string[]>>({});
  const [error, setError] = useState<string | null>(null);
  const [pending, setPending] = useState(false);

  const set = (key: keyof typeof form) => (e: ChangeEvent<HTMLInputElement>) => setForm({ ...form, [key]: e.target.value });
  const fieldError = (name: string) => errors[name.charAt(0).toUpperCase() + name.slice(1)]?.[0] ?? errors[name]?.[0];

  async function submit(e: FormEvent) {
    e.preventDefault();
    setError(null);
    setErrors({});
    setPending(true);
    try {
      const result = await api<RegisterResult>("/api/auth/register", { method: "POST", body: { tenant, ...form, phone: form.phone || null } });
      setTenant(tenant);
      toast.success("Doğrulama kodu e-postana gönderildi.");
      navigate(`/verify-email?tenant=${encodeURIComponent(tenant)}&email=${encodeURIComponent(result.email)}`);
    } catch (err) {
      if (err instanceof ApiError && err.errors) setErrors(err.errors);
      setError(errorMessage(err));
    } finally {
      setPending(false);
    }
  }

  return (
    <AuthCard title="Hesap oluştur" description="Kayıttan sonra e-postana 6 haneli bir doğrulama kodu gönderilir.">
      <form onSubmit={submit} className="grid gap-4">
        <TenantSelect value={tenant} onChange={setTenantValue} />
        <div className="grid grid-cols-2 gap-3">
          <Field label="Ad" htmlFor="firstName" error={fieldError("firstName")}>
            <Input id="firstName" required value={form.firstName} onChange={set("firstName")} />
          </Field>
          <Field label="Soyad" htmlFor="lastName" error={fieldError("lastName")}>
            <Input id="lastName" required value={form.lastName} onChange={set("lastName")} />
          </Field>
        </div>
        <Field label="Firma" htmlFor="companyName" error={fieldError("companyName")}>
          <Input id="companyName" required value={form.companyName} onChange={set("companyName")} />
        </Field>
        <Field label="Telefon" htmlFor="phone" error={fieldError("phone")}>
          <Input id="phone" value={form.phone} onChange={set("phone")} />
        </Field>
        <Field label="E-posta" htmlFor="email" error={fieldError("email")}>
          <Input id="email" type="email" autoComplete="email" required value={form.email} onChange={set("email")} />
        </Field>
        <Field label="Şifre" htmlFor="password" hint="En az 8 karakter; harf ve rakam içermeli." error={fieldError("password")}>
          <Input id="password" type="password" autoComplete="new-password" required value={form.password} onChange={set("password")} />
        </Field>
        {error && (
          <Alert variant="destructive">
            <AlertDescription>{error}</AlertDescription>
          </Alert>
        )}
        <Button type="submit" disabled={pending}>
          <UserPlusIcon /> Kayıt ol
        </Button>
        <p className="text-muted-foreground text-center text-sm">
          Zaten hesabın var mı?{" "}
          <Link to="/login" className="text-primary hover:underline">
            Giriş yap
          </Link>
        </p>
      </form>
    </AuthCard>
  );
}
