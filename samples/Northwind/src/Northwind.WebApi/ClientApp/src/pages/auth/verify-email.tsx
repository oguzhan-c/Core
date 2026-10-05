import { useState, type FormEvent } from "react";
import { Link, useNavigate, useSearchParams } from "react-router";
import { MailCheckIcon, MailIcon } from "lucide-react";
import { toast } from "sonner";

import { Field } from "@/components/common/field";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { api, errorMessage } from "@/lib/api";
import { useAuth } from "@/lib/auth";
import type { UserProfile } from "@/lib/types";
import { AuthCard } from "@/pages/auth/auth-card";

export function VerifyEmailPage() {
  const [params] = useSearchParams();
  const { setUser } = useAuth();
  const navigate = useNavigate();

  const tenant = params.get("tenant") ?? "northwind";
  const [email, setEmail] = useState(params.get("email") ?? "");
  const [code, setCode] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [pending, setPending] = useState(false);

  async function submit(e: FormEvent) {
    e.preventDefault();
    setError(null);
    setPending(true);
    try {
      const user = await api<UserProfile>("/api/auth/verify-email", { method: "POST", body: { tenant, email, code } });
      setUser(user);
      toast.success("E-posta adresin doğrulandı. Hoş geldin!");
      navigate("/");
    } catch (err) {
      setError(errorMessage(err));
    } finally {
      setPending(false);
    }
  }

  async function resend() {
    try {
      await api("/api/auth/resend-code", { method: "POST", body: { tenant, email } });
      toast.success("Yeni kod gönderildi.");
    } catch (err) {
      toast.error(errorMessage(err));
    }
  }

  return (
    <AuthCard
      title="E-postanı doğrula"
      description={
        <>
          <strong>{email || "E-posta adresine"}</strong> gönderilen 6 haneli kodu gir. Kod 10 dakika geçerli, en fazla 5 deneme hakkın var.
        </>
      }
      footer={
        import.meta.env.DEV && (
          <Alert>
            <MailIcon />
            <AlertDescription>
              Geliştirmede e-postalar gerçekten gönderilmez.{" "}
              <Link to="/dev/mailbox" target="_blank" className="text-primary underline">
                Geliştirme posta kutusunu aç
              </Link>{" "}
              ve kodu oradan al.
            </AlertDescription>
          </Alert>
        )
      }
    >
      <form onSubmit={submit} className="grid gap-4">
        {!params.get("email") && (
          <Field label="E-posta" htmlFor="email">
            <Input id="email" type="email" required value={email} onChange={(e) => setEmail(e.target.value)} />
          </Field>
        )}
        <Field label="Doğrulama kodu" htmlFor="code">
          <Input
            id="code"
            inputMode="numeric"
            autoComplete="one-time-code"
            maxLength={6}
            required
            className="text-center font-mono text-2xl tracking-[0.5em]"
            value={code}
            onChange={(e) => setCode(e.target.value.replace(/\D/g, ""))}
          />
        </Field>
        {error && (
          <Alert variant="destructive">
            <AlertDescription>{error}</AlertDescription>
          </Alert>
        )}
        <Button type="submit" disabled={pending || code.length !== 6}>
          <MailCheckIcon /> Doğrula
        </Button>
        <Button type="button" variant="link" onClick={resend} disabled={!email}>
          Kodu tekrar gönder
        </Button>
      </form>
    </AuthCard>
  );
}
