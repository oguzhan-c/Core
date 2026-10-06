# Northwind ClientApp

React 19 + Vite + Tailwind CSS v4 + [shadcn/ui](https://ui.shadcn.com) (new-york) + [Redux Toolkit / RTK Query](https://redux-toolkit.js.org/rtk-query/overview).
Mağaza sitesi ve yönetim paneli tek uygulamada.

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
| `src/app` | Redux store (`configureStore`) ve tipli `useAppDispatch` / `useAppSelector` |
| `src/lib` | RTK Query temeli (`api.ts`: cookie + otomatik token yenileme, hata dönüşümü), oturum (`useAuth`), sepet slice'ı (`useStore`), tipler |
| `src/services` | RTK Query endpoint'leri: `auth`, `storefront`, `catalog`, `sales`, `admin`, `account` |
| `src/pages/site` | ana sayfa, katalog, ürün, sepet, siparişlerim, geliştirme posta kutusu |
| `src/pages/auth` | giriş, kayıt, e-posta doğrulama |
| `src/pages/admin` | özet, ürünler, kategoriler, siparişler, müşteriler, filtre laboratuvarı, değişiklik geçmişi, outbox, kullanıcılar |

## Veri katmanı: RTK Query

Tek bir `createApi` (`lib/api.ts`) var; her özellik dosyası ona `injectEndpoints` ile endpoint ekler ve hazır hook'ları
dışa verir (`useGetProductsQuery`, `useCheckoutMutation` ...). Sayfalar `fetch` ya da `api(...)` çağırmaz.

- **Önbellek etiketleri:** Sorgular `providesTags`, komutlar `invalidatesTags` bildirir. Örneğin siparişi kargoya vermek
  `Order`, `Product`, `Report`, `Outbox`, `AuditLog` etiketlerini geçersiz kılar; açık olan listeler kendiliğinden yenilenir,
  sayfalarda elle yenileme yoktur.
- **Oturum:** `/api/auth/me` sonucu önbellekte (`useAuth`). 401 gelince bir kez `/api/auth/refresh` denenir (eşzamanlı
  istekler tek yenilemeyi bekler); yenilenemezse kullanıcı çıkış yapmış sayılır. Girişte kullanıcıya özel veriler yeniden
  yüklenir (başka kullanıcının önbelleği görünmez).
- **Hatalar:** Sunucunun ProblemDetails'i düz bir `ApiError` nesnesine çevrilir (`status`, `code`, alan hataları);
  `errorMessage(e)` kullanıcıya gösterilecek metni, `isApiError(e) && e.code === "..."` belirli hatayı ayırır.
- **Sayfalama:** Parametre değişirken önceki sayfa ekranda kalır (`data`), `isFetching` ile soluklaşır.
- **Sepet:** `storefront` slice'ı (mağaza başına sepet), değişiklikler `localStorage`'a yazılır.
- **Diğer:** Outbox ve posta kutusu `pollingInterval` ile yenilenir; filtre laboratuvarı `useLazySearchQuery` kullanır;
  rapor gibi yetki gerektiren sorgular yetki yoksa `skip` ile hiç gönderilmez.
