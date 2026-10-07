/**
 * Dış sağlayıcıyla giriş (Google, Microsoft, GitHub). Akış tam sayfa yönlendirmedir (fetch değil): tarayıcı sağlayıcıya
 * gider, dönüşte sunucu token'ları HttpOnly cookie'ye yazar ve sayfaya geri yönlendirir. Hata olursa adrese
 * `?externalError=kod` eklenir.
 */
const base = "/api/auth/external";

export function externalLoginUrl(provider: string, tenant: string, returnUrl?: string | null): string {
  const query = new URLSearchParams({ tenant });
  if (returnUrl) query.set("returnUrl", returnUrl);
  return `${base}/${encodeURIComponent(provider)}/login?${query}`;
}

/** Giriş yapmış kullanıcının hesabına bağlar; dönüşte `?externalLinked=provider` eklenir. */
export function externalLinkUrl(provider: string, returnUrl: string): string {
  return `${base}/${encodeURIComponent(provider)}/link?${new URLSearchParams({ returnUrl })}`;
}

const messages: Record<string, string> = {
  denied: "Sağlayıcıda giriş iptal edildi ya da izin verilmedi.",
  failed: "Dış hesapla işlem tamamlanamadı; tekrar dene.",
  tenant_required: "Önce mağaza seç.",
  external_email_not_verified:
    "Sağlayıcı doğrulanmış bir e-posta vermedi. Şifrenle giriş yapıp hesabı Hesap güvenliği sayfasından bağlayabilirsin.",
  external_already_linked: "Bu dış hesap bu mağazada başka bir kullanıcıya bağlı.",
  external_provider_linked: "Bu sağlayıcıda zaten bağlı bir hesabın var; önce onu kaldır.",
  email_not_confirmed: "E-posta adresin henüz doğrulanmadı.",
  email_taken: "Bu e-posta adresiyle kayıtlı bir hesap var.",
  locked_out: "Çok fazla hatalı deneme yapıldı; hesap geçici olarak kilitlendi.",
  "store.not_found": "Mağaza bulunamadı.",
};

export function externalErrorMessage(code: string | null): string | null {
  if (!code) return null;
  return messages[code] ?? messages.failed;
}
