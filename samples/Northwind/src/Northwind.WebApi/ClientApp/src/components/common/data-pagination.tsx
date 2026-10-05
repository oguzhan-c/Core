import { ChevronLeftIcon, ChevronRightIcon } from "lucide-react";

import { Button } from "@/components/ui/button";
import { formatNumber } from "@/lib/format";
import type { Paginate } from "@/lib/types";

/** Sunucudan gelen sayfa bilgisine (IPaginate) göre sayfa düğmeleri. */
export function DataPagination<T>({ page, onPageChange }: { page?: Paginate<T>; onPageChange: (index: number) => void }) {
  if (!page || page.count === 0) return null;

  const first = page.index * page.size + 1;
  const last = Math.min(page.count, first + page.items.length - 1);

  return (
    <div className="flex flex-col items-center justify-between gap-2 sm:flex-row">
      <p className="text-muted-foreground text-sm">
        {formatNumber(page.count)} kayıttan {formatNumber(first)}–{formatNumber(last)} arası · Sayfa {page.index + 1} / {page.pages}
      </p>
      <div className="flex items-center gap-1">
        <Button variant="outline" size="sm" disabled={!page.hasPrevious} onClick={() => onPageChange(page.index - 1)}>
          <ChevronLeftIcon /> Önceki
        </Button>
        {pageNumbers(page.index, page.pages).map((n, i) =>
          n === null ? (
            <span key={`gap-${i}`} className="text-muted-foreground px-1">
              …
            </span>
          ) : (
            <Button key={n} variant={n === page.index ? "default" : "ghost"} size="icon-sm" onClick={() => onPageChange(n)}>
              {n + 1}
            </Button>
          )
        )}
        <Button variant="outline" size="sm" disabled={!page.hasNext} onClick={() => onPageChange(page.index + 1)}>
          Sonraki <ChevronRightIcon />
        </Button>
      </div>
    </div>
  );
}

/** 0 tabanlı sayfa numaraları; aradaki boşluklar null. */
function pageNumbers(current: number, total: number): (number | null)[] {
  if (total <= 7) return Array.from({ length: total }, (_, i) => i);
  const pages = new Set([0, total - 1, current - 1, current, current + 1].filter((n) => n >= 0 && n < total));
  const sorted = [...pages].sort((a, b) => a - b);
  const result: (number | null)[] = [];
  sorted.forEach((n, i) => {
    if (i > 0 && n - sorted[i - 1] > 1) result.push(null);
    result.push(n);
  });
  return result;
}
