
import { Field } from "@/components/common/field";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { useGetStoreTenantsQuery } from "@/services/storefront";

/** Giriş/kayıt formlarında mağaza seçimi (mağaza istemciden yalnızca bu akışlarda alınır). */
export function TenantSelect({ value, onChange }: { value: string; onChange: (value: string) => void }) {
  const tenants = useGetStoreTenantsQuery();

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
