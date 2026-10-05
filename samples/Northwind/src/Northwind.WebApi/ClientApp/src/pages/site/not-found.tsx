import { Link } from "react-router";

import { Button } from "@/components/ui/button";

export function NotFoundPage() {
  return (
    <div className="mx-auto flex max-w-md flex-col items-center gap-4 px-4 py-24 text-center">
      <p className="text-primary text-5xl font-bold">404</p>
      <h1 className="text-xl font-semibold">Sayfa bulunamadı</h1>
      <Button asChild>
        <Link to="/">Ana sayfaya dön</Link>
      </Button>
    </div>
  );
}
