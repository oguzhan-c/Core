import { PlusIcon, Trash2Icon, FolderPlusIcon } from "lucide-react";

import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { cn } from "@/lib/utils";
import type { Filter } from "@/lib/types";

export interface FieldOption {
  value: string;
  label: string;
  type: "string" | "number" | "boolean" | "date" | "enum";
  options?: string[];
}

export const operatorLabels: Record<string, string> = {
  eq: "eşittir",
  neq: "eşit değildir",
  lt: "küçüktür",
  lte: "küçük/eşit",
  gt: "büyüktür",
  gte: "büyük/eşit",
  contains: "içerir",
  doesnotcontain: "içermez",
  startswith: "ile başlar",
  endswith: "ile biter",
  in: "listede (a,b,c)",
  between: "arasında (alt,üst)",
  isnull: "boş",
  isnotnull: "dolu",
};

const operatorsByType: Record<FieldOption["type"], string[]> = {
  string: ["contains", "eq", "neq", "startswith", "endswith", "doesnotcontain", "in", "isnull", "isnotnull"],
  number: ["eq", "neq", "gt", "gte", "lt", "lte", "between", "in"],
  date: ["gte", "lte", "between", "eq", "isnull", "isnotnull"],
  boolean: ["eq"],
  enum: ["eq", "neq", "in"],
};

const noValue = ["isnull", "isnotnull"];

export const emptyGroup = (): Filter => ({ logic: "and", filters: [] });

/**
 * DynamicQuery filtre ağacını görsel olarak kurar: her grup "ve"/"veya" ile birleşir, gruplar iç içe olabilir.
 * Sonuç doğrudan backend'in Filter modeline karşılık gelir.
 */
export function FilterBuilder({ value, onChange, fields }: { value: Filter; onChange: (filter: Filter) => void; fields: FieldOption[] }) {
  return <GroupEditor group={value} onChange={onChange} fields={fields} depth={0} />;
}

function GroupEditor({
  group,
  onChange,
  onRemove,
  fields,
  depth,
}: {
  group: Filter;
  onChange: (filter: Filter) => void;
  onRemove?: () => void;
  fields: FieldOption[];
  depth: number;
}) {
  const children = group.filters ?? [];
  const setChildren = (filters: Filter[]) => onChange({ ...group, filters });
  const updateChild = (i: number, child: Filter) => setChildren(children.map((c, j) => (j === i ? child : c)));
  const removeChild = (i: number) => setChildren(children.filter((_, j) => j !== i));
  const first = fields[0];

  return (
    <div className={cn("space-y-2 rounded-lg border p-3", depth % 2 === 0 ? "bg-muted/30" : "bg-background")}>
      <div className="flex flex-wrap items-center gap-2">
        <div className="bg-muted inline-flex rounded-md p-0.5">
          {(["and", "or"] as const).map((logic) => (
            <button
              key={logic}
              type="button"
              onClick={() => onChange({ ...group, logic })}
              className={cn(
                "rounded px-2.5 py-1 text-xs font-medium",
                (group.logic ?? "and") === logic ? "bg-background shadow-sm" : "text-muted-foreground"
              )}
            >
              {logic === "and" ? "VE (hepsi)" : "VEYA (biri)"}
            </button>
          ))}
        </div>
        <Button
          type="button"
          variant="outline"
          size="sm"
          onClick={() => setChildren([...children, { field: first.value, operator: operatorsByType[first.type][0], value: "" }])}
        >
          <PlusIcon /> Koşul
        </Button>
        <Button type="button" variant="outline" size="sm" onClick={() => setChildren([...children, emptyGroup()])}>
          <FolderPlusIcon /> Grup
        </Button>
        {onRemove && (
          <Button type="button" variant="ghost" size="sm" className="ml-auto" onClick={onRemove}>
            <Trash2Icon /> Grubu sil
          </Button>
        )}
      </div>

      {children.length === 0 && <p className="text-muted-foreground px-1 text-xs">Koşul yok — tüm kayıtlar gelir.</p>}

      {children.map((child, i) =>
        child.field ? (
          <ConditionEditor key={i} condition={child} fields={fields} onChange={(c) => updateChild(i, c)} onRemove={() => removeChild(i)} />
        ) : (
          <GroupEditor
            key={i}
            group={child}
            fields={fields}
            depth={depth + 1}
            onChange={(c) => updateChild(i, c)}
            onRemove={() => removeChild(i)}
          />
        )
      )}
    </div>
  );
}

function ConditionEditor({
  condition,
  fields,
  onChange,
  onRemove,
}: {
  condition: Filter;
  fields: FieldOption[];
  onChange: (filter: Filter) => void;
  onRemove: () => void;
}) {
  const field = fields.find((f) => f.value === condition.field) ?? fields[0];
  const operators = operatorsByType[field.type];
  const operator = condition.operator && operators.includes(condition.operator) ? condition.operator : operators[0];

  return (
    <div className="bg-background flex flex-wrap items-center gap-2 rounded-md border p-2">
      <Select
        value={field.value}
        onValueChange={(value) => {
          const next = fields.find((f) => f.value === value)!;
          onChange({ field: value, operator: operatorsByType[next.type][0], value: "" });
        }}
      >
        <SelectTrigger size="sm" className="min-w-40">
          <SelectValue />
        </SelectTrigger>
        <SelectContent>
          {fields.map((f) => (
            <SelectItem key={f.value} value={f.value}>
              {f.label}
            </SelectItem>
          ))}
        </SelectContent>
      </Select>

      <Select value={operator} onValueChange={(op) => onChange({ ...condition, operator: op })}>
        <SelectTrigger size="sm" className="min-w-36">
          <SelectValue />
        </SelectTrigger>
        <SelectContent>
          {operators.map((op) => (
            <SelectItem key={op} value={op}>
              {operatorLabels[op] ?? op}
            </SelectItem>
          ))}
        </SelectContent>
      </Select>

      {!noValue.includes(operator) &&
        (field.type === "boolean" ? (
          <Select value={condition.value || "true"} onValueChange={(v) => onChange({ ...condition, operator, value: v })}>
            <SelectTrigger size="sm" className="w-28">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              <SelectItem value="true">evet</SelectItem>
              <SelectItem value="false">hayır</SelectItem>
            </SelectContent>
          </Select>
        ) : field.type === "enum" && operator !== "in" ? (
          <Select value={condition.value || field.options?.[0]} onValueChange={(v) => onChange({ ...condition, operator, value: v })}>
            <SelectTrigger size="sm" className="w-36">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              {field.options?.map((o) => (
                <SelectItem key={o} value={o}>
                  {o}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        ) : (
          <Input
            className="h-8 w-56"
            placeholder={placeholder(field.type, operator)}
            value={condition.value ?? ""}
            onChange={(e) => onChange({ ...condition, operator, value: e.target.value })}
          />
        ))}

      <Button type="button" variant="ghost" size="icon-sm" className="ml-auto" onClick={onRemove} aria-label="Koşulu sil">
        <Trash2Icon />
      </Button>
    </div>
  );
}

function placeholder(type: FieldOption["type"], operator: string) {
  if (operator === "between") return type === "date" ? "1997-01-01,1997-12-31" : "10,50";
  if (operator === "in") return "a,b,c";
  if (type === "date") return "1997-01-01";
  if (type === "number") return "0";
  return "değer";
}

/**
 * Boş grupları ve değeri girilmemiş koşulları atar; boolean/enum için varsayılan değeri yazar
 * (görselde seçili görünen değer sunucuya da gitsin).
 */
export function cleanFilter(filter: Filter, fields: FieldOption[]): Filter | null {
  if (filter.field) {
    const field = fields.find((f) => f.value === filter.field);
    const operator = filter.operator ?? "eq";
    if (noValue.includes(operator)) return { field: filter.field, operator };

    let value = filter.value ?? "";
    if (!value && field?.type === "boolean") value = "true";
    if (!value && field?.type === "enum") value = field.options?.[0] ?? "";
    if (field?.type === "date" && value) value = value.split(",").map(toUtc).join(",");
    return value ? { field: filter.field, operator, value } : null;
  }

  const children = (filter.filters ?? []).map((f) => cleanFilter(f, fields)).filter((f): f is Filter => f !== null);
  return children.length ? { logic: filter.logic ?? "and", filters: children } : null;
}

/** "1997-01-01" → "1997-01-01T00:00:00Z" (sunucudaki tarih alanları UTC). */
function toUtc(date: string) {
  const trimmed = date.trim();
  return /^\d{4}-\d{2}-\d{2}$/.test(trimmed) ? `${trimmed}T00:00:00Z` : trimmed;
}
