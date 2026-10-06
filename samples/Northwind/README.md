# Northwind Starter

Can.Core paketleriyle yazılmış örnek bir e-ticaret uygulaması: .NET 10 API + React 19 / shadcn arayüzü
(mağaza sitesi ve yönetim paneli). Klasik **Northwind** verisi (ürünler, müşteriler,
siparişler, çalışanlar, tedarikçiler) DDD ile yeniden modellendi ve PostgreSQL'de code-first olarak kuruluyor.
Tek veritabanında birden fazla mağaza (tenant) çalışır: `northwind` mağazası Northwind verisiyle, `demo` mağazası boş açılır.

## Çalıştırma

```bash
cd samples/Northwind
docker compose up -d                      # PostgreSQL (localhost:5432); Homebrew Postgres varsa gerekmez
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
| `/account/security` | Hesap güvenliği: iki adımlı doğrulama (authenticator / e-posta), passkey'ler |
| `/admin` | Yönetim paneli (Admin/Sales/Warehouse) |
| `/dev/mailbox` | Geliştirme posta kutusu: doğrulama kodları ve bildirimler |
| `/swagger`, `/scalar` | API belgesi (yalnızca Development) |

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

## Üretim için

- `Security:Jwt:SigningKey` (en az 32 karakter) ve `ConnectionStrings:Northwind`'i ortam değişkeni ya da gizli
  ayarlardan ver; `Seed:DemoUserPassword`'u verme.
- `Mail:Smtp` ayarlarını doldur (`Mail:PickupDirectory` boş olmalı).
- `Security:Passkey:ServerDomain` sitenin alan adı, `Origins` https adresleri olmalı. Data Protection anahtarlarını
  kalıcı ve paylaşılan bir yerde sakla (birden fazla sunucuda bekleyen 2FA girişleri çözülebilsin); passkey
  challenge'ları için `IMemoryCache` yerine dağıtık önbellek kullan.
- Birden fazla örnekle çalışırken `Database:InitializeOnStartup=false` yapıp migration'ları dağıtımda uygula.

Northwind verisi Microsoft'a aittir (Ms-PL); bkz. `src/Northwind.Infrastructure/Seeding/NOTICE.md`.
