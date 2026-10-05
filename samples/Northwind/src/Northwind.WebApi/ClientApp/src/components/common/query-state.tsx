import type { ReactNode } from "react";
import { AlertCircleIcon, InboxIcon } from "lucide-react";

import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { Skeleton } from "@/components/ui/skeleton";
import { errorMessage } from "@/lib/api";

/** Yükleniyor / hata durumlarını tek yerden gösterir. */
export function QueryState({ isLoading, error, rows = 5 }: { isLoading: boolean; error: unknown; rows?: number }) {
  if (isLoading) {
    return (
      <div className="space-y-2">
        {Array.from({ length: rows }, (_, i) => (
          <Skeleton key={i} className="h-9 w-full" />
        ))}
      </div>
    );
  }

  if (error) {
    return (
      <Alert variant="destructive">
        <AlertCircleIcon />
        <AlertTitle>Veriler alınamadı</AlertTitle>
        <AlertDescription>{errorMessage(error)}</AlertDescription>
      </Alert>
    );
  }

  return null;
}

export function EmptyState({ title, description, action }: { title: string; description?: string; action?: ReactNode }) {
  return (
    <div className="flex flex-col items-center justify-center gap-2 rounded-lg border border-dashed p-10 text-center">
      <InboxIcon className="text-muted-foreground size-8" />
      <p className="font-medium">{title}</p>
      {description && <p className="text-muted-foreground text-sm">{description}</p>}
      {action}
    </div>
  );
}
