# Değişiklik geçmişi

Bu dosya [Keep a Changelog](https://keepachangelog.com/tr-TR/1.1.0/) biçimindedir; sürümler
[Semantic Versioning](https://semver.org/lang/tr/)'e uyar. Tüm `CanFx.*` paketleri aynı sürümle yayınlanır.

- **major** (2.0.0): kırıcı değişiklik (public API'den bir şey kaldırıldı/değişti, davranış uyumsuz değişti)
- **minor** (1.3.0): geriye uyumlu yeni özellik (`feat` commit'leri)
- **patch** (1.2.1): geriye uyumlu düzeltme (`fix` commit'leri)
- 1.0.0'dan önce (0.x) minor sürümler de kırıcı olabilir.

Yayın: `## [Unreleased]` başlığını `## [x.y.z] - YYYY-AA-GG` yap, `Directory.Build.props`'taki `VersionPrefix`'i bir
sonraki sürüme al, commit'le ve `vx.y.z` etiketini push et. CI paketleri derler, test eder ve NuGet'e yayınlar
(bu dosyada sürümün bölümü yoksa yayın durur).

## [Unreleased]

### Eklendi

- Security: `ILoginThrottle`: hatalı girişler hesap + IP çifti başına sayılır; saldırgan yanlış şifre deneyerek yalnızca
  kendi IP'sini engeller, hesabın sahibi başka IP'den girmeye devam eder. `AddCanSecurity` bellek içi uygulamayı
  kaydeder (`o.LoginThrottle`: IP başına 5 deneme, 15 dk); hesap genelindeki kilit için `AccountMaxAttempts` (20).
  Birden çok sunucuda: Redis.ScaleOut → `UseForLoginThrottle()`.
- FileStorage: `IFileUploadValidator` / `FileUploadPolicy`: boyut, izinli ve yasak uzantılar (ara uzantılar dahil:
  `fatura.php.jpg`), içeriğin uzantıyla uyumu (dosya imzası) ve çalıştırılabilir içerik denetimi;
  `FileUploadValidator.SafeFileName`. `AddCanFileUploadValidation(...)` ile varsayılan kurallar.
- Settings: `SettingKeys.OptionList(...)` (sabit bir listeden seçim; tip `list`, `SettingDefinition.Options` /
  `SettingView.Options`) ve hazır ayarlar `CanSettingKeys` (giriş güvenliği, dosya yükleme).

### Değişti

- Redis.ScaleOut: Redis'e ulaşılamazken hız sınırı artık kalkmıyor: sınır sunucunun kendi belleğinde uygulanır
  (önceden istekler sınırlanmadan geçiyordu). Bağlantı yokken hız sınırı ve çıktı önbelleği komut zaman aşımını
  (5 sn) beklemez; durum değişimi bir kez loglanır.
- Caching.Redis: bağlantı yokken komutlar beklemeden hata verir (`AbortOnConnectFail = false`,
  `BacklogPolicy.FailFast`); HybridCache sunucunun belleğiyle devam eder.

## [0.4.1] - 2026-11-09

### Düzeltildi

- DevServices: profilli docker-compose servisleri (`redis: profiles: [cache, all]`, rabbitmq, elasticsearch ...)
  "docker-compose.yml'de servis yok" sanılıp başlatılmıyordu; yalnızca profilsiz veritabanı servisi başlıyordu.
  `docker compose config` ve `up` artık tüm profiller açık (`--profile "*"`) çalıştırılır; yine yalnızca istenen
  servis başlar.

## [0.4.0] - 2026-11-03

### Değişti

- DevServices: servisin portu doluysa (ör. bilgisayardaki PostgreSQL ya da başka bir projenin konteyneri) servis
  sıradaki boş portta başlatılır (`PortRange`, varsayılan 100) ve uygulamanın okuduğu adres (`AddressFrom`) o
  çalıştırma için yeni portla değiştirilir; dosyalara dokunulmaz. Bu projenin konteyneri zaten çalışıyorsa portu
  neyse o kullanılır. docker-compose'da olmayan (yorumda kalmış) servis açıkça bildirilir. **Kırıcı:** `Auto` artık
  önce docker'ı kullanır (bilgisayardaki kurulumu kullanmak için `Local`); kayıt
  `AddCanDevServices(configuration, environment)` (portlar kayıtta seçilir, adresleri okuyan kayıtlardan önce çağır).

## [0.3.0] - 2026-11-03

### Değişti

- DevServices: servisler kod gerekmeden appsettings'ten tanımlanır: `AddCanDevServices(configuration)` ve
  `"DevServices": { "Services": { "Redis": { "Compose": "redis", "AddressFrom": "Redis:ConnectionString" } } }`.
  Adres o ayardan açılışta okunur (bağlantı dizesi, URL, `sunucu:port` ya da `Host`/`Port` bölümü); boşsa, dosyaysa
  (SQLite) ya da bu bilgisayar değilse servis başlatılmaz. `When` ile koşul (`Mail:Provider=Smtp`). **Kırıcı:**
  `Services` artık servis başına ayar nesnesi (`"Database": "Auto"` yerine `"Database": { "Mode": "Auto", ... }`).
  Kodda `Add(...)` ile tanımlama sürüyor.

## [0.2.1] - 2026-11-02

### Değişti

- Hata yanıtları: `ValidationException`'daki alan hatası metni bir çeviri anahtarıysa (ör. `account.current_password_invalid`)
  isteğin diline çevrilir; düz metin olduğu gibi kalır. Böylece mesaj metinleri yalnızca `*.json` çeviri dosyalarında tutulabilir.

## [0.2.0] - 2026-11-02

### Eklendi

- **Kullanıcı**: `UserAggregateRoot<TId>` (domain event yayınlayan kullanıcı). Sade kullanıcı için `User<TId>` kullanılır.
- **Hız sınırı**: `RateLimiting:Policies` ile appsettings'ten kendi politikalarını tanımlama; her kural için
  `PartitionBy` (`UserOrIp`, `Ip`, `Tenant`). Uç noktada kullanılan ama tanımlı olmayan politika adı uygulamayı açılışta durdurur.
- **Hata mesajları**: `exception.With("name", value)` ile parametreli mesajlar; `{name}` çeviride de doldurulur.
  `JsonLocalizationStore.FindMissingTranslations(...)` ve Development'ta eksik çeviri uyarısı.
- **Veritabanı açılışı**: `DatabaseStartup.PrepareAsync(db, mode, isDevelopment)` — `Migrate`, `Recreate`, `None`.
  `Recreate` yalnızca Development'ta ve yerel veritabanında çalışır.
- **Seed**: `IDataSeeder.DevelopmentOnly` (örnek veri), `SeedDataAsync(includeDevelopmentData: ...)`, JSON seed dosyaları
  (`SeedFiles.ReadJsonAsync<T>`) ve tekrarlanabilir örnek veri üretici `SampleData`.
- **`CanFx.DevServices`** (yeni paket): Development'ta servis bilgisayarda çalışıyorsa onu kullanır, yoksa
  docker-compose ile yalnızca o servisi başlatır. Servis başına `Auto` / `Docker` / `Local` / `Off`.
- **`CanFx.WebApi.Postman`** (yeni paket): Development'ta OpenAPI belgesini Postman'e aktarır; sonraki açılışlarda aynı
  koleksiyonu günceller (kopya oluşturmaz), `baseUrl`'li ortam hazırlar. Anahtar user-secrets'ta (`Postman:ApiKey`).

### Değişti

- **Kırıcı**: `User<TId>` artık `FullAuditedEntity<TId>`'den türer (domain event yok). Event gerekiyorsa
  `UserAggregateRoot<TId>`'den türet.
- **Kırıcı**: `SeedDataAsync` imzası `includeDevelopmentData` parametresi aldı.

- Proje şablonu (`CanFx.Templates`), kod üretici (`CanFx.Generator`, komutu `canfxgen`) ve Rider eklentisi ayrı repoya taşındı
  (github.com/oguzhan-c/Generator); bu repo yalnızca `CanFx.*` paketlerini yayınlar.

## [0.1.0] - 2026-10-21

İlk sürüm. .NET 10 için katmanlı (DDD / Clean Architecture) uygulama altyapısı. Paketler `CanFx.*` adıyla
yayınlanır (geliştirme sırasındaki adı `Can.Core.*` idi); proje şablonu `CanFx.Templates` (`dotnet new canfx-monolith`),
kod üretici `CanFx.Generator.Cli` (`can-gen`).

### Eklendi

- **Anlık bildirim**: açık SignalR bağlantıları düzenli olarak yeniden doğrulanır (`RevalidationInterval`,
  `IRealtimeConnectionValidator`): çıkış / oturum kapatma / güvenlik damgası değişikliği / silinen kullanıcı / pasif
  tenant bağlantıyı kapatır, rol değişikliği rol gruplarını bağlantı kopmadan günceller; token süresi dolunca bağlantı
  kapanır (`CloseOnAuthenticationExpiration`); bağlantı açılış/kapanış logları; OpenTelemetry'ye SignalR metrikleri ve
  span'leri.
- **Şablon**: kimlik tablolarının anahtar tipi seçilebilir (`--IdKeyType guid|int|long`); tek yerde (`Directory.Build.props` → `IdentityKey` alias) tanımlı, kod her yerde `IdentityKey` / `IdentityKeys.New()` kullanır.
- **Rol / yetki yönetimi**: `PermissionSet` (joker dahil alt küme kontrolü, ad doğrulama), yetki yükseltmeyi engelleyen
  `IAccessGrantGuard` (alt küme, kendini değiştirememe, sistem rolleri, son yönetici), `IdentityPermissions`
  ("yetki verme yetkisi"), sunucu taraflı rol/yetki çözümleme (`IUserAuthorizationStore`,
  `AddCanUserAuthorizationStore`, `UseCanPermissionResolution`: değişiklik token süresini beklemeden geçerli), kod →
  veritabanı yetki kataloğu eşitleme (`SyncOperationClaimsAsync`, kullanımdan kalkan yetkiler); `Role.IsSystem`,
  `Role.Description`, `OperationClaim.IsDeprecated`.
- **Domain**: entity, aggregate root, value object, domain event, denetim (oluşturan/güncelleyen/silen) ve yumuşak
  silme, `Result`/`Error` tipleri, multi-tenant entity'ler.
- **Mediator**: request/notification, pipeline behavior'ları; dış paket yok.
- **Application**: logging, performans, telemetri, yetkilendirme (rol/yetki, joker), doğrulama (FluentValidation),
  önbellek (HybridCache), transaction behavior'ları; business rule'lar; `IDistributedLock`.
- **Özellik bayrakları**: kullanıcı/rol/tenant hedefleme, sabit yüzdelik açılım, zaman aralığı, tenant hariç tutma,
  ayarlardan canlı yükleme; `IFeatureGatedRequest`, `.RequireFeature()`, `/api/features`.
- **Persistence**: `CanDbContext`, repository ve unit of work, `DynamicQuery` (izin listeli alanlar), sayfalama,
  imleçli (keyset) sayfalama (`GetCursorPageAsync`, `ToCursorPageAsync`), veritabanı tanılaması (yavaş sorgu, SQL'e
  kaynak etiketi, komut metrikleri, N+1 dedektörü), isteğe bağlı veri saklama ve temizlik (tablo başına süre,
  partili silme, deneme modu, sunucular arası kilit), outbox (üstel geri çekilme, ölü mesajlar, yeniden kuyruklama, sağlık kontrolü, metrikler), inbox, değişiklik geçmişi,
  seed.
- **Multi-tenancy**: tek veritabanı ya da tenant başına veritabanı, tenant yalnızca imzalı token'dan.
- **Security**: PBKDF2 parola özeti, JWT (cookie'de), yenileme token'ı, OTP, doğrulama kodları, passkey (FIDO2);
  güvenlik damgası denetimi, kurtarma kodları, şifreli TOTP anahtarları, access token geri alma; aktif oturumlar
  (listeleme, tek cihazdan / diğer cihazlardan çıkış, `MapCanSessions`), kayıtlı cihazlar (ECDSA P-256 cihaz
  bağlama, tek kullanımlık meydan okuma); güvenlik olayları (log, metrik, isteğe bağlı veritabanı, kendi sink'in;
  istek bilgisiyle zenginleştirme); EF Core deposu.
- **WebApi**: hata → ProblemDetails (çeviri destekli), `ICurrentUser`, tenant çözümleme, hız sınırlama, sağlık uç
  noktaları, harici giriş, idempotency anahtarı (`Idempotency-Key`), URL/başlık ile API sürümleme
  (`Deprecation`/`Sunset`); yapılandırma hataları ve eksik kayıtlar açılışta, tek mesajda.
- **HTTP önbelleği**: kullanıcı/tenant+rol/herkese açık kapsamlı sunucu önbelleği (`WithResponseCache`), handler
  önbelleğiyle ortak etiketler, entity değişince otomatik silme (`AddCanEntityCacheInvalidation`, commit'ten sonra),
  ETag ve 304, Brotli/gzip sıkıştırma, güvenli `Cache-Control` varsayılanları, `WithNoStore`; Redis'te ortak önbellek
  (`UseForOutputCache`).
- **OpenAPI** (`CanFx.WebApi.OpenApi`): API sürümü başına belge, kullanımdan kalkan sürümler, cookie/Bearer güvenlik
  şeması, ProblemDetails hata yanıtları, `Idempotency-Key` başlığı, CDN'den yüklenen Scalar arayüzü; Production'da
  varsayılan kapalı.
- **İstek aktivite logu** (varsayılan kapalı): kim/ne zaman/hangi tenant/uç nokta/sonuç/süre/IP; isteği bekletmeyen
  kuyruk ve partili yazma, maskelenen sorgu parametreleri, veritabanı deposu, kendi sink'in.
- **Tarayıcı güvenliği** (üçü de varsayılan kapalı): güvenlik başlıkları (HSTS, yol başına CSP, nosniff, çerçeve,
  Referrer/Permissions/Cross-Origin politikaları), kimlik bilgili CORS (`*` yasak, açılan başlıklar, localhost yalnızca
  Development), iki katmanlı CSRF koruması (Sec-Fetch-Site/Origin denetimi + imzalı çift gönderimli token).
- **Ayarlar**: tipli uygulama/tenant ayarları, doğrulama, gizli (şifreli) değerler, önbellek; EF Core deposu; yönetim
  uç noktaları.
- **Event bus**: zarf, sabit event adları, tenant'ı koruyan dağıtım, ortak tüketim akışı (anında ve gecikmeli deneme,
  DLQ); RabbitMQ, Kafka ve Azure Service Bus taşıyıcıları; metrikler, sağlık kontrolü, DLQ'dan yeniden gönderim.
- **Raporlama**: pivot motoru (14 özet, gösterim dönüşümleri, Top N, ifade dili), veritabanında GROUP BY, tanım
  denetimi, bellek moduna düşme nedeni, CSV/Excel/PDF (formül enjeksiyonuna karşı korumalı); EF Core deposu, uç noktalar;
  React arayüzü (`clients/reporting-react`).
- **Diğer**: arka plan işleri (bellek içi ve Hangfire), Redis önbellek ve scale-out, dosya depolama (yerel, S3, Azure Blob),
  arama (bellek içi, Elasticsearch), e-posta (MailKit, SendGrid), SMS, SignalR bildirimleri, dayanıklılık
  (retry, circuit breaker, hedging ...), JSON yerelleştirme, Serilog, OpenTelemetry.
- **Test yardımcıları**: `TestClock`, `TestCurrentUser`/`TestCurrentTenant`, `SqliteTestDatabase`, test kimlik
  doğrulaması ve `CanTestApp`.
- **CI**: GitHub Actions, GitLab CI/CD, Azure DevOps, Bitbucket Pipelines, Jenkins, AWS CodeBuild ve Google Cloud Build
  yapılandırmaları (ortak `build/ci.sh`); etiketle NuGet yayını, Source Link ve sembol paketleri.
