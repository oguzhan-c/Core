# Northwind Starter

Can.Core paketleriyle yazılmış örnek bir e-ticaret uygulaması: .NET 10 API + React 19 / shadcn arayüzü
(mağaza sitesi ve yönetim paneli). Klasik **Northwind** verisi (ürünler, müşteriler,
siparişler, çalışanlar, tedarikçiler) DDD ile yeniden modellendi ve PostgreSQL'de code-first olarak kuruluyor.
Tek veritabanında birden fazla mağaza (tenant) çalışır: `northwind` mağazası Northwind verisiyle, `demo` mağazası boş açılır.

## Çalıştırma

```bash
cd samples/Northwind
docker compose up -d                      # PostgreSQL (localhost:5432) + Redis (6379); Homebrew Postgres varsa gerekmez
dotnet run --project src/Northwind.WebApi --launch-profile http

# ayrı bir terminalde arayüz
cd src/Northwind.WebApi/ClientApp
npm install
npm run dev                               # http://localhost:5173
```

Arayüz geliştirme sunucusu `/api` isteklerini .NET uygulamasına aktarır. `npm run build` çıktıyı `wwwroot`'a yazar;
o zaman her şey tek adresten (<http://localhost:5180>) çalışır. `dotnet publish` arayüzü kendisi derler.

| Adres | |
|---|---|
| `/` | Mağaza: katalog, sepet, kayıt + e-posta doğrulama, siparişlerim |
| `/account/security` | Hesap güvenliği: iki adımlı doğrulama (authenticator / e-posta), passkey'ler, bağlı hesaplar |
| `/admin` | Yönetim paneli (Admin/Sales/Warehouse) |
| `/dev/mailbox` | Geliştirme posta kutusu: doğrulama kodları ve bildirimler |
| `/swagger`, `/scalar` | API belgesi (yalnızca Development) |
| `/hangfire` | Arka plan işleri dashboard'u (yalnızca Admin, `Hangfire:Enabled`) |

Homebrew ile kurulu Postgres kullanıyorsan veritabanı kullanıcısını bir kez oluştur:

```bash
psql postgres -c "CREATE ROLE northwind LOGIN PASSWORD 'northwind' CREATEDB;"
psql postgres -c "CREATE DATABASE northwind_can OWNER northwind;"
```

İlk açılışta şema oluşturulur, roller ve bölgeler eklenir, her mağazaya demo kullanıcılar açılır ve `northwind`
mağazasına Northwind verisi (77 ürün, 91 müşteri, 830 sipariş) aktarılır. API belgesi: <http://localhost:5180/swagger> (ya da <http://localhost:5180/scalar>).

Geliştirmede http kullanılır; bu yüzden `appsettings.Development.json` cookie'lerin `Secure` bayrağını kapatır.
https ile çalışmak için önce `dotnet dev-certs https --trust`, sonra `--launch-profile https`.

| Kullanıcı | Rol | Yapabildikleri |
|---|---|---|
| `admin@northwind.local` | Admin | her şey (ürün/fiyat/kategori, denetim kayıtları, outbox, kullanıcılar) |
| `sales@northwind.local` | Sales | müşteri, sipariş, raporlar |
| `warehouse@northwind.local` | Warehouse | stok girişi, kargoya verme |
| `customer@northwind.local` | Customer | site: sepet, sipariş, kendi siparişleri (ALFKI müşterisine bağlı) |

Siteden de kayıt olunabilir: kayıttan sonra e-postaya 6 haneli kod gider (geliştirmede `/dev/mailbox`), kod girilince
hesap doğrulanır ve oturum açılır.

Şifre (yalnızca geliştirme): `appsettings.Development.json` → `Seed:DemoUserPassword`. `demo` mağazası için
`admin@demo.local` vb. aynı şifreyle.

## Kimlik doğrulama: cookie'deki JWT

```bash
curl -c jar -X POST http://localhost:5180/api/auth/login \
  -H 'Content-Type: application/json' \
  -d '{"tenant":"northwind","email":"sales@northwind.local","password":"Northwind.2026!"}'

curl -b jar "http://localhost:5180/api/orders?size=5"
```

- Token'lar yanıt gövdesinde dönmez; `HttpOnly`, `Secure`, `SameSite=Strict` cookie olarak yazılır.
- Mağaza istemciden **yalnızca girişte** alınır. Sonraki isteklerde mağaza ve kullanıcı yalnızca imzalı token'dan
  okunur; header ile başka mağazanın verisi istenemez.
- `POST /api/auth/refresh` refresh token'ı döndürür (rotasyon); eski bir token tekrar kullanılırsa kullanıcının tüm
  oturumları kapatılır. `POST /api/auth/logout` çıkış. Arayüz 401 alınca yenilemeyi kendisi dener.
- Herkese açık katalog (`/api/store/{mağaza}/products`) mağazayı adresten alır; yalnızca okunur, herkese açık verilerdir.
  Müşteri işlemleri (`/api/store/my/...`) mağaza ve müşteriyi token'dan bulur; istemci müşteri kimliği gönderemez.

## Mimari

```
src/
  Northwind.Domain          Aggregate'ler ve kuralları (Product, Order, Customer ...), domain event'ler
  Northwind.Application     Command/query'ler, doğrulama, iş kuralları, event handler'lar, arka plan işi
  Northwind.Infrastructure  EF Core + PostgreSQL, tablo yapılandırmaları, seed
  Northwind.WebApi          Minimal API endpoint'leri, kimlik doğrulama, OpenAPI
tests/
  Northwind.Domain.Tests          aggregate kuralları
  Northwind.Infrastructure.Tests  Northwind SQL okuyucusu
```

Can.Core'dan kullanılanlar ve starter'daki örnekleri:

| Özellik | Nerede |
|---|---|
| Aggregate + domain event | `Order.AddLine` stoğu düşer, `Product.ReserveStock` kritik seviyede `StockBelowReorderLevel` üretir |
| Aynı transaction'da event handler | `CancelOrderCommand` → `OrderCancelled` → `ReleaseStockWhenOrderCancelled` stoğu geri koyar |
| Outbox (kaybolmayan event) | `Order.Ship` → `OrderShipped` → `NotifyWhenOrderShipped` e-posta gönderir; hata olursa tekrar dener |
| Değişiklik geçmişi | `[Audited]` Product/Customer/Order/AppUser; panelde "Değişiklik geçmişi" ve kayıt detayları |
| E-posta doğrulama | Kayıt → `EmailAuthenticator` + HMAC'li 6 haneli kod; 10 dk, 5 deneme; hata kodu `email_not_confirmed` |
| Hata kodları | ProblemDetails `code` alanı (`email_not_confirmed`, `invalid_verification_code`, `address_required`) |
| Çoklu mağaza (tenant) | Tüm aggregate'ler `TenantAggregateRoot`; sorgular otomatik filtrelenir |
| Pipeline | yetki (`ISecuredRequest`), doğrulama (FluentValidation), transaction, önbellek (raporlar, kategoriler) |
| Dinamik sorgu | `POST /api/{products,customers,orders}/search`; panelde iç içe VE/VEYA gruplu "Filtre laboratuvarı" |
| Mapper | `ProjectTo<SupplierDto>` adresi düzleştirerek SQL'e çevirir |
| Arka plan işi | `ReorderReportJob`: her mağaza için günlük rapor; panelden `IBackgroundJobQueue` ile hemen çalıştırılabilir |
| Yetkiler | Roller + ince taneli yetkiler: `orders.create`, `orders.cancel` (Sales), `orders.ship`, `products.stock` (Warehouse). Kargo/iptal/stok komutları ve panel düğmeleri yetkiye göre |
| Hangfire | `Hangfire:Enabled=true` ise işler PostgreSQL'de (`hangfire` şeması) kalıcı kuyrukta; rapor cron ile (`Hangfire:ReorderReportCron`, UTC) her mağaza için ayrı iş olarak çalışır. Kapalıysa bellek içi kuyruk |
| Anlık bildirim | SignalR: yeni/iptal sipariş ve kritik stok depoya, kargo satışa ve siparişin sahibi müşteriye; panel listeleri kendiliğinden yenilenir |
| İzleme | `OpenTelemetry:Enabled=true` ise istekler, SQL (Npgsql), domain event'ler, outbox, işler, e-posta ve dış HTTP çağrıları tek iz olarak `docker compose up -d dashboard` → <http://localhost:18888> |
| Önbellek | HybridCache; `Redis:ConnectionString` doluysa (ör. `localhost:6379`) Redis ikinci katman olur |
| E-posta | `Mail:Provider`: `Pickup` (.eml klasörü), `Smtp` (MailKit) ya da `SendGrid` |
| Outbox izleme | Panelde "Outbox & işler": bekleyen/yayınlanan/hatalı mesajlar, yeniden deneme |
| Seed | `ReferenceDataSeeder` (host), `DemoUserSeeder` ve `NorthwindDataSeeder` (mağaza başına) |

Geliştirmede e-postalar `src/Northwind.WebApi/mails/` klasörüne `.eml` olarak yazılır ve `/dev/mailbox` sayfasında görünür.

## Migration

Starter, migration yoksa şemayı `EnsureCreated` ile kurar. Kalıcı ortamlar için migration oluştur:

```bash
dotnet tool install --global dotnet-ef
dotnet ef migrations add Initial \
  --project src/Northwind.Infrastructure --startup-project src/Northwind.WebApi \
  --output-dir Persistence/Migrations
```

Bundan sonra uygulama açılışta migration'ları uygular (`Database:InitializeOnStartup`). Migration'lardan önce
oluşturulmuş bir geliştirme veritabanını silip (`docker compose down -v`) yeniden başlat.

## Hatalar: Result pattern

Domain metotları, handler'lar ve endpoint'ler exception yerine `Result<T>` kullanır (bkz. kök README → Result):

- Aggregate'ler kurallarını `Result<Success>` / `Result<T>` ile döndürür; hata katalogları aggregate'in yanında:
  `ProductErrors`, `OrderErrors`, `CustomerErrors`, `CategoryErrors`, `AuthErrors`.
- Alan kontrolleri (`Check.Required/Optional/Positive ...`) `Error?` döner ve `Result.Validate(...)` ile **tüm** hatalar
  birlikte toplanır (ör. sipariş numarası, kargo ücreti ve alıcı aynı anda hatalıysa üçü de döner).
- Her istek `IRequest<Result<T>>`; endpoint'ler `sender.Send(...).ToHttpResult()`. Başarısız komutta transaction geri
  alınır: sepetteki ürünlerden biri stokta yoksa hiçbir ürünün stoğu düşmez.
- Exception yalnızca beklenmeyen durumlarda: seed verisinin bozuk olması (`ThrowIfFailure()`), eksik rol gibi kurulum hataları.

## İki adımlı doğrulama ve passkey

Giriş yaptıktan sonra kullanıcı menüsü → **Hesap güvenliği** (`/account/security`):

| | Nasıl çalışır |
|---|---|
| Authenticator uygulaması (TOTP) | QR kodu okutulur, ilk kod girilince açılır. Gizli anahtar `OtpAuthenticator`'da; aynı kod iki kez kabul edilmez |
| E-posta ile kod | Her girişte e-postaya 6 haneli kod gider (`EmailAuthenticator`, 10 dk, 5 deneme) |
| Passkey | Touch ID / Face ID / Windows Hello / telefon. Sunucuda yalnızca açık anahtar (`UserPasskey`); şifresiz giriş |

İki adımlı doğrulama açıkken `POST /api/auth/login` oturum açmaz, `{ "twoFactor": { "method": "Otp" } }` döner.
Bekleyen giriş (kullanıcı, mağaza, security stamp) Data Protection ile **şifrelenip** 10 dakikalık HttpOnly cookie'ye
yazılır; istemci kodu `POST /api/auth/login/two-factor` ile gönderir. Hatalı kodlar hesabı kilitleme sayacına eklenir;
şifre ya da 2FA ayarı o arada değişirse bekleyen giriş geçersiz olur.

Passkey'de her akış iki adımdır (`/options` → tamamla). Challenge sunucu önbelleğinde 5 dakika tutulur, tarayıcıya
yalnızca rastgele anahtarı HttpOnly cookie ile verilir ve bir kez kullanılabilir. Passkey ile girişte kullanıcı adı
sorulmaz; passkey'in sahibi seçilen mağazada değilse giriş reddedilir. Ayarlar `Security:Passkey` (`ServerDomain`,
`Origins`); bölüm yoksa passkey endpoint'leri açılmaz. Tarayıcılar http'yi yalnızca `localhost` için kabul eder.

## Google / Microsoft / GitHub ile giriş

Anahtar girilen sağlayıcının düğmesi giriş sayfasında görünür (hiçbiri yoksa düğme de yok). Anahtarlar user-secrets'ta:

```bash
cd samples/Northwind/src/Northwind.WebApi
dotnet user-secrets set "Security:External:Google:ClientId" "....apps.googleusercontent.com"
dotnet user-secrets set "Security:External:Google:ClientSecret" "GOCSPX-..."
dotnet user-secrets set "Security:External:GitHub:ClientId" "Ov23li..."
dotnet user-secrets set "Security:External:GitHub:ClientSecret" "..."
dotnet user-secrets set "Security:External:Microsoft:ClientId" "<application (client) id>"
dotnet user-secrets set "Security:External:Microsoft:ClientSecret" "..."
```

| Sağlayıcı | Nereden | Dönüş adresi (geliştirme) |
|---|---|---|
| Google | Google Cloud Console → APIs & Services → Credentials → OAuth client ID (Web application) | `http://localhost:5173/signin-google` |
| GitHub | GitHub → Settings → Developer settings → OAuth Apps → New OAuth App | `http://localhost:5173/signin-github` |
| Microsoft | Azure portal → Microsoft Entra ID → App registrations → New registration (Web) | `http://localhost:5173/signin-microsoft` |

`npm run dev` ile çalışırken dönüş adresi 5173'tür (Vite `/signin-*` isteklerini API'ye aktarır); yalnızca .NET
uygulamasıyla çalışırken `http://localhost:5180/signin-...`. İkisini de kaydedebilirsin.

Akış: giriş sayfasında mağaza seçilir → sağlayıcı → `/signin-google` → `/api/auth/external/callback`. Kullanıcı
önce bağlı dış hesapla (sağlayıcının değişmez kimliği) aranır; yoksa sağlayıcı e-postayı **doğrulanmış** verdiyse
aynı e-postalı hesaba bağlanır; o da yoksa şifresiz yeni müşteri hesabı açılır. Microsoft iş/okul hesaplarında
e-postaya güvenilmediği için Microsoft ile yalnızca önceden bağlanmış hesaplar girebilir: şifrenle gir →
**Hesap güvenliği → Bağlı hesaplar → Microsoft bağla**. İki adımlı doğrulama açıksa dış girişte de kod istenir.
Hesaba girmenin tek yolu olan dış hesap kaldırılamaz (önce passkey ya da başka bir hesap bağla).

Yeni tablo (`UserLogins`) için geliştirme veritabanını sıfırla: `docker compose down -v && docker compose up -d`.

## Ürün araması

Mağaza sitesindeki arama kutusu arama dizinini kullanır: yazım hatası toleransı (`cikolata` → Çikolata), yazarken
arama (`çik`), ad + kategori + tedarikçi + birimde arama. Varsayılan bellek içi motordur; Elasticsearch ile:

```bash
docker compose up -d elasticsearch     # http://localhost:9200 (yalnızca geliştirme: güvenlik kapalı)
```

sonra `appsettings.Development.json` → `"Search": { "Elasticsearch": { "Url": "http://localhost:9200" } }`.
Açılışta dizin oluşturulur ve ürünler yeniden yüklenir (`Search:ReindexOnStartup`). Ürün eklenince, adı/fiyatı
değişince, satıştan kaldırılınca ya da silinince `ProductCatalogChanged` event'i dizini günceller. Elle yeniden
kurmak için (Admin): `POST /api/admin/search/products/reindex`. Arama sonuçlarının stok ve fiyatı veritabanından
taze okunur.

## Rapor tasarımcısı

Panelde **Satış → Rapor tasarımcısı** (`/admin/reports`): alanları satır, sütun ve değerlere sürükleyerek pivot rapor
kurulur; sonuç anında hesaplanır, Excel/PDF/CSV indirilir, rapor kaydedilip mağazadaki herkesle paylaşılabilir.
İki veri kaynağı var (`Northwind.Infrastructure/Reporting/NorthwindReporting.cs`):

| Kaynak | Satır | Yetki | Varsayılan rol |
|---|---|---|---|
| `sales` (Satışlar) | Sipariş satırı + sipariş, müşteri, ürün, kategori, çalışan, kargo | `reports.sales` | Sales |
| `inventory` (Stok) | Ürün + kategori, tedarikçi, stok değeri | `reports.inventory` | Warehouse |

Admin ikisini de görür. Gruplama mümkünse PostgreSQL'de yapılır (GROUP BY); sonuç çubuğundaki "veritabanında
hesaplandı" bunu gösterir. Kayıtlı raporlar `reporting.SavedReports` tablosundadır; `EnsureCreated` mevcut
veritabanına tablo eklemediği için bu sürümden önce oluşturulmuş geliştirme veritabanını silip yeniden başlat.

## Üretim için

- `Security:Jwt:SigningKey` (en az 32 karakter) ve `ConnectionStrings:Northwind`'i ortam değişkeni ya da gizli
  ayarlardan ver; `Seed:DemoUserPassword`'u verme.
- `Mail:Provider`'ı seç: `Smtp` için `Mail:Smtp`, `SendGrid` için `Mail:SendGrid` (`ApiKey` user-secrets/ortam
  değişkeninden: `dotnet user-secrets set "Mail:SendGrid:ApiKey" "SG.xxxx"`).
- Birden fazla sunucuda `Redis:ConnectionString` ver ve `Hangfire:Enabled=true` kullan (bellek içi kuyruk ve
  tekrarlayan işler her sunucuda ayrı çalışır).
- `Security:Passkey:ServerDomain` sitenin alan adı, `Origins` https adresleri olmalı. Data Protection anahtarlarını
  kalıcı ve paylaşılan bir yerde sakla (birden fazla sunucuda bekleyen 2FA girişleri çözülebilsin); passkey
  challenge'ları için `IMemoryCache` yerine dağıtık önbellek kullan.
- Birden fazla örnekle çalışırken `Database:InitializeOnStartup=false` yapıp migration'ları dağıtımda uygula.

Northwind verisi Microsoft'a aittir (Ms-PL); bkz. `src/Northwind.Infrastructure/Seeding/NOTICE.md`.
