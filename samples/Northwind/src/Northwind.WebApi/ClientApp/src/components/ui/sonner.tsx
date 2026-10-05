import type * as React from "react";
import { Toaster as Sonner, type ToasterProps } from "sonner";

/** shadcn/ui sonner sarmalayıcısı (next-themes olmadan; tema açık). */
function Toaster(props: ToasterProps) {
  return (
    <Sonner
      theme="light"
      className="toaster group"
      richColors
      closeButton
      style={
        {
          "--normal-bg": "var(--popover)",
          "--normal-text": "var(--popover-foreground)",
          "--normal-border": "var(--border)",
        } as React.CSSProperties
      }
      {...props}
    />
  );
}

export { Toaster };
