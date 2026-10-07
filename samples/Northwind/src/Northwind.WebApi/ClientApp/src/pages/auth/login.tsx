import { useState, type FormEvent } from "react";
import { Link, useNavigate, useSearchParams } from "react-router";
import { ArrowLeftIcon, ExternalLinkIcon, FingerprintIcon, LogInIcon, ShieldCheckIcon } from "lucide-react";
import { toast } from "sonner";

import { Field } from "@/components/common/field";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { errorMessage, isApiError } from "@/lib/api";
import { staffRoles, useAuth } from "@/lib/auth";
import { externalErrorMessage, externalLoginUrl } from "@/lib/external-login";
import { useStore } from "@/lib/store";
import type { TwoFactorPrompt, UserProfile } from "@/lib/types";
import { passkeyErrorMessage, passkeysSupported } from "@/lib/webauthn";
import { AuthCard } from "@/pages/auth/auth-card";
import { TenantSelect } from "@/pages/auth/tenant-select";
import { useGetAuthFeaturesQuery, useGetExternalProvidersQuery } from "@/services/auth";

const demoAccounts = [
  { email: "admin", role: "Yönetici — her şey" },
  { email: "sales", role: "Satış — müşteri, sipariş, rapor" },
  { email: "warehouse", role: "Depo — stok, kargo" },
  { email: "customer", role: "Müşteri (yalnızca northwind) — site" },
];

export function LoginPage() {
  const { login, loginWithPasskey } = useAuth();
  const { tenant: storeTenant, setTenant } = useStore();
  const navigate = useNavigate();
  const [params] = useSearchParams();

  const [tenant, setTenantValue] = useState(storeTenant);
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  // Dış sağlayıcıdan dönüşte sunucu hata kodunu ya da ikinci adım bilgisini adrese ekler.
  const [error, setError] = useState<string | null>(() => externalErrorMessage(params.get("externalError")));
  const [pending, setPending] = useState(false);
  const [twoFactor, setTwoFactor] = useState<TwoFactorPrompt | null>(() => {
    const method = params.get("twoFactor");
    return method === "Email" || method === "Otp" ? { method, destination: params.get("destination") } : null;
  });

  const features = useGetAuthFeaturesQuery();
  const canUsePasskey = !!features.data?.passkeys && passkeysSupported();
  const externalProviders = useGetExternalProvidersQuery().data ?? [];

  function signInWith(provider: string) {
    // Tam sayfa yönlendirme: sağlayıcıya gidilir, dönüşte oturum cookie'leri yazılmış olur.
    setTenant(tenant);
    setPending(true);
    window.location.assign(externalLoginUrl(provider, tenant, params.get("returnUrl")));
  }

  function finish(user: UserProfile) {
    setTenant(tenant);
    toast.success(`Hoş geldin ${user.firstName}!`);
    const returnUrl = params.get("returnUrl");
    navigate(returnUrl ?? (user.roles.some((r) => staffRoles.includes(r)) ? "/admin" : "/"));
  }

  async function submit(e: FormEvent) {
    e.preventDefault();
    setError(null);
    setPending(true);
    try {
      const outcome = await login({ tenant, email, password });
      if (outcome.twoFactor) {
        setPassword("");
        setTwoFactor(outcome.twoFactor);
        return;
      }
      finish(outcome.user);
    } catch (err) {
      if (isApiError(err) && err.code === "email_not_confirmed") {
        navigate(`/verify-email?tenant=${encodeURIComponent(tenant)}&email=${encodeURIComponent(email)}`);
        toast.info("Önce e-posta adresini doğrula.");
        return;
      }
      setError(errorMessage(err));
    } finally {
      setPending(false);
    }
  }

  async function signInWithPasskey() {
    setError(null);
    setPending(true);
    try {
      finish(await loginWithPasskey(tenant));
    } catch (err) {
      setError(passkeyErrorMessage(err) ?? errorMessage(err));
    } finally {
      setPending(false);
    }
  }

  if (twoFactor) {
    return (
      <TwoFactorStep
        prompt={twoFactor}
        onSuccess={finish}
        onCancel={() => {
          setTwoFactor(null);
          setError(null);
        }}
      />
    );
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
        {(canUsePasskey || externalProviders.length > 0) && (
          <div className="text-muted-foreground flex items-center gap-3 text-xs">
            <span className="bg-border h-px flex-1" />
            veya
            <span className="bg-border h-px flex-1" />
          </div>
        )}
        {canUsePasskey && (
          <Button type="button" variant="outline" disabled={pending} onClick={signInWithPasskey}>
            <FingerprintIcon /> Passkey ile giriş yap
          </Button>
        )}
        {externalProviders.map((p) => (
          <Button key={p.name} type="button" variant="outline" disabled={pending || !tenant} onClick={() => signInWith(p.name)}>
            <ExternalLinkIcon /> {p.displayName} ile giriş yap
          </Button>
        ))}
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

/** İkinci adım: authenticator uygulamasındaki ya da e-postaya gelen 6 haneli kod. */
function TwoFactorStep({ prompt, onSuccess, onCancel }: { prompt: TwoFactorPrompt; onSuccess: (user: UserProfile) => void; onCancel: () => void }) {
  const { completeTwoFactor, resendTwoFactorCode } = useAuth();
  const [code, setCode] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [pending, setPending] = useState(false);

  const byEmail = prompt.method === "Email";

  async function submit(e: FormEvent) {
    e.preventDefault();
    setError(null);
    setPending(true);
    try {
      onSuccess(await completeTwoFactor(code));
    } catch (err) {
      if (isApiError(err) && err.status === 401) {
        // Bekleyen giriş geçersiz (süre doldu ya da hesap kilitlendi): baştan giriş.
        toast.error(errorMessage(err));
        onCancel();
        return;
      }
      setCode("");
      setError(errorMessage(err));
    } finally {
      setPending(false);
    }
  }

  async function resend() {
    try {
      await resendTwoFactorCode();
      toast.success("Yeni kod gönderildi.");
    } catch (err) {
      toast.error(errorMessage(err));
    }
  }

  return (
    <AuthCard
      title="İki adımlı doğrulama"
      description={
        byEmail ? (
          <>
            <strong>{prompt.destination ?? "E-posta adresine"}</strong> gönderilen 6 haneli kodu gir.
          </>
        ) : (
          "Authenticator uygulamandaki (Google Authenticator, Microsoft Authenticator, 1Password ...) 6 haneli kodu gir."
        )
      }
      footer={
        import.meta.env.DEV &&
        byEmail && (
          <Alert>
            <AlertDescription>
              Geliştirmede e-postalar klasöre yazılıyorsa kodu{" "}
              <Link to="/dev/mailbox" target="_blank" className="text-primary underline">
                geliştirme posta kutusunda
              </Link>{" "}
              bulabilirsin.
            </AlertDescription>
          </Alert>
        )
      }
    >
      <form onSubmit={submit} className="grid gap-4">
        <Field label="Doğrulama kodu" htmlFor="code">
          <Input
            id="code"
            inputMode="numeric"
            autoComplete="one-time-code"
            autoFocus
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
          <ShieldCheckIcon /> Doğrula ve giriş yap
        </Button>
        <div className="flex justify-between">
          <Button type="button" variant="ghost" size="sm" onClick={onCancel}>
            <ArrowLeftIcon /> Geri
          </Button>
          {byEmail && (
            <Button type="button" variant="link" size="sm" onClick={resend}>
              Kodu tekrar gönder
            </Button>
          )}
        </div>
      </form>
    </AuthCard>
  );
}
