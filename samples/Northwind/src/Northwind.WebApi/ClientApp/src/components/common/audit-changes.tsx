import { Fragment } from "react";

interface Change {
  old?: unknown;
  new?: unknown;
}

function show(value: unknown) {
  if (value === null || value === undefined) return <span className="text-muted-foreground italic">boş</span>;
  if (typeof value === "object") return <code className="text-xs">{JSON.stringify(value)}</code>;
  return String(value);
}

/** AuditLog.changes JSON'ını alan / eski / yeni tablosu olarak gösterir. */
export function AuditChanges({ changes }: { changes?: string | null }) {
  if (!changes) return <span className="text-muted-foreground text-xs">Değişen alan yok</span>;

  let parsed: Record<string, Change>;
  try {
    parsed = JSON.parse(changes) as Record<string, Change>;
  } catch {
    return <code className="text-xs break-all">{changes}</code>;
  }

  return (
    <div className="grid grid-cols-[auto_1fr_auto_1fr] items-baseline gap-x-2 gap-y-1 text-xs">
      {Object.entries(parsed).map(([field, change]) => (
        <Fragment key={field}>
          <span className="font-medium">{field}</span>
          <span className="text-muted-foreground line-through decoration-rose-400/60">{"old" in change ? show(change.old) : "—"}</span>
          <span className="text-muted-foreground">→</span>
          <span className="text-emerald-700 dark:text-emerald-400">{"new" in change ? show(change.new) : "—"}</span>
        </Fragment>
      ))}
    </div>
  );
}
