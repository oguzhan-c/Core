import { useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { MailIcon, RefreshCwIcon } from "lucide-react";

import { PageHeader } from "@/components/common/page-header";
import { EmptyState, QueryState } from "@/components/common/query-state";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { api } from "@/lib/api";
import { formatDateTime } from "@/lib/format";
import type { Mail } from "@/lib/types";
import { cn } from "@/lib/utils";

/**
 * YALNIZCA geliştirme: e-postalar SMTP yerine klasöre yazılır; bu sayfa onları gösterir
 * (kayıt doğrulama kodu, kargo bildirimi, yeniden sipariş raporu ...).
 */
export function MailboxPage() {
  const [selected, setSelected] = useState<string | null>(null);
  const mails = useQuery({ queryKey: ["dev-mailbox"], queryFn: () => api<Mail[]>("/api/dev/mailbox"), refetchInterval: 5000 });
  const mail = mails.data?.find((m) => m.id === selected) ?? mails.data?.[0];

  return (
    <div className="mx-auto max-w-6xl space-y-6 px-4 py-8">
      <PageHeader
        title="Geliştirme posta kutusu"
        description="Uygulamanın gönderdiği e-postalar (Mail:PickupDirectory). Yalnızca Development ortamında açıktır."
        actions={
          <Button variant="outline" size="sm" onClick={() => mails.refetch()}>
            <RefreshCwIcon /> Yenile
          </Button>
        }
      />
      <QueryState isLoading={mails.isLoading} error={mails.error} />
      {mails.data?.length === 0 && <EmptyState title="Henüz e-posta yok" description="Kayıt ol ya da bir siparişi kargoya ver." />}
      {!!mails.data?.length && (
        <div className="grid gap-4 md:grid-cols-[320px_1fr]">
          <Card className="py-2">
            <CardContent className="divide-y px-0">
              {mails.data.map((m) => (
                <button
                  key={m.id}
                  onClick={() => setSelected(m.id)}
                  className={cn("w-full px-4 py-3 text-left text-sm", mail?.id === m.id ? "bg-accent" : "hover:bg-accent/50")}
                >
                  <p className="line-clamp-1 font-medium">{m.subject}</p>
                  <p className="text-muted-foreground line-clamp-1 text-xs">{m.to}</p>
                  <p className="text-muted-foreground text-xs">{formatDateTime(m.date)}</p>
                </button>
              ))}
            </CardContent>
          </Card>
          {mail && (
            <Card>
              <CardHeader>
                <CardTitle className="flex items-center gap-2">
                  <MailIcon className="size-4" /> {mail.subject}
                </CardTitle>
                <CardDescription>
                  {mail.from} → {mail.to} · {formatDateTime(mail.date)}
                </CardDescription>
              </CardHeader>
              <CardContent>
                {mail.html ? (
                  <iframe title="E-posta" sandbox="" srcDoc={mail.html} className="h-96 w-full rounded-md border bg-white" />
                ) : (
                  <pre className="bg-muted rounded-md p-4 text-sm whitespace-pre-wrap">{mail.text}</pre>
                )}
              </CardContent>
            </Card>
          )}
        </div>
      )}
    </div>
  );
}
