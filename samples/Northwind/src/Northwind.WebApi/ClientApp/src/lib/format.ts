const money = new Intl.NumberFormat("tr-TR", { style: "currency", currency: "USD" });
const number = new Intl.NumberFormat("tr-TR");
const date = new Intl.DateTimeFormat("tr-TR", { dateStyle: "medium" });
const dateTime = new Intl.DateTimeFormat("tr-TR", { dateStyle: "medium", timeStyle: "short" });

export const formatMoney = (value: number) => money.format(value);
export const formatNumber = (value: number) => number.format(value);
export const formatDate = (value?: string | null) => (value ? date.format(new Date(value)) : "—");
export const formatDateTime = (value?: string | null) => (value ? dateTime.format(new Date(value)) : "—");

export function formatAddress(address?: { street: string; city: string; region?: string | null; postalCode?: string | null; country: string } | null) {
  if (!address) return "—";
  return [address.street, [address.postalCode, address.city].filter(Boolean).join(" "), address.region, address.country]
    .filter(Boolean)
    .join(", ");
}

export const initials = (first?: string, last?: string) => `${first?.[0] ?? ""}${last?.[0] ?? ""}`.toUpperCase() || "?";
