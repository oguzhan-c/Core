# Northwind ClientApp

React 19 + Vite + Tailwind CSS v4 + [shadcn/ui](https://ui.shadcn.com) (new-york). Mağaza sitesi ve yönetim paneli tek uygulamada.

```bash
npm install
npm run dev        # http://localhost:5173 — /api istekleri .NET uygulamasına (5180) aktarılır
npm run build      # tip kontrolü + ../wwwroot'a derleme (.NET uygulaması sunar)
```

Önce .NET uygulamasını başlat: `dotnet run --project ../ --launch-profile http`.

Yeni shadcn bileşeni eklemek için: `npx shadcn@latest add <bileşen>` (`components.json` hazır).

| Klasör | İçerik |
|---|---|
| `src/components/ui` | shadcn bileşenleri |
| `src/components/common` | sayfalama, filtre oluşturucu, değişiklik geçmişi, durum rozetleri |
| `src/lib` | API istemcisi (cookie + otomatik token yenileme), oturum, sepet, tipler |
| `src/pages/site` | ana sayfa, katalog, ürün, sepet, siparişlerim, geliştirme posta kutusu |
| `src/pages/auth` | giriş, kayıt, e-posta doğrulama |
| `src/pages/admin` | özet, ürünler, kategoriler, siparişler, müşteriler, filtre laboratuvarı, değişiklik geçmişi, outbox, kullanıcılar |
