import { useEffect, useState, type FormEvent, type ReactNode } from "react";
import { QRCodeSVG } from "qrcode.react";
import {
  FingerprintIcon,
  KeyRoundIcon,
  MailIcon,
  PencilIcon,
  PlusIcon,
  ShieldCheckIcon,
  ShieldOffIcon,
  SmartphoneIcon,
  Trash2Icon,
} from "lucide-react";
import { toast } from "sonner";

import { ConfirmDialog } from "@/components/common/confirm-dialog";
import { Field } from "@/components/common/field";
import { PageHeader } from "@/components/common/page-header";
import { QueryState } from "@/components/common/query-state";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import { errorMessage } from "@/lib/api";
import { formatDateTime } from "@/lib/format";
import type { AccountSecurity, PasskeyInfo, TwoFactorMethod } from "@/lib/types";
import { createPasskey, passkeyErrorMessage, passkeysSupported } from "@/lib/webauthn";
import {
  useAddPasskeyMutation,
  useBeginOtpSetupMutation,
  useDeletePasskeyMutation,
  useDisableTwoFactorMutation,
  useEnableEmailTwoFactorMutation,
  useEnableOtpMutation,
  useGetAccountSecurityQuery,
  usePasskeyRegistrationOptionsMutation,
  useRenamePasskeyMutation,
} from "@/services/account";

const methodLabels: Record<TwoFactorMethod, string> = {
  None: "Kapalı",
  Email: "E-posta ile kod",
  Otp: "Authenticator uygulaması",
};

/** Hesap güvenliği: iki adımlı doğrulama (TOTP / e-posta) ve passkey'ler. */
export function AccountSecurityPage() {
  // Bu sayfadaki her işlem "Security" etiketini geçersiz kılar; özet kendiliğinden yenilenir.
  const security = useGetAccountSecurityQuery();

  return (
    <div className="mx-auto grid max-w-3xl gap-6 px-4 py-8">
      <PageHeader
        title="Hesap güvenliği"
        description="Girişte şifreye ek olarak ikinci bir adım iste ya da şifre yerine cihazındaki passkey ile giriş yap."
      />
      <QueryState isLoading={security.isLoading} error={security.error} rows={4} />
      {security.data && (
        <>
          <TwoFactorCard security={security.data} />
          <PasskeysCard security={security.data} />
        </>
      )}
    </div>
  );
}

// ---------------------------------------------------------------- iki adımlı doğrulama

function TwoFactorCard({ security }: { security: AccountSecurity }) {
  const [otpOpen, setOtpOpen] = useState(false);
  const [emailOpen, setEmailOpen] = useState(false);
  const [disableOpen, setDisableOpen] = useState(false);

  const method = security.twoFactor;
  const [enableEmailTwoFactor] = useEnableEmailTwoFactorMutation();

  async function enableEmail() {
    try {
      await enableEmailTwoFactor().unwrap();
      toast.success("Girişte artık e-postana kod gönderilecek.");
    } catch (err) {
      toast.error(errorMessage(err));
    }
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle className="flex items-center gap-2">
          <ShieldCheckIcon className="size-5" /> İki adımlı doğrulama
          <Badge variant={method === "None" ? "warning" : "success"}>{methodLabels[method]}</Badge>
        </CardTitle>
        <CardDescription>
          Açıkken şifren doğru olsa bile giriş için ikinci bir kod istenir. Şifren ele geçirilse bile hesabına girilemez.
        </CardDescription>
      </CardHeader>
      <CardContent className="grid gap-3">
        {method === "None" && (
          <div className="grid gap-3 sm:grid-cols-2">
            <MethodOption
              icon={<SmartphoneIcon />}
              title="Authenticator uygulaması"
              description="Google/Microsoft Authenticator, 1Password ... 30 saniyede bir değişen kod. Önerilen."
              action="Kur"
              onClick={() => setOtpOpen(true)}
            />
            <MethodOption
              icon={<MailIcon />}
              title="E-posta ile kod"
              description={`Her girişte ${security.email} adresine 6 haneli kod gönderilir.`}
              action="Aç"
              disabled={!security.emailConfirmed}
              onClick={() => setEmailOpen(true)}
            />
          </div>
        )}

        {method === "Email" && (
          <Alert>
            <MailIcon />
            <AlertTitle>Girişte e-postana kod gönderiliyor</AlertTitle>
            <AlertDescription>Daha güvenli olması için authenticator uygulamasına geçebilirsin.</AlertDescription>
          </Alert>
        )}

        {method === "Otp" && (
          <Alert>
            <SmartphoneIcon />
            <AlertTitle>Girişte authenticator uygulamandaki kod isteniyor</AlertTitle>
            <AlertDescription>Telefonunu değiştirirsen önce burada kapat, sonra yeni telefonda tekrar kur.</AlertDescription>
          </Alert>
        )}

        {method !== "None" && (
          <div className="flex flex-wrap gap-2">
            {method === "Email" && (
              <Button variant="outline" onClick={() => setOtpOpen(true)}>
                <SmartphoneIcon /> Authenticator uygulamasına geç
              </Button>
            )}
            <Button variant="outline" onClick={() => setDisableOpen(true)}>
              <ShieldOffIcon /> Kapat
            </Button>
          </div>
        )}
      </CardContent>

      <OtpSetupDialog open={otpOpen} onOpenChange={setOtpOpen} />
      <DisableTwoFactorDialog open={disableOpen} onOpenChange={setDisableOpen} />
      <ConfirmDialog
        open={emailOpen}
        onOpenChange={setEmailOpen}
        title="E-posta ile iki adımlı doğrulama"
        description={`Bundan sonra her girişte ${security.email} adresine gönderilen kodu girmen gerekecek.`}
        confirmText="Aç"
        onConfirm={enableEmail}
      />
    </Card>
  );
}

function MethodOption({
  icon,
  title,
  description,
  action,
  disabled,
  onClick,
}: {
  icon: ReactNode;
  title: string;
  description: string;
  action: string;
  disabled?: boolean;
  onClick: () => void;
}) {
  return (
    <div className="flex flex-col gap-3 rounded-lg border p-4">
      <div className="flex items-center gap-2 font-medium [&>svg]:size-4">
        {icon}
        {title}
      </div>
      <p className="text-muted-foreground flex-1 text-sm">{description}</p>
      <Button size="sm" onClick={onClick} disabled={disabled} className="self-start">
        {action}
      </Button>
    </div>
  );
}

function OtpSetupDialog({ open, onOpenChange }: { open: boolean; onOpenChange: (open: boolean) => void }) {
  const [code, setCode] = useState("");
  const [error, setError] = useState<string | null>(null);

  // Pencere her açıldığında yeni anahtar üretilir (yarım kalan eski kurulum sunucuda silinir).
  const [beginSetup, setup] = useBeginOtpSetupMutation();
  const [enableOtp, { isLoading: enabling }] = useEnableOtpMutation();

  useEffect(() => {
    if (open) void beginSetup();
  }, [open, beginSetup]);

  async function enable() {
    try {
      await enableOtp(code).unwrap();
      toast.success("Authenticator uygulaması bağlandı. Girişte artık uygulamadaki kod istenecek.");
      close(false);
    } catch (err) {
      setCode("");
      setError(errorMessage(err));
    }
  }

  function close(next: boolean) {
    if (!next) {
      setCode("");
      setError(null);
    }
    onOpenChange(next);
  }

  function submit(e: FormEvent) {
    e.preventDefault();
    setError(null);
    void enable();
  }

  return (
    <Dialog open={open} onOpenChange={close}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Authenticator uygulaması kur</DialogTitle>
          <DialogDescription>1. Uygulamada “hesap ekle” → QR kodu okut. 2. Uygulamanın gösterdiği 6 haneli kodu aşağıya yaz.</DialogDescription>
        </DialogHeader>

        <QueryState isLoading={setup.isLoading} error={setup.error} rows={3} />
        {setup.data && (
          <form onSubmit={submit} className="grid gap-4">
            <div className="flex flex-col items-center gap-3">
              <div className="rounded-lg bg-white p-3">
                <QRCodeSVG value={setup.data.provisioningUri} size={180} />
              </div>
              <div className="text-center text-xs">
                <div className="text-muted-foreground">QR okutamıyorsan anahtarı elle gir:</div>
                <code className="bg-muted mt-1 inline-block rounded px-2 py-1 font-mono text-sm select-all">{setup.data.secret}</code>
              </div>
            </div>
            <Field label="Uygulamadaki kod" htmlFor="otp-code">
              <Input
                id="otp-code"
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
            <DialogFooter>
              <Button type="button" variant="outline" onClick={() => close(false)}>
                Vazgeç
              </Button>
              <Button type="submit" disabled={enabling || code.length !== 6}>
                <ShieldCheckIcon /> Doğrula ve aç
              </Button>
            </DialogFooter>
          </form>
        )}
      </DialogContent>
    </Dialog>
  );
}

function DisableTwoFactorDialog({ open, onOpenChange }: { open: boolean; onOpenChange: (open: boolean) => void }) {
  const [password, setPassword] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [disableTwoFactor, { isLoading: disabling }] = useDisableTwoFactorMutation();

  async function disable() {
    try {
      await disableTwoFactor(password).unwrap();
      toast.success("İki adımlı doğrulama kapatıldı.");
      close(false);
    } catch (err) {
      setError(errorMessage(err));
    }
  }

  function close(next: boolean) {
    if (!next) {
      setPassword("");
      setError(null);
    }
    onOpenChange(next);
  }

  return (
    <Dialog open={open} onOpenChange={close}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>İki adımlı doğrulamayı kapat</DialogTitle>
          <DialogDescription>Güvenlik için şifreni tekrar gir. Kapatınca girişte yalnızca şifre istenir.</DialogDescription>
        </DialogHeader>
        <form
          onSubmit={(e) => {
            e.preventDefault();
            setError(null);
            void disable();
          }}
          className="grid gap-4"
        >
          <Field label="Şifre" htmlFor="disable-password">
            <Input id="disable-password" type="password" autoComplete="current-password" required value={password} onChange={(e) => setPassword(e.target.value)} />
          </Field>
          {error && (
            <Alert variant="destructive">
              <AlertDescription>{error}</AlertDescription>
            </Alert>
          )}
          <DialogFooter>
            <Button type="button" variant="outline" onClick={() => close(false)}>
              Vazgeç
            </Button>
            <Button type="submit" variant="destructive" disabled={disabling || !password}>
              <ShieldOffIcon /> Kapat
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}

// ---------------------------------------------------------------- passkey'ler

/** Yeni passkey için önerilen ad (kullanıcı değiştirebilir). */
function suggestedPasskeyName(): string {
  const agent = navigator.userAgent;
  if (/iPhone/.test(agent)) return "iPhone";
  if (/iPad/.test(agent)) return "iPad";
  if (/Android/.test(agent)) return "Android telefon";
  if (/Macintosh/.test(agent)) return "Mac";
  if (/Windows/.test(agent)) return "Windows bilgisayar";
  return "Passkey";
}

function PasskeysCard({ security }: { security: AccountSecurity }) {
  const [addOpen, setAddOpen] = useState(false);
  const [renaming, setRenaming] = useState<PasskeyInfo | null>(null);
  const [deleting, setDeleting] = useState<PasskeyInfo | null>(null);

  const supported = passkeysSupported();

  const [registrationOptions] = usePasskeyRegistrationOptionsMutation();
  const [addPasskey] = useAddPasskeyMutation();
  const [renamePasskey] = useRenamePasskeyMutation();
  const [deletePasskey] = useDeletePasskeyMutation();

  async function remove(passkey: PasskeyInfo) {
    try {
      await deletePasskey(passkey.id).unwrap();
      toast.success(`“${passkey.name}” silindi.`);
    } catch (err) {
      toast.error(errorMessage(err));
    }
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle className="flex items-center gap-2">
          <FingerprintIcon className="size-5" /> Passkey'ler
        </CardTitle>
        <CardDescription>
          Şifre yerine Touch ID, Face ID, Windows Hello ya da telefonunla giriş yap. Passkey cihazında kalır; sunucuya yalnızca açık anahtar
          kaydedilir, oltalama sitelerinde çalışmaz.
        </CardDescription>
      </CardHeader>
      <CardContent className="grid gap-3">
        {!security.passkeysEnabled && (
          <Alert>
            <AlertDescription>
              Sunucuda passkey ayarlı değil (<code>Security:Passkey</code>).
            </AlertDescription>
          </Alert>
        )}
        {security.passkeysEnabled && !supported && (
          <Alert>
            <AlertDescription>Bu tarayıcı passkey desteklemiyor.</AlertDescription>
          </Alert>
        )}

        {security.passkeys.length === 0 ? (
          <p className="text-muted-foreground text-sm">Henüz passkey eklemedin.</p>
        ) : (
          <ul className="divide-y rounded-lg border">
            {security.passkeys.map((p) => (
              <li key={p.id} className="flex items-center gap-3 p-3">
                <KeyRoundIcon className="text-muted-foreground size-5 shrink-0" />
                <div className="min-w-0 flex-1">
                  <div className="flex items-center gap-2 font-medium">
                    <span className="truncate">{p.name}</span>
                    {p.isBackedUp && <Badge variant="secondary">Senkronize</Badge>}
                  </div>
                  <div className="text-muted-foreground text-xs">
                    Eklendi: {formatDateTime(p.createdAt)} · Son kullanım: {p.lastUsedAt ? formatDateTime(p.lastUsedAt) : "hiç"}
                  </div>
                </div>
                <Button size="icon" variant="ghost" title="Yeniden adlandır" onClick={() => setRenaming(p)}>
                  <PencilIcon />
                </Button>
                <Button size="icon" variant="ghost" title="Sil" onClick={() => setDeleting(p)}>
                  <Trash2Icon />
                </Button>
              </li>
            ))}
          </ul>
        )}

        {security.passkeysEnabled && supported && (
          <Button className="self-start" onClick={() => setAddOpen(true)}>
            <PlusIcon /> Passkey ekle
          </Button>
        )}
      </CardContent>

      <PasskeyNameDialog
        open={addOpen}
        onOpenChange={setAddOpen}
        title="Passkey ekle"
        description="Bu passkey'i listede tanıyabileceğin bir ad ver. Sonra cihazın parmak izi / yüz / PIN isteyecek."
        initialName={suggestedPasskeyName()}
        submitText="Devam"
        onSubmit={async (name) => {
          // seçenekler (sunucu) → cihazda passkey oluştur → yanıtı doğrulat ve kaydet (sunucu)
          const options = await registrationOptions().unwrap();
          const credential = await createPasskey(options);
          await addPasskey({ name, credential }).unwrap();
          toast.success("Passkey eklendi. Artık giriş sayfasında “Passkey ile giriş yap”ı kullanabilirsin.");
        }}
      />
      <PasskeyNameDialog
        open={!!renaming}
        onOpenChange={(open) => !open && setRenaming(null)}
        title="Passkey'i yeniden adlandır"
        initialName={renaming?.name ?? ""}
        submitText="Kaydet"
        onSubmit={async (name) => {
          await renamePasskey({ id: renaming!.id, name }).unwrap();
        }}
      />
      <ConfirmDialog
        open={!!deleting}
        onOpenChange={(open) => !open && setDeleting(null)}
        title="Passkey'i sil"
        description={`“${deleting?.name}” silinince o cihazla giriş yapılamaz. Cihazdaki kaydı da cihaz ayarlarından silebilirsin.`}
        confirmText="Sil"
        destructive
        onConfirm={() => (deleting ? remove(deleting) : undefined)}
      />
    </Card>
  );
}

function PasskeyNameDialog({
  open,
  onOpenChange,
  title,
  description,
  initialName,
  submitText,
  onSubmit,
}: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  title: string;
  description?: string;
  initialName: string;
  submitText: string;
  onSubmit: (name: string) => Promise<void>;
}) {
  const [name, setName] = useState(initialName);
  const [error, setError] = useState<string | null>(null);
  const [pending, setPending] = useState(false);
  const [lastOpen, setLastOpen] = useState(open);

  // Pencere her açıldığında alanı başlangıç değerine döndür.
  if (open !== lastOpen) {
    setLastOpen(open);
    if (open) {
      setName(initialName);
      setError(null);
    }
  }

  async function submit(e: FormEvent) {
    e.preventDefault();
    setError(null);
    setPending(true);
    try {
      await onSubmit(name.trim());
      onOpenChange(false);
    } catch (err) {
      setError(passkeyErrorMessage(err) ?? errorMessage(err));
    } finally {
      setPending(false);
    }
  }

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>{title}</DialogTitle>
          {description && <DialogDescription>{description}</DialogDescription>}
        </DialogHeader>
        <form onSubmit={submit} className="grid gap-4">
          <Field label="Ad" htmlFor="passkey-name">
            <Input id="passkey-name" maxLength={64} required value={name} onChange={(e) => setName(e.target.value)} />
          </Field>
          {error && (
            <Alert variant="destructive">
              <AlertDescription>{error}</AlertDescription>
            </Alert>
          )}
          <DialogFooter>
            <Button type="button" variant="outline" onClick={() => onOpenChange(false)} disabled={pending}>
              Vazgeç
            </Button>
            <Button type="submit" disabled={pending || !name.trim()}>
              {submitText}
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}
