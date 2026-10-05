import { useQuery } from "@tanstack/react-query";

import { Field } from "@/components/common/field";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { api } from "@/lib/api";
import type { StoreTenant } from "@/lib/types";

/** Giriş/kayıt formlarında mağaza seçimi (mağaza istemciden yalnızca bu akışlarda alınır). */
export function TenantSelect({ value, onChange }: { value: string; onChange: (value: string) => void }) {
  const tenants = useQuery({ queryKey: ["store", "tenants"], queryFn: () => api<StoreTenant[]>("/api/store/tenants"), staleTime: Infinity });

  return (
    <Field label="Mağaza">
      <Select value={value} onValueChange={onChange}>
        <SelectTrigger className="w-full">
          <SelectValue placeholder="Mağaza seç" />
        </SelectTrigger>
        <SelectContent>
          {tenants.data?.map((t) => (
            <SelectItem key={t.identifier} value={t.identifier}>
              {t.name}
            </SelectItem>
          ))}
        </SelectContent>
      </Select>
    </Field>
  );
}
