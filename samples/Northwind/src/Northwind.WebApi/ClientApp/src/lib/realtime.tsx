import { HubConnectionBuilder, HubConnectionState, LogLevel, type HubConnection } from "@microsoft/signalr";
import { useEffect } from "react";
import { toast } from "sonner";

import { useAppDispatch } from "@/app/hooks";
import { baseApi } from "@/lib/api";
import { useAuth } from "@/lib/auth";

/** Sunucunun gönderdiği anlık mesaj (Can.Core.Realtime.RealtimeMessage). */
interface RealtimeMessage<T = Record<string, unknown>> {
  type: string;
  data: T;
  createdAt: string;
}

/**
 * Giriş yapan kullanıcı için SignalR bağlantısını açar; sunucu kullanıcıyı mağazasının (tenant), kendi ve rollerinin
 * gruplarına ekler. Kimlik HttpOnly cookie'deki JWT ile gider (token JavaScript'e açılmaz).
 * Mesaj gelince kısa bir bildirim gösterilir ve ilgili liste önbelleği yenilenir.
 */
export function RealtimeConnector() {
  const { user } = useAuth();
  const dispatch = useAppDispatch();
  const userKey = user ? `${user.id}:${user.tenantId}` : null;

  useEffect(() => {
    if (!userKey) return;

    const connection: HubConnection = new HubConnectionBuilder()
      .withUrl("/hubs/notifications")
      .withAutomaticReconnect()
      .configureLogging(LogLevel.Warning)
      .build();

    connection.on("notify", (message: RealtimeMessage) => {
      const data = message.data as { orderNumber?: number; productName?: string; unitsInStock?: number };

      switch (message.type) {
        case "order.placed":
          toast.info(`Yeni sipariş #${data.orderNumber}`);
          dispatch(baseApi.util.invalidateTags(["Order", "Dashboard", "Product"]));
          break;
        case "order.shipped":
          toast.success(`Sipariş #${data.orderNumber} kargoya verildi`);
          dispatch(baseApi.util.invalidateTags(["Order", "MyOrder", "Dashboard"]));
          break;
        case "order.cancelled":
          toast.warning(`Sipariş #${data.orderNumber} iptal edildi`);
          dispatch(baseApi.util.invalidateTags(["Order", "Dashboard", "Product"]));
          break;
        case "stock.low":
          toast.warning(`"${data.productName}" stoğu kritik: ${data.unitsInStock}`);
          dispatch(baseApi.util.invalidateTags(["Product", "Dashboard"]));
          break;
      }
    });

    let stopped = false;
    connection.start().catch((error: unknown) => {
      // Sunucuda SignalR kapalıysa ya da oturum düştüyse sessizce vazgeç (uygulama çalışmaya devam eder).
      if (!stopped) console.warn("Anlık bildirim bağlantısı kurulamadı", error);
    });

    return () => {
      stopped = true;
      if (connection.state !== HubConnectionState.Disconnected) void connection.stop();
    };
  }, [userKey, dispatch]);

  return null;
}
