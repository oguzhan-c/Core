# Değişiklik geçmişi

Bu dosya [Keep a Changelog](https://keepachangelog.com/tr-TR/1.1.0/) biçimindedir; sürümler
[Semantic Versioning](https://semver.org/lang/tr/)'e uyar. Tüm `Can.Core.*` paketleri aynı sürümle yayınlanır.

- **major** (2.0.0): kırıcı değişiklik (public API'den bir şey kaldırıldı/değişti, davranış uyumsuz değişti)
- **minor** (1.3.0): geriye uyumlu yeni özellik (`feat` commit'leri)
- **patch** (1.2.1): geriye uyumlu düzeltme (`fix` commit'leri)
- 1.0.0'dan önce (0.x) minor sürümler de kırıcı olabilir.

Yayın: `## [Unreleased]` başlığını `## [x.y.z] - YYYY-AA-GG` yap, `Directory.Build.props`'taki `VersionPrefix`'i bir
sonraki sürüme al, commit'le ve `vx.y.z` etiketini push et. CI paketleri derler, test eder ve NuGet'e yayınlar
(bu dosyada sürümün bölümü yoksa yayın durur).

## [Unreleased]

İlk sürüm. .NET 10 için katmanlı (DDD / Clean Architecture) uygulama altyapısı.

### Eklendi

- **Domain**: entity, aggregate root, value object, domain event, denetim (oluşturan/güncelleyen/silen) ve yumuşak
  silme, `Result`/`Error` tipleri, multi-tenant entity'ler.
- **Mediator**: request/notification, pipeline behavior'ları; dış paket yok.
- **Application**: logging, performans, telemetri, yetkilendirme (rol/yetki, joker), doğrulama (FluentValidation),
  önbellek (HybridCache), transaction behavior'ları; business rule'lar; `IDistributedLock`.
- **Özellik bayrakları**: kullanıcı/rol/tenant hedefleme, sabit yüzdelik açılım, zaman aralığı, tenant hariç tutma,
  ayarlardan canlı yükleme; `IFeatureGatedRequest`, `.RequireFeature()`, `/api/features`.
- **Persistence**: `CanDbContext`, repository ve unit of work, `DynamicQuery` (izin listeli alanlar), sayfalama,
  imleçli (keyset) sayfalama (`GetCursorPageAsync`, `ToCursorPageAsync`), outbox (üstel geri çekilme, ölü mesajlar, yeniden kuyruklama, sağlık kontrolü, metrikler), inbox, değişiklik geçmişi,
  seed.
- **Multi-tenancy**: tek veritabanı ya da tenant başına veritabanı, tenant yalnızca imzalı token'dan.
- **Security**: PBKDF2 parola özeti, JWT (cookie'de), yenileme token'ı, OTP, doğrulama kodları, passkey (FIDO2);
  EF Core deposu.
- **WebApi**: hata → ProblemDetails (çeviri destekli), `ICurrentUser`, tenant çözümleme, hız sınırlama, sağlık uç
  noktaları, harici giriş, idempotency anahtarı (`Idempotency-Key`), URL/başlık ile API sürümleme
  (`Deprecation`/`Sunset`).
- **HTTP önbelleği**: kullanıcı/tenant+rol/herkese açık kapsamlı sunucu önbelleği (`WithResponseCache`), handler
  önbelleğiyle ortak etiketler, entity değişince otomatik silme (`AddCanEntityCacheInvalidation`, commit'ten sonra),
  ETag ve 304, Brotli/gzip sıkıştırma, güvenli `Cache-Control` varsayılanları, `WithNoStore`; Redis'te ortak önbellek
  (`UseForOutputCache`).
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
