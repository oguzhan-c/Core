// Passkey (WebAuthn) yardımcıları.
// Sunucu seçenekleri JSON olarak verir (ikili alanlar base64url); tarayıcı API'si ArrayBuffer ister.
// Yanıt da aynı şekilde JSON'a çevrilip sunucuya gönderilir. Özel anahtar cihazdan hiç çıkmaz;
// sunucuya yalnızca imza ve açık anahtar gider.

type Json = Record<string, unknown>;

interface CredentialDescriptorJson {
  id: string;
  type: string;
  transports?: string[] | null;
}

export interface CreationOptionsJson extends Json {
  challenge: string;
  user: { id: string; name: string; displayName: string };
  excludeCredentials?: CredentialDescriptorJson[] | null;
}

export interface RequestOptionsJson extends Json {
  challenge: string;
  allowCredentials?: CredentialDescriptorJson[] | null;
}

/** Fido2'nin tanıdığı taşıma türleri; bilinmeyen bir değer sunucuda yanıtın okunamamasına yol açmasın. */
const knownTransports = new Set(["usb", "nfc", "ble", "smart-card", "hybrid", "internal"]);

export function passkeysSupported(): boolean {
  return typeof window !== "undefined" && typeof window.PublicKeyCredential === "function" && !!navigator.credentials;
}

export function base64UrlToBuffer(value: string): ArrayBuffer {
  const base64 = value.replace(/-/g, "+").replace(/_/g, "/");
  const binary = atob(base64.padEnd(Math.ceil(base64.length / 4) * 4, "="));
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
  return bytes.buffer;
}

export function bufferToBase64Url(buffer: ArrayBuffer): string {
  const bytes = new Uint8Array(buffer);
  let binary = "";
  for (const b of bytes) binary += String.fromCharCode(b);
  return btoa(binary).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
}

/** null alanları atar: tarayıcı API'si bazı alanlarda null'ı geçersiz enum değeri sayar. */
function stripNulls<T>(value: T): T {
  if (Array.isArray(value)) return value.map(stripNulls) as T;
  if (value && typeof value === "object") {
    const result: Json = {};
    for (const [key, item] of Object.entries(value as Json)) {
      if (item !== null && item !== undefined) result[key] = stripNulls(item);
    }
    return result as T;
  }
  return value;
}

function toDescriptors(list?: CredentialDescriptorJson[] | null): PublicKeyCredentialDescriptor[] {
  return (list ?? []).map((c) => ({
    type: "public-key",
    id: base64UrlToBuffer(c.id),
    ...(c.transports ? { transports: c.transports as AuthenticatorTransport[] } : {}),
  }));
}

/** Kullanıcının iptal etmesi gibi tarayıcı hatalarını okunur metne çevirir. */
export function passkeyErrorMessage(error: unknown): string | null {
  if (error instanceof DOMException) {
    switch (error.name) {
      case "NotAllowedError":
        return "Passkey işlemi iptal edildi ya da süresi doldu.";
      case "InvalidStateError":
        return "Bu cihazda bu hesap için zaten bir passkey var.";
      case "SecurityError":
        return "Bu adreste passkey kullanılamıyor (alan adı/origin sunucu ayarıyla eşleşmiyor).";
      case "NotSupportedError":
        return "Bu tarayıcı ya da cihaz passkey desteklemiyor.";
    }
  }
  return null;
}

/** Yeni passkey oluşturur (navigator.credentials.create) ve sunucuya gönderilecek JSON'u döndürür. */
export async function createPasskey(optionsJson: CreationOptionsJson): Promise<Json> {
  const options = stripNulls(optionsJson);
  const publicKey = {
    ...options,
    challenge: base64UrlToBuffer(options.challenge),
    user: { ...options.user, id: base64UrlToBuffer(options.user.id) },
    excludeCredentials: toDescriptors(options.excludeCredentials),
  } as unknown as PublicKeyCredentialCreationOptions;

  const credential = (await navigator.credentials.create({ publicKey })) as PublicKeyCredential | null;
  if (!credential) throw new Error("Passkey oluşturulmadı.");

  const response = credential.response as AuthenticatorAttestationResponse;
  const transports = (response.getTransports?.() ?? []).filter((t) => knownTransports.has(t));

  return stripNulls({
    id: credential.id,
    rawId: bufferToBase64Url(credential.rawId),
    type: credential.type,
    authenticatorAttachment: credential.authenticatorAttachment,
    response: {
      attestationObject: bufferToBase64Url(response.attestationObject),
      clientDataJSON: bufferToBase64Url(response.clientDataJSON),
      transports,
    },
    clientExtensionResults: credential.getClientExtensionResults(),
  });
}

/** Kayıtlı bir passkey ile imza alır (navigator.credentials.get) ve sunucuya gönderilecek JSON'u döndürür. */
export async function getPasskey(optionsJson: RequestOptionsJson): Promise<Json> {
  const options = stripNulls(optionsJson);
  const publicKey = {
    ...options,
    challenge: base64UrlToBuffer(options.challenge),
    allowCredentials: toDescriptors(options.allowCredentials),
  } as unknown as PublicKeyCredentialRequestOptions;

  const credential = (await navigator.credentials.get({ publicKey })) as PublicKeyCredential | null;
  if (!credential) throw new Error("Passkey seçilmedi.");

  const response = credential.response as AuthenticatorAssertionResponse;

  return stripNulls({
    id: credential.id,
    rawId: bufferToBase64Url(credential.rawId),
    type: credential.type,
    authenticatorAttachment: credential.authenticatorAttachment,
    response: {
      authenticatorData: bufferToBase64Url(response.authenticatorData),
      clientDataJSON: bufferToBase64Url(response.clientDataJSON),
      signature: bufferToBase64Url(response.signature),
      userHandle: response.userHandle ? bufferToBase64Url(response.userHandle) : null,
    },
    clientExtensionResults: credential.getClientExtensionResults(),
  });
}
