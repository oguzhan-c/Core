# Can.Core

Yeni .NET projelerinde tekrar kullanılmak üzere yazılmış çekirdek paketler.
nArchitecture, CCA (Backend) ve CleanArchitectureWithBlazorServer projelerinin en iyi yanlarından derlendi.
Lisans derdi olan MediatR ve AutoMapper'ın yerine kendi implementasyonlarımızı içerir.

| Paket | Bağımlılık | İçerik |
|---|---|---|
| `Can.Core.Domain` | yok | `Entity<TId>`, `AggregateRoot<TId>`, audit/soft delete arayüzleri ve base sınıfları, `IDomainEvent`, `ValueObject` |
| `Can.Core.Mediator.Abstractions` | yok | `IRequest`, `IRequestHandler`, `INotificationHandler`, `IPipelineBehavior`, `ISender`, `IPublisher` |
| `Can.Core.Mediator` | DI.Abstractions | `Mediator`, publish stratejileri, `AddCanMediator(...)` |
| `Can.Core.Mapping` | DI.Abstractions | `MappingProfile`, `IMapper`, `ProjectTo`, `AddCanMapping(...)` |
| `Can.Core.Application.Abstractions` | yok | `ICurrentUser`, `ICurrentTenant`, istek işaretleyicileri (`ISecuredRequest`, `ICachableRequest` ...), hata tipleri |
| `Can.Core.Application` | Mediator, FluentValidation, HybridCache | Pipeline behavior'ları, `BaseBusinessRules`, `AddCanApplication(...)` |
| `Can.Core.Security` | IdentityModel, Fido2 | PBKDF2 şifre hash, JWT + refresh token, TOTP, e-posta kodu, passkey; User/Role/RefreshToken entity'leri |
| `Can.Core.Security.EntityFrameworkCore` | EF Core | Security entity'lerinin tablo, index ve ilişkileri: `ApplyCanSecurityModel<TUser, TId>()` |
| `Can.Core.BackgroundJobs` | Hosting.Abstractions | Tenant'ı koruyan iş kuyruğu, tekrarlayan işler |
| `Can.Core.BackgroundJobs.Hangfire` | Hangfire | Aynı `IBackgroundJobQueue` ile kalıcı kuyruk, yeniden deneme, cron, rol korumalı dashboard |
| `Can.Core.Redis.ScaleOut` | StackExchange.Redis, SignalR.StackExchangeRedis | Çok sunucu: ortak Data Protection anahtarları, SignalR backplane, sunucular arası hız sınırı, dağıtık kilit |
| `Can.Core.Caching.Redis` | StackExchangeRedis | Redis'i HybridCache'in ikinci (dağıtık) katmanı yapar: `AddCanRedisCache(...)` |
| `Can.Core.EventBus` | yok | `IEventBus`, sabit event adları, tenant'ı koruyan dağıtıcı, bellek içi taşıyıcı, inbox, ortak tüketim akışı (yeniden deneme, DLQ); outbox ile birlikte çalışır |
| `Can.Core.EventBus.RabbitMQ` | RabbitMQ.Client | Topic exchange, quorum kuyruk, gönderim onayı, gecikmeli deneme kuyrukları, DLQ |
| `Can.Core.EventBus.Kafka` | Confluent.Kafka | Event başına topic, idempotent producer, consumer group, retry/DLQ topic'leri |
| `Can.Core.EventBus.AzureServiceBus` | Azure.Messaging.ServiceBus | Topic + subscription (correlation filtreleri), zamanlanmış deneme, yerleşik DLQ |
| `Can.Core.FileStorage` | yok | `IFileStorage`, yerel disk, tenant'a göre otomatik klasörleme, güvenli yol doğrulama |
| `Can.Core.FileStorage.S3` | Extensions.Http | S3 uyumlu depolama (AWS, MinIO, R2); SDK'sız, kendi Signature V4 imzalaması, presigned URL |
| `Can.Core.Search` | yok | `ISearchIndex<T>`: tam metin arama, filtre, sıralama, vurgulama, toplama, tenant yalıtımı; bellek içi motor |
| `Can.Core.Search.Elasticsearch` | Extensions.Http | Elasticsearch / OpenSearch motoru; resmî istemci yerine HttpClient + Query DSL |
| `Can.Core.Reporting` | DI.Abstractions | Pivot rapor motoru: satır/sütun gruplama, ara/genel toplam, 14 özet (ortanca, yüzdelik, std. sapma ...), yüzde/kümülatif/fark/sıra gösterimleri, Top N + "Diğer", ifade dili, veritabanında GROUP BY, CSV/Excel/PDF (paketsiz) |
| `Can.Core.Reporting.EntityFrameworkCore` | EF Core | Kaydedilmiş raporlar tablosu ve deposu: `ApplyCanReportingModel()`, `UseEntityFrameworkStore<TContext>()` |
| `Can.Core.Reporting.AspNetCore` | ASP.NET Core | `MapCanReporting()`: kaynaklar, çalıştır, dışa aktar, kayıtlı raporlar; yetki ve tenant denetimi |
| `Can.Core.Localization` | Localization.Abstractions | JSON tabanlı `IStringLocalizer` (resx yok), kültür zinciri, hata kodu → metin |
| `Can.Core.Realtime` | yok | `IRealtimeNotifier`: kullanıcıya / role / tenant'a / gruba anlık mesaj (Application katmanı kullanır) |
| `Can.Core.Realtime.SignalR` | ASP.NET Core | Bildirimleri SignalR ile ileten hub; bağlantılar tenant/kullanıcı/rol gruplarına otomatik girer |
| `Can.Core.Resilience` | Threading.RateLimiting | Retry, circuit breaker, timeout, rate limiter/bulkhead, fallback, hedging pipeline'ları (Polly'siz) |
| `Can.Core.Resilience.Http` | Extensions.Http | `AddCanStandardResilienceHandler()`: HttpClient için hazır dayanıklılık |
| `Can.Core.MultiTenancy` | yok | `TenantInfo`, `ITenantStore`, `TenantContext`, tenant başına bağlantı dizesi, `CreateTenantScope` |
| `Can.Core.Sms` | Extensions.Http | `ISmsSender`, E.164 numara, GSM-7/UCS-2 parça hesabı, Türkçe karakter dönüştürme, HTTP sağlayıcı temeli |
| `Can.Core.Mailing` | yok | `IEmailSender`, `EmailMessage`, testler için `InMemoryEmailSender` |
| `Can.Core.Mailing.MailKit` | MailKit | SMTP göndericisi, geliştirme için `.eml` klasörü |
| `Can.Core.Mailing.SendGrid` | Extensions.Http | SendGrid Web API v3 göndericisi (SDK'sız, `IHttpClientFactory`) |
| `Can.Core.Logging.Serilog` | Serilog | `AddCanSerilog()`, `UseCanRequestLogging()` |
| `Can.Core.WebApi` | ASP.NET Core | Hata → ProblemDetails, `HttpCurrentUser`, token'dan tenant çözümleme, cookie tabanlı `AddCanJwtAuthentication()` |
| `Can.Core.Observability.OpenTelemetry` | OpenTelemetry | Can.Core ve .NET'in iz/metriklerini OTLP ile dışa aktarır: `AddCanOpenTelemetry()` |
| `Can.Core.Persistence.Abstractions` | Domain | `IRepository<TEntity,TId>`, `IUnitOfWork`, `DynamicQuery` (filtre/sıralama), `IPaginate<T>` — EF'e bağımlı değil |
| `Can.Core.Persistence` | EF Core | `CanDbContext`, `EfRepository`, `UnitOfWork`, audit ve domain event interceptor'ları, outbox, değişiklik geçmişi, seed, `AddCanPersistence(...)` |

```bash
dotnet build Core.slnx
dotnet test --solution Core.slnx
```

Testler xUnit v3 ile yazılır ve Microsoft Testing Platform ile çalışır (`global.json` → `"test": { "runner": "Microsoft.Testing.Platform" }`).
Her test projesi kendi kendini çalıştırabilen bir programdır; sık kullanılan komutlar:

```bash
dotnet test --project tests/Can.Core.Persistence.Tests                     # tek proje
dotnet test --solution Core.slnx --filter-class "*SpecificationTests"      # sınıfa göre
dotnet test --solution Core.slnx --filter-method "*Retries*"               # metoda göre
dotnet test --project tests/Can.Core.Persistence.Tests --show-live-output on  # ITestOutputHelper çıktısı
dotnet run --project tests/Can.Core.Persistence.Tests                      # doğrudan (xUnit'in kendi çalıştırıcısı)
```

## Domain

```csharp
public readonly record struct OrderId(Guid Value);          // TId'yi sen seçersin: int, Guid, string, strongly-typed...

public sealed record OrderConfirmed(OrderId OrderId) : DomainEvent;

public sealed class Order : FullAuditedAggregateRoot<OrderId>
{
    private Order() { }                                      // EF Core için
    public Order(OrderId id) : base(id) { }

    public OrderStatus Status { get; private set; }

    public void Confirm()
    {
        Status = OrderStatus.Confirmed;
        RaiseDomainEvent(new OrderConfirmed(Id));            // event'i sadece aggregate ekler
    }
}
```

| Sınıf | Ne ekler |
|---|---|
| `Entity<TId>` | kimlik, `IsTransient()`, `IsSameAs(other)` |
| `AuditedEntity<TId>` | + `CreatedAt/By`, `UpdatedAt/By` |
| `FullAuditedEntity<TId>` | + `IsDeleted`, `DeletedAt/By` (soft delete) |
| `AggregateRoot<TId>` | kimlik + domain event'ler |
| `AuditedAggregateRoot<TId>` / `FullAuditedAggregateRoot<TId>` | aynısı, aggregate için |

Base sınıf istemezsen sadece arayüzü uygula: `ICreationAudited`, `IModificationAudited`, `ISoftDeletable`, `IMultiTenant<TTenantId>`.

**Neden `Equals` override edilmiyor?** EF Core change tracker'ı, `HashSet` ve lazy-loading proxy'leri ile
sürpriz yaşamamak için entity'ler referans eşitliğini korur. Kimliğe göre karşılaştırma için `a.IsSameAs(b)` kullan.
Değere göre eşitlik `ValueObject`'te var.

### Result: beklenen hatalar exception değil, dönüş değeri

`Result<T>` ya bir değer ya da en az bir `Error` taşır (ErrorOr tarzı, `readonly struct`). Bulunamadı, iş kuralı ihlali,
doğrulama gibi **beklenen** hatalar böyle döner; exception'lar gerçekten beklenmeyen durumlar (bug, veritabanı çöktü)
için kalır. Değer ve hata dönüş tipine kendiliğinden çevrilir:

```csharp
public static class ProductErrors
{
    public static Error NotFound(Guid id) => Error.NotFound("product.not_found", $"'{id}' ürünü bulunamadı.");
    public static Error OutOfStock(string name) => Error.Failure("product.out_of_stock", $"'{name}' için stok yok.");
}

public Result<Success> Reserve(int quantity)
{
    if (quantity > UnitsInStock)
        return ProductErrors.OutOfStock(Name);   // Error → Result

    UnitsInStock -= quantity;
    return Result.Success;                       // değersiz başarı
}

public static Result<Product> Create(string name, decimal price)
{
    // Tüm kontroller tek seferde: hepsinin hatası birlikte döner (null = geçti).
    Result<Success> valid = Result.Validate(
        string.IsNullOrWhiteSpace(name) ? Error.Validation("required", "Ad boş olamaz.", "Name") : null,
        price < 0 ? Error.Validation("not_negative", "Fiyat negatif olamaz.", "Price") : null);
    if (valid.IsFailure)
        return valid.Errors;                     // başka tipteki sonucun hataları aktarılır

    return new Product(name, price);             // değer → Result
}
```

| | |
|---|---|
| `ErrorType` | `Failure` 400 · `Validation` 400 (alan: `Field`) · `Unauthorized` 401 · `Forbidden` 403 · `NotFound` 404 · `Conflict` 409 · `Unexpected` 500 |
| Okuma | `IsSuccess`, `Value` (başarısızsa **exception**, sessizce `default` dönmez), `Errors`, `FirstError`, `TryGetValue(out v)`, `ValueOrDefault` |
| Zincir | `Then` (sonraki adım da `Result` döner), `Map`, `Ensure(koşul, hata)`, `Tap`, `Else`, `MapErrors`, `ToSuccess` + `...Async` sürümleri; ilk hatada durur |
| `Task<Result<T>>` | aynı zincirler `await` yazmadan: `await repo.GetByIdAsync(id).ToResult(ProductErrors.NotFound(id)).Then(p => p.Reserve(2))` |
| Sonlandırma | `Match(değer => ..., hatalar => ...)`, `MatchFirst`, `Switch` |
| Toplama | `Result.Validate(params Error?[])`, `Result.Combine(params IResultBase[])` |
| Kaçış | `ThrowIfFailure()`: hatanın imkânsız olması gereken yerler (seed, testler) |
| `default(Result<T>)` | başarısız sayılır (`result.uninitialized`): unutulmuş bir dönüş başarı gibi görünmez |
| JSON | `{"isSuccess":..,"value":..}` / `{"isSuccess":false,"errors":[..]}`; önbelleğe (HybridCache) yazılabilsin diye |

## Mediator

```csharp
services.AddCanMediator(cfg =>
{
    cfg.RegisterServicesFromAssemblyContaining<CreateOrderCommand>();
    cfg.AddOpenBehavior(typeof(ValidationBehavior<,>));   // ilk eklenen en dışta çalışır
    cfg.AddOpenBehavior(typeof(TransactionBehavior<,>));
    // cfg.NotificationPublisherType = typeof(TaskWhenAllPublisher);   // varsayılan: sırayla
});
```

```csharp
public sealed record CreateOrderCommand(string CustomerId) : IRequest<OrderId>;

public sealed class CreateOrderHandler(IOrderRepository orders) : IRequestHandler<CreateOrderCommand, OrderId>
{
    public async Task<OrderId> Handle(CreateOrderCommand request, CancellationToken ct) { ... }
}

public sealed record DeleteOrderCommand(OrderId Id) : IRequest;                // yanıtsız
public sealed class DeleteOrderHandler : IRequestHandler<DeleteOrderCommand>   // Unit ile uğraşma
{
    public Task Handle(DeleteOrderCommand request, CancellationToken ct) { ... }
}
```

**Domain event'ler için ayrı dispatcher yok.** Handler'lar doğrudan domain event tipini dinler;
Domain katmanı mediator'a bağımlı olmaz:

```csharp
public sealed class SendConfirmationMail : INotificationHandler<OrderConfirmed> { ... }
public sealed class WriteToOutbox : INotificationHandler<IDomainEvent> { ... }   // tüm domain event'ler

// Persistence'taki interceptor:
foreach (IDomainEvent e in events) await publisher.Publish(e, ct);   // gerçek tipe göre handler'lar bulunur
```

Belirli isteklere uygulanan behavior'lar generic kısıtla yazılır (nArchitecture'daki `ICachableRequest` gibi);
kısıtı sağlamayan istekler için behavior otomatik atlanır:

```csharp
public sealed class CachingBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : ICachableRequest { ... }
```

## Mapping

```csharp
services.AddCanMapping(typeof(ProductProfile).Assembly);

public sealed class ProductProfile : MappingProfile
{
    public ProductProfile()
    {
        CreateMap<Product, ProductDto>()                                    // Brand.Name → BrandName otomatik
            .ForMember(d => d.Label, o => o.MapFrom(s => s.Name + " (" + s.Code + ")"))
            .ForMember(d => d.Secret, o => o.Ignore());

        CreateMap<UpdateProductCommand, Product>();
    }
}
```

```csharp
ProductDto dto        = mapper.Map<ProductDto>(product);
List<ProductDto> list = mapper.Map<List<ProductDto>>(products);
mapper.Map(command, existingProduct);                                       // mevcut nesneyi güncelle
var page = await db.Products.ProjectTo<ProductDto>(mapper).ToListAsync();   // EF: sadece gereken kolonlar
```

Desteklenenler: aynı isim (büyük/küçük harf duyarsız), flattening (`CustomerAddressCity` → `Customer.Address.City`),
iç içe map'ler, koleksiyonlar (`List`, dizi, `IEnumerable`, `IReadOnlyList`, `HashSet`), `T?` ↔ `T`,
sayısal dönüşümler, enum ↔ sayı, enum → string, positional record constructor'ları, `AfterMap`, `ReverseMap`.
Bellekte yapılan eşlemelerde null navigation'lar `NullReferenceException` yerine default değer verir.

Açılışta ya da bir testte `mapper.Configuration.AssertConfigurationIsValid()` çağır: eşlenemeyen her üye listelenir.

## Persistence

```csharp
public sealed class AppDbContext(DbContextOptions<AppDbContext> options, ICurrentTenant currentTenant)
    : CanDbContext(options, currentTenant)
{
    public DbSet<Product> Products => Set<Product>();

    protected override void ConfigureModel(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
}

services.AddCanMediator(cfg => cfg.RegisterServicesFromAssemblyContaining<AppDbContext>());
services.AddCanPersistence<AppDbContext>(o => o.UseNpgsql(connectionString));
// ICurrentUser / ICurrentTenant'ı WebApi katmanında kaydet (yoksa "boş" implementasyonlar kullanılır)
```

Handler'da:

```csharp
public sealed class ChangePriceHandler(IRepository<Product, int> products, IUnitOfWork unitOfWork)
    : IRequestHandler<ChangePriceCommand>
{
    public async Task Handle(ChangePriceCommand request, CancellationToken ct)
    {
        Product product = await products.GetByIdAsync(request.Id, cancellationToken: ct)
            ?? throw new NotFoundException(...);

        product.ChangePrice(request.Price);       // event aggregate'in içinde eklenir
        await unitOfWork.SaveChangesAsync(ct);    // audit + soft delete + event yayını burada
    }
}
```

**SaveChanges sırasında otomatik olanlar**

| Ne | Nasıl |
|---|---|
| `CreatedAt/By`, `UpdatedAt/By` | `AuditingInterceptor`, `ICurrentUser` ve `TimeProvider` ile. Oluşturma bilgisi sonradan değiştirilemez. |
| Soft delete | `repository.Delete(x)` → `IsDeleted = true` (cascade tetiklemez). Doğrudan `db.Remove(x)` da işaretlemeye çevrilir. Kalıcı silme: `Delete(x, permanent: true)`. |
| Tenant | Yeni kayda aktif tenant yazılır; başka tenant'a yazmaya çalışmak hata verir; tenant değiştirilemez. |
| Domain event'ler | Kayıt **başarılı olduktan sonra** `IPublisher` ile yayınlanır. Kayıt başarısızsa event'ler aggregate'te kalır. |

**Global filtreler** (EF Core 10 isimli filtreler): `CanQueryFilters.SoftDelete` silinmişleri, `CanQueryFilters.Tenant`
diğer tenant'ların kayıtlarını gizler. `withDeleted: true` yalnızca soft delete filtresini kapatır; **tenant filtresi her zaman açık kalır**.
Tenant yoksa (`ICurrentTenant.Id == null`) tenant'a ait hiçbir kayıt görünmez.

**Dinamik sorgu** (nArchitecture ile aynı JSON):

```json
{ "sort": [{ "field": "price", "dir": "desc" }],
  "filter": { "field": "name", "operator": "contains", "value": "kalem",
              "logic": "or", "filters": [{ "field": "category.name", "operator": "eq", "value": "Ofis" }] } }
```

Operatörler: `eq, neq, lt, lte, gt, gte, isnull, isnotnull, startswith, endswith, contains, doesnotcontain, in, between`.
System.Linq.Dynamic.Core yerine doğrudan expression tree kurulur: alan adları gerçek property'lere karşı doğrulanır,
değerler SQL parametresi olarak gider. Not: istemci herhangi bir public property'ye göre filtreleyebilir; hassas alanları
(ör. `PasswordHash`) DTO'ya değil entity'ye filtre açan endpoint'lerde dikkatli ol.

**Specification: sık kullanılan sorgular**

Tek seferlik ya da istemciden gelen sorgular için dinamik sorgu yeterli; uygulamanın birçok yerinde tekrar eden
sorgular ise adı olan bir sınıfa taşınır. Paket gerektirmez, Application katmanı EF Core'a bağımlı olmaz.

```csharp
public sealed class ProductsInStockSpec : Specification<Product>
{
    public ProductsInStockSpec(int? categoryId = null)
    {
        Where(p => p.UnitsInStock > 0 && !p.Discontinued);   // birden fazla Where VE'lenir
        if (categoryId is not null)
            Where(p => p.CategoryId == categoryId);

        Include(p => p.Category);                             // iç içe: Include("Lines.Product")
        OrderBy(p => p.Name);                                 // sonrakiler ThenBy olur
        AsReadOnly();                                         // AsNoTracking; IncludeDeleted(), Top(n), Page(i, s)
    }
}

// Projeksiyon: yalnızca seçilen kolonlar okunur
public sealed class ProductLookupSpec : Specification<Product, ProductLookup>
{
    public ProductLookupSpec() { OrderBy(p => p.Name); Select(p => new ProductLookup(p.Id, p.Name)); }
}

await repository.ListAsync(new ProductsInStockSpec(3), ct);
await repository.PaginateAsync(new ProductsInStockSpec(), index: 0, size: 20, ct);
await repository.ListAsync(new ProductLookupSpec(), ct);              // IReadOnlyList<ProductLookup>
await repository.FirstOrDefaultAsync(spec, ct); await repository.CountAsync(spec, ct); await repository.AnyAsync(spec, ct);

var spec = new ProductsInStockSpec() & new CheapProductsSpec();      // |, ! ; Specification<Product>.Create(p => ...)
bool ok = spec.IsSatisfiedBy(product);                               // bellekte (iş kuralı, test)
```

Birleştirmede koşullar tek parametreye indirgenir (SQL'e çevrilir); include/sıralama soldaki specification'dan alınır.

**Outbox: kaybolmaması gereken event'ler**

`IIntegrationEvent` uygulayan event'ler kayıttan hemen sonra bellekte yayınlanmaz; aggregate değişiklikleriyle aynı
transaction'da `OutboxMessages` tablosuna yazılır ve arka plan işi tarafından yayınlanır (en az bir kez; handler'lar idempotent olmalı).

```csharp
public sealed record OrderShipped(int OrderId, string Email) : DomainEvent, IIntegrationEvent;

// DbContext
protected override void ConfigureModel(ModelBuilder modelBuilder)
{
    modelBuilder.AddCanOutbox();
    modelBuilder.AddCanAuditTrail();
}

// Program.cs
builder.Services.AddCanOutbox<AppDbContext>(o => o.Interval = TimeSpan.FromSeconds(5));
```

**Event bus: servisler arası event'ler**

```csharp
[IntegrationEventName("sales.order-shipped")]                     // taşıyıcıdaki sabit ad (yoksa tipin tam adı)
public sealed record OrderShipped(int OrderId, string Email) : DomainEvent, IIntegrationEvent;

builder.Services.AddCanEventBus(typeof(OrderShipped).Assembly);   // tanınan event'ler
// dinleyen taraf: sıradan bir INotificationHandler<OrderShipped>
```

`AddCanEventBus` kayıtlıysa outbox event'leri bus'a verir. Bus event'i bir zarfa (`EventEnvelope`: ad, JSON, tenant,
EventId) koyup taşıyıcıya gönderir; karşı tarafta `EventDispatcher` zarfı açar ve handler'ları event'in tenant'ı adına
kendi scope'unda çalıştırır. Varsayılan taşıyıcı bellek içidir (aynı süreç, hemen dağıtır; hata olursa outbox tekrar
dener). Tanınmayan event'ler (başka servislerin) atlanır. Doğrudan `eventBus.PublishAsync(...)` veritabanı kaydıyla
atomik değildir; veriye bağlı event'leri aggregate'ten yükselt.

**Broker taşıyıcıları: RabbitMQ, Kafka, Azure Service Bus**

Servisler arası iletim için bir taşıyıcı seç (her biri ayrı paket, resmî istemciyle); hepsi aynı zarfı, aynı yeniden
deneme ve DLQ kurallarını kullanır:

```csharp
builder.Services.AddCanEventBus(typeof(OrderShipped).Assembly);
builder.Services.AddCanInbox<AppDbContext>();                    // tekrar teslimleri ayıkla; modelBuilder.AddCanInbox()

builder.Services.AddCanRabbitMqTransport(o => { o.ConnectionString = "..."; o.ConsumerName = "billing"; });
// ya da
builder.Services.AddCanKafkaTransport(o => { o.BootstrapServers = "localhost:9092"; o.ConsumerName = "billing"; });
// ya da
builder.Services.AddCanAzureServiceBusTransport(o => { o.ConnectionString = "..."; o.ConsumerName = "billing"; });
```

Tüketim akışı (`EventConsumer`, broker'dan bağımsız): mesaj zarfa çevrilir → inbox'ta işlenmiş mi → handler'lar →
hata olursa aynı süreçte hemen `ImmediateRetries` kez daha → yine olmazsa `RetryDelays` (varsayılan 10 sn, 1 dk, 10 dk,
1 sa) sonra yeniden teslim → hepsi tükenince DLQ. `PermanentEventFailureException` ve bozuk JSON doğrudan DLQ'ya gider.
DLQ mesajında hata, tipi, zamanı ve tüketici başlıklarda (`can-error`, `can-error-type`, `can-failed-at`, `can-consumer`).
`ConsumerName` aynı olan örnekler işi paylaşır, farklı servisler her event'in kopyasını alır. Dinlenen event'ler:
handler'ı kayıtlı olanlar (ya da `Events` listesi).

| | RabbitMQ (`RabbitMQ.Client` 7) | Kafka (`Confluent.Kafka` 2) | Azure Service Bus (`Azure.Messaging.ServiceBus` 7) |
|---|---|---|---|
| Yayın | Topic exchange `can.events`, routing key = event adı, gönderim onayı beklenir | Event başına topic (`TopicPrefix` + ad), idempotent producer, `acks=all` | Topic `can-events`, `Subject` = event adı, `MessageId` = event kimliği |
| Tüketim | Servis başına quorum kuyruk, event adlarıyla bağlı; prefetch 50, eşzamanlılık 8 | Consumer group = tüketici adı; offset yalnızca işlendikten sonra | Servis başına subscription, event başına correlation kuralı; peek-lock, eşzamanlılık 8 |
| Gecikmeli deneme | Her gecikme için `{kuyruk}.retry.{sn}s` (TTL dolunca ana kuyruğa döner) | `{tüketici}.retry` topic'i; zamanı gelmemişse partition durdurulur (`Pause`/`Seek`) | Zamanlanmış kopya (`Subject = can.retry`, yalnızca bu subscription alır) |
| DLQ | `{kuyruk}.dlq` (ana kuyruğun DLX'i de bu: broker'ın teslim sınırı aşılırsa kaybolmaz) | `{tüketici}.dlq` topic'i | Subscription'ın yerleşik dead-letter kuyruğu |
| Topoloji | `DeclareTopology` | `CreateTopics` | `ManageTopology` (Manage yetkisi) |

Kaynak kodlarından çıkan ve tasarıma yansıyan noktalar: RabbitMQ istemcisinin tüketicisi varsayılan olarak tek mesaj
işler (`ConsumerDispatchConcurrency = 1`) ve gövde yalnızca olay içinde geçerlidir (kopyalanır); bekleme kuyruğuna
yeniden yayında `x-death` başlıkları taşınmaz (taşınırsa broker döngü sanıp mesajı düşürür). Kafka'da gecikmeli teslim
yoktur ve `max.poll.interval.ms` (5 dk) aşılırsa tüketici gruptan atılır: bu yüzden bekleme thread uyutularak değil
partition durdurularak yapılır. Service Bus'ta yeni subscription her şeyi geçiren `$Default` kuralıyla gelir (silinir)
ve SDK varsayılanları (`MaxConcurrentCalls = 1`, otomatik tamamlama) değiştirilir.

Kendi taşıyıcın: `IEventTransport` (yayın) + mesaj geldiğinde `new EventConsumer(dispatcher, options, logger)
.ConsumeAsync(envelope, attempt, ct)` ve sonuca göre onay / yeniden teslim / DLQ; zarf ↔ başlık dönüşümü `EventHeaders`.

**Inbox: tam bir kez işleme.** Broker'lar "en az bir kez" teslim eder. `AddCanInbox<TContext>()` ile her event bir
tüketicide bir kez işlenir: handler'ların `TContext` değişiklikleri ve inbox kaydı aynı transaction'dadır (handler
hata verirse ikisi birden geri alınır). Veritabanı dışı yan etkileri (e-posta, HTTP) idempotent yaz. Eski kayıtlar
`Retention` (7 gün) sonra silinir. Tek örnekli uygulamalarda `AddCanInMemoryInbox()`.

Gerçek broker'a karşı testler (Northwind `docker compose up -d rabbitmq kafka`):
`CAN_RABBITMQ_URL=amqp://guest:guest@localhost:5672/ CAN_KAFKA_BOOTSTRAP=localhost:9092 dotnet test --project tests/Can.Core.EventBus.Brokers.Tests`.
Azure için gerçek ad alanı ya da [Service Bus emülatörü](https://learn.microsoft.com/azure/service-bus-messaging/overview-emulator):
`CAN_AZURE_SERVICEBUS_CONNECTION="Endpoint=...;UseDevelopmentEmulator=true;"`.

**Değişiklik geçmişi (audit trail)**

`[Audited]` işaretli entity'lerin her değişikliği `AuditLogs` tablosuna yazılır: kim, ne zaman, hangi tenant,
hangi alan neyden neye (`{"Price":{"old":10,"new":12}}`). Hassas alanlara `[DisableAuditing]` koy (Security
entity'lerinde şifre hash'i, gizli anahtarlar ve token hash'leri zaten işaretli). Tüm entity'ler için:
`services.AddCanAuditTrail(o => o.AuditAllEntities = true)`.

**Başlangıç verisi**

```csharp
public sealed class CategorySeeder(AppDbContext db) : IDataSeeder
{
    public int Order => 1;
    public async Task SeedAsync(DataSeedContext context, CancellationToken ct)
    {
        if (context.IsHost || await db.Categories.AnyAsync(ct)) return;   // idempotent
        db.Categories.Add(new Category("İçecekler"));
        await db.SaveChangesAsync(ct);
    }
}

builder.Services.AddCanDataSeeders(typeof(Program).Assembly);
await app.Services.InitializeTenantDatabasesAsync<AppDbContext>();   // migration
await app.Services.SeedDataAsync();   // önce host, sonra her aktif tenant kendi scope'unda
```

**Transaction**

```csharp
await unitOfWork.ExecuteInTransactionAsync(async ct =>
{
    ...
    return result;
}, ct);   // başarılıysa kaydet + commit, hata olursa rollback
```

## Application

```csharp
services.AddCanApplication(o => o.AdminRole = "Admin", typeof(CreateProductCommand).Assembly);
// handler'lar, FluentValidation validator'ları, BaseBusinessRules sınıfları ve behavior'lar tek çağrıyla kaydedilir
```

Bir isteğin hangi davranışlara gireceğini işaretleyiciler belirler (nArchitecture'daki gibi):

```csharp
public sealed record CreateProductCommand(string Name, int Price)
    : IRequest<int>, ISecuredRequest, ITransactionalRequest, ICacheRemoverRequest, ILoggableRequest
{
    public IReadOnlyCollection<string> Roles => ["Product.Write"];
    public IReadOnlyCollection<string> CacheTagsToRemove => ["products"];
}

public sealed record GetProductsQuery(int Page) : IRequest<List<ProductDto>>, ICachableRequest
{
    public string CacheKey => $"products:{Page}";
    public IReadOnlyCollection<string> CacheTags => ["products"];
}
```

| Behavior | İşaretleyici | Ne yapar |
|---|---|---|
| Logging | `ILoggableRequest` | başlangıç/bitiş/hata, kullanıcıyla (gövde loglanmaz) |
| Performance | hepsi | `SlowRequestThreshold`'u aşanları uyarı olarak loglar |
| Authorization | `ISecuredRequest` | giriş yoksa `UnauthorizedException`, rol yoksa `ForbiddenException`; `AdminRole` her zaman geçer |
| Validation | validator'ı olan her istek | FluentValidation; hatalar alan bazlı `ValidationException.Errors` |
| Caching | `ICachableRequest` | HybridCache (bellek + varsa Redis), etiketli |
| CacheRemoving | `ICacheRemoverRequest` | işlem **commit edildikten sonra** etiketleri/anahtarları siler |
| Transaction | `ITransactionalRequest` | handler'ı transaction'da çalıştırır, sonunda kaydeder; handler'da `SaveChangesAsync` gerekmez |

Sıra: `Logging → Performance → Authorization → Validation → Caching → CacheRemoving → Transaction → Handler`.

**Hatalar.** İstek `Result<T>` döndürüyorsa (`IRequest<Result<ProductDto>>`) pipeline da exception fırlatmaz:

| Davranış | `Result<T>` dönen istekte |
|---|---|
| Authorization | `Error.Unauthorized()` / `Error.Forbidden()` döner |
| Validation | her FluentValidation hatası `Error.Validation("validation", mesaj, alan)` olur |
| Transaction | sonuç başarısızsa **hiçbir şey kaydedilmez, transaction geri alınır** (handler'ın ara `SaveChanges`'ları dahil) |
| Caching | başarısız sonuç önbelleğe yazılmaz |
| CacheRemoving | başarısız işlemde önbellek temizlenmez (veri değişmedi) |
| Logging | başarısız sonuç hata kodlarıyla Warning olarak loglanır |

`Result` dönmeyen istekler eskisi gibi exception ile çalışır (WebApi HTTP koduna çevirir):
`ValidationException` 400 · `BusinessException` (Domain) 400 · `UnauthorizedException` 401 · `ForbiddenException` 403 ·
`NotFoundException` 404 (`NotFoundException.For<Product>(id)`) · `ConflictException` 409.

## WebApi

```csharp
builder.Services.AddCanApplication(typeof(CreateProductCommand).Assembly);
builder.Services.AddCanPersistence<AppDbContext>(o => o.UseNpgsql(connectionString));
builder.Services.AddCanSecurity(o => builder.Configuration.GetSection("Security:Jwt").Bind(o.Jwt));
builder.Services.AddCanWebApi();
builder.Services.AddCanJwtAuthentication();   // JWT HttpOnly cookie'den okunur

var app = builder.Build();
app.UseCanExceptionHandler();   // en başta
app.UseAuthentication();
app.UseCanTenantResolution();   // authentication'dan SONRA
app.UseAuthorization();

app.MapPost("/products", (CreateProductCommand command, ISender sender, CancellationToken ct) => sender.Send(command, ct));
app.MapGet("/me", (ISender sender, CancellationToken ct) => sender.Send(new GetMeQuery(), ct));
```

**Hatalar** RFC 9457 ProblemDetails olarak döner (`application/problem+json`). 500'lerde iç ayrıntı yalnızca Development'ta gösterilir.

**Result → HTTP.** `ToHttpResult()` başarıda 200 + değer (değer `Success` ise 204), hatada exception handler ile
**aynı biçimde** ProblemDetails döndürür (`status`, `title`, `detail`, `code`; doğrulamada alan bazında `errors`,
birden fazla iş hatasında `details`). Durum kodunu ilk hatanın türü belirler.

```csharp
products.MapGet("/{id:guid}", (Guid id, ISender sender, CancellationToken ct) =>
    sender.Send(new GetProductQuery(id), ct).ToHttpResult());

products.MapPost("/", (CreateProductCommand command, ISender sender, CancellationToken ct) =>
    sender.Send(command, ct).ToHttpResult(id => TypedResults.Created($"/api/products/{id}", new { id })));

// handler'a gitmeden dönen hata
if (cookie is null)
    return AuthErrors.SessionExpired.ToProblem();
```

**Kullanıcı ve tenant yalnızca imzalı JWT'den** okunur (`sub`, `name`, `email`, `role`, `tenant_id`). Header, query string
ya da istek gövdesinden tenant/kullanıcı bilgisi alınmaz; istemci bunları değiştiremez.

**JWT cookie'de taşınır** (`AddCanJwtAuthentication`):

| Ayar | Varsayılan | Neden |
|---|---|---|
| `HttpOnly` | açık | JavaScript token'a erişemez; XSS ile çalınamaz |
| `Secure` | açık | yalnızca HTTPS |
| `SameSite` | `Strict` | başka sitelerden gelen isteklerle gönderilmez (CSRF koruması) |
| Refresh token `Path` | `/auth/refresh` | yalnızca yenileme isteğinde gönderilir |
| `AllowAuthorizationHeader` | kapalı | `Authorization: Bearer` header'ı yok sayılır; mobil/servis istemcileri için açılabilir |

```csharp
app.MapPost("/auth/login", async (LoginCommand command, ISender sender, IAuthCookieService cookies, HttpResponse response) =>
{
    LoginResult result = await sender.Send(command);
    cookies.SetTokens(response, result.AccessToken, result.RefreshToken);   // HttpOnly cookie'ler
    return Results.NoContent();
});
app.MapPost("/auth/logout", (IAuthCookieService cookies, HttpResponse response) => { cookies.Clear(response); return Results.NoContent(); });
```

Ön yüz farklı bir sitedeyse (`SameSite = None`) cookie'ler çapraz istekle gönderilebilir; o durumda antiforgery token ekle.

**Tenant** kuralları:

| Durum | Aktif tenant |
|---|---|
| Giriş yok | yok |
| Token'da tek `tenant_id` | o tenant |
| Token'da tenant yok ya da birden fazla | yok (hiçbir tenant verisi görünmez) |
| Tenant depoda yok ya da pasif | **403** (pasife alınan tenant'ın eski token'ları da hemen geçersiz) |

Tenant değiştirmek isteyen kullanıcı için sunucu üyeliği kontrol eder ve o tenant için **yeni token** üretir.

## Security

```csharp
builder.Services.AddCanSecurity(o =>
{
    builder.Configuration.GetSection("Security:Jwt").Bind(o.Jwt);          // Issuer, Audience, SigningKey (≥ 32 karakter)
    o.VerificationCodeKey = builder.Configuration["Security:CodeKey"];     // e-posta kodları için (isteğe bağlı)
    o.Passkey = new PasskeyOptions                                          // passkey için (isteğe bağlı)
    {
        ServerDomain = "example.com",
        ServerName = "Can App",
        Origins = ["https://example.com"],
    };
});
builder.Services.AddCanJwtAuthentication();   // Can.Core.WebApi: aynı ayarlarla doğrulama, token HttpOnly cookie'de
```

| Servis | Ne yapar | nArchitecture'dan farkı |
|---|---|---|
| `IPasswordHasher` | PBKDF2-SHA256, 600.000 iterasyon; tek metin (`v1.iter.salt.hash`) | HMACSHA512 yerine yavaş KDF; ayrı salt kolonu yok; iterasyon artınca `SuccessRehashNeeded` |
| `ITokenService` | JWT access token (`sub`, `name`, `email`, `role`, tek aktif `tenant_id`, `jti`) + refresh token | Refresh token veritabanında **hash'li**; `TimeProvider` ile test edilebilir |
| `ITotpService` | Google/Microsoft Authenticator (RFC 6238), QR için `otpauth://` adresi | Dış paket yok; aynı kodun tekrar kullanımı engellenir |
| `IVerificationCodeService` | 6 haneli e-posta/SMS kodu, HMAC'li saklanır | Deneme sınırı ve süre `EmailAuthenticator` entity'sinde |
| `IPasskeyService` | Passkey kayıt ve giriş (WebAuthn, Fido2NetLib) | Yeni |

**Entity'ler** (tek `TId` ile, türetip genişletilir): `User<TId>` (e-posta, şifre hash'i, 2FA tipi, hesap kilitleme,
security stamp), `Role<TId>`, `UserRole<TId>`, `RefreshToken<TId>` (rotasyon ve iptal), `OtpAuthenticator<TId>`,
`EmailAuthenticator<TId>`, `UserPasskey<TId>`, `UserLogin<TId>` (bağlı Google/Microsoft/GitHub hesabı),
`OperationClaim<TId>` + `RoleOperationClaim<TId>` + `UserOperationClaim<TId>`.

Tabloları kurmak için (`Can.Core.Security.EntityFrameworkCore`):

```csharp
protected override void ConfigureModel(ModelBuilder modelBuilder) =>
    modelBuilder.ApplyCanSecurityModel<AppUser, Guid>(o => o.Schema = "identity");
```

Roller JWT'ye `role` claim'i olarak yazılır; `ISecuredRequest.Roles`, `ICurrentUser.IsInRole` ve
`[Authorize(Roles = ...)]` aynı claim'i kullanır.

**Yetkiler (operation claim)**: rollerin yanında ince taneli yetkiler. Rollere ya da doğrudan kullanıcıya verilir,
token'a `permission` claim'i olarak yazılır.

```csharp
var ship = new OperationClaim<Guid>(Guid.CreateVersion7(), "orders.ship", "Siparişi kargoya verme");
warehouseRole.GrantOperationClaim(ship);          // rol üzerinden
user.GrantOperationClaim(cancel);                 // doğrudan
// giriş: roller + yetkiler yüklü kullanıcıdan
tokens.CreateAccessToken(new TokenSubject(user.Id.ToString(), Roles: roles, Permissions: user.GetPermissionNames()));

public sealed record ShipOrderCommand(Guid OrderId) : IRequest<Result<Success>>, ISecuredRequest
{
    public IReadOnlyCollection<string> Permissions => ["orders.ship"];   // Roles ile birlikte: herhangi biri yeter
}
```

Eşleşme büyük/küçük harf duyarsız; `"orders.*"` `orders.` ile başlayan her yetkiyi, `"*"` hepsini kapsar
(`ICurrentUser.HasPermission`). Yönetici rolü (`CanApplicationOptions.AdminRole`) her şeyi geçer. Yetki listesi
token'a (ve cookie'ye) girdiği için çok sayıda yetkiyi tek tek değil joker olarak vermek boyutu küçük tutar;
yetki değişikliği kullanıcının bir sonraki token yenilemesinde geçerli olur.

**Dış sağlayıcıyla giriş (OAuth)**: Google, Microsoft, GitHub ya da herhangi bir OAuth 2.0 sağlayıcısı. ASP.NET
Core'un yerleşik OAuth handler'ı kullanılır, ek paket yok; PKCE açık, sağlayıcının token'ı saklanmaz. Dönüşte
uygulamanın kendi oturumu (cookie'deki JWT) açılır.

```csharp
// anahtarlar user-secrets'ta; ClientId/ClientSecret boş olan sağlayıcı açılmaz
builder.Services.AddCanExternalLogin(o => o
    .AddGoogle(config["Security:External:Google:ClientId"] ?? "", config["Security:External:Google:ClientSecret"] ?? "")
    .AddMicrosoft(config["Security:External:Microsoft:ClientId"] ?? "", config["Security:External:Microsoft:ClientSecret"] ?? "")
    .AddGitHub(config["Security:External:GitHub:ClientId"] ?? "", config["Security:External:GitHub:ClientSecret"] ?? ""));

app.UseAuthentication(); // /signin-google gibi dönüş adreslerini handler karşılar
app.MapGroup("/api").MapCanExternalLogin("/auth/external", async (info, http) =>
{
    // info: Provider, ProviderKey (değişmez kimlik), Email, EmailVerified, Name, Tenant, ReturnUrl, Mode (login/link)
    // kullanıcıyı bul/oluştur → token'ları cookie'ye yaz → Results.Redirect(info.ReturnUrl)
});
```

| Adres | Ne yapar |
|---|---|
| `GET /api/auth/external/providers` | Ayarlı sağlayıcılar (giriş düğmeleri) |
| `GET /api/auth/external/{provider}/login?tenant=..&returnUrl=..` | Sağlayıcıya yönlendirir (tam sayfa, fetch değil) |
| `GET /api/auth/external/{provider}/link?returnUrl=..` | Giriş yapmış kullanıcının hesabına bağlar |
| `GET /api/auth/external/callback` | Dönüş: kimlik okunur, uygulamanın callback'i çağrılır |

Sağlayıcı konsolunda dönüş adresi `https://<site>/signin-{provider}` olarak kaydedilir (`/signin-google`,
`/signin-microsoft`, `/signin-github`). Güvenlik kuralları:

- Kullanıcıyı **`ProviderKey` ile** bul, e-postayla değil (e-posta değişebilir, başkası yazabilir).
- E-postayla mevcut hesaba otomatik bağlama yalnızca `EmailVerified` ise yapılır. Google'da `email_verified`,
  GitHub'da birincil + doğrulanmış e-posta kullanılır; Microsoft'ta iş/okul hesaplarında e-posta yöneticice
  değiştirilebildiği için varsayılan olarak güvenilmez (`TrustEmail = false`).
- `returnUrl` yalnızca uygulama içi göreli adres olabilir (açık yönlendirme engellenir).
- Bağlama modunda kullanıcı ve tenant istemciden değil, sağlayıcıya giden şifreli state'ten okunur: dönüş isteği
  siteler arası olduğu için `SameSite=Strict` oturum cookie'si gelmez.
- Hata olursa `ErrorPath?externalError=kod` adresine dönülür (`denied`, `failed`, ya da uygulamanın hata kodu).

Güvenlik notları: imza anahtarlarını koda/appsettings'e yazma (User Secrets, ortam değişkeni, Key Vault);
`OtpAuthenticator.SecretKey`'i veritabanında şifreli sakla; refresh token yeniden kullanımı tespit edilirse
kullanıcının tüm token'larını iptal et (bkz. `RefreshToken<TId>` açıklaması).

## Multi-tenancy: tek veritabanı ya da tenant başına veritabanı

İki yaklaşım aynı uygulamada birlikte kullanılabilir: bir tenant'a `ConnectionString` verilirse kendi veritabanını,
verilmezse ortak veritabanını (TenantId filtresiyle) kullanır. Tenant filtresi her durumda açık kalır (ek güvenlik).

```csharp
builder.Services.AddCanMultiTenancy(o =>
{
    o.DefaultConnectionString = builder.Configuration.GetConnectionString("Default");   // ortak veritabanı
    builder.Configuration.GetSection("Tenants").Bind(o.Tenants);                        // ya da kendi ITenantStore'un
});

builder.Services.AddCanPersistence<AppDbContext>((sp, o) =>
    o.UseNpgsql(sp.GetRequiredService<ITenantConnectionStringResolver>().Resolve()));

builder.Services.AddCanWebApi();

var app = builder.Build();
await app.Services.InitializeTenantDatabasesAsync<AppDbContext>();   // ortak + her tenant DB'sine migration
```

```json
"Tenants": [
  { "Id": "8f1c...", "Identifier": "acme",   "Name": "Acme",   "ConnectionString": "Host=db-acme;Database=acme;..." },
  { "Id": "2b7e...", "Identifier": "globex", "Name": "Globex" }
]
```

| Durum | Sonuç |
|---|---|
| Token'da tek `tenant_id` | o tenant; depodan tanımı yüklenir, kendi DB'si varsa ona bağlanılır |
| Depoda olmayan ya da pasif tenant | **403** |
| Tenant yok (anonim istek) | ortak veritabanı, tenant'a ait kayıtlar görünmez |

**Tenant başına veritabanında giriş:** anonim istekte tenant olmadığından login handler'ı tenant'ı isteğin kendisinden
(ör. `LoginCommand.Tenant` = `"acme"`) alır, depodan bulur ve doğrulamayı o tenant'ın scope'unda yapar; başarılı olursa
`tenant_id`'li token üretilir. Kimlik bilgisi doğrulanmadan hiçbir veri okunmaz.

```csharp
TenantInfo tenant = await tenantStore.FindAsync(command.Tenant, ct) ?? throw new UnauthorizedException();
await using AsyncServiceScope scope = serviceProvider.CreateTenantScope(tenant);
// scope.ServiceProvider üzerinden kullanıcı bulunur, şifre doğrulanır, token üretilir
```

Arka plan işleri ve seed için de aynı yöntem: `await using var scope = app.Services.CreateTenantScope(tenant);`

## SMS

SMS, sağlayıcının (Netgsm, İleti Merkezi, Twilio, Vonage ...) HTTP API'sine istek atılarak gönderilir; operatörlere
doğrudan bağlanmak için lisans gerekir. Bu paket sağlayıcıdan bağımsız altyapıdır: uygulama `ISmsSender` kullanır,
sağlayıcı değişince yalnızca kayıt değişir.

```csharp
builder.Services.AddCanSms(o =>
    {
        o.DefaultSender = "NORTHWIND";   // sağlayıcıda onaylı başlık
        o.TransliterateToGsm = true;     // ğ→g, ş→s: 70 yerine 160 karakter, daha ucuz
        o.MaxSegments = 3;               // yanlışlıkla uzun metne karşı
    })
    .UsePickupDirectory("sms");          // geliştirme: her SMS bir JSON dosyası; testlerde UseInMemory()

SmsSendResult result = await sms.SendAsync(new SmsMessage("0532 123 45 67", $"Doğrulama kodun: {code}"), ct);
if (!result.Succeeded) ...               // geçersiz numara, boş/uzun metin, sağlayıcının reddi (kredi bitti ...)
```

| | |
|---|---|
| Numara | `PhoneNumbers.TryNormalize`: `0532 123 45 67`, `+90 532...`, `0090...`, `532...` → `+905321234567` (E.164); `Mask` loglar için |
| Parça hesabı | `SmsText.Analyze`: GSM-7 160/153, UCS-2 70/67 karakter; `€ [ ] { }` GSM'de 2 sayılır. `ç ğ ı ş İ` GSM'de yok → metin UCS-2 olur |
| Hatalar | Doğrulama ve sağlayıcının reddi `SmsSendResult.Failed`; ağ hatası, 5xx ve 429 `SmsException` (tekrar denenebilir) |
| Gözlemlenebilirlik | `Can.Core.Sms` span'ı; `can.sms.messages` (sent/rejected/failed) ve maliyet için `can.sms.segments`. Numara ve metin etiketlenmez |

Yeni bir sağlayıcı `HttpSmsProvider`'dan türetilir: yalnızca isteği kurmak (`CreateRequest`) ve yanıtı okumak
(`ReadResponseAsync`) yazılır; zaman aşımı, bağlantı ve sunucu hataları temel sınıftadır.

```csharp
public sealed class AcmeSmsProvider(HttpClient http, AcmeOptions options) : HttpSmsProvider(http)
{
    public override string Name => "acme";

    protected override HttpRequestMessage CreateRequest(SmsRequest sms) =>
        new(HttpMethod.Post, "https://api.acme.example/v1/sms")
        {
            Content = JsonContent.Create(new { to = sms.To, text = sms.Text, sender = sms.From, key = options.ApiKey }),
        };

    protected override async Task<SmsSendResult> ReadResponseAsync(HttpResponseMessage response, SmsRequest sms, CancellationToken ct)
    {
        AcmeResponse? body = await response.Content.ReadFromJsonAsync<AcmeResponse>(ct);
        return body?.Status == "ok"
            ? SmsSendResult.Sent(body.Id, sms.Segments)
            : SmsSendResult.Failed(SmsErrorCodes.Rejected, body?.Error ?? "Reddedildi.");
    }
}

builder.Services.AddCanSms().UseHttpProvider<AcmeSmsProvider>().AddCanStandardResilienceHandler();
```

Türkiye'de toplu/ticari SMS için İYS (İleti Yönetim Sistemi) onayı gerekir; `SmsMessage.IsCommercial` sağlayıcıya
bunu bildirmek içindir. Doğrulama kodu gibi bilgilendirme mesajları ticari değildir.

## Mailing

```csharp
builder.Services.AddCanMailKit(o => builder.Configuration.GetSection("Mailing:Smtp").Bind(o));   // üretim
builder.Services.AddCanEmailPickupDirectory("mails");                                            // geliştirme: .eml dosyaları
builder.Services.AddCanSendGrid(o => builder.Configuration.GetSection("Mailing:SendGrid").Bind(o)); // ya da SendGrid
builder.Services.AddCanInMemoryEmail();                                                          // testler

var message = new EmailMessage("Doğrulama kodun") { HtmlBody = $"<p>Kodun: <b>{code}</b></p>", TextBody = $"Kodun: {code}" };
message.To.Add(new EmailAddress(user.Email));
await emailSender.SendAsync(message, ct);
```

E-postayı istek içinde değil, domain event handler'ından (ileride arka plan işinden) göndermek önerilir.

SendGrid: `ApiKey` ("Mail Send" yetkisi yeterli) secret store'dan gelmeli; `FromAddress` SendGrid'de doğrulanmış
olmalı. `SandboxMode = true` isteği doğrular ama göndermez. Başarısız yanıt `SendGridException` (durum kodu + gövde) fırlatır.
AB veri yerleşimi için `BaseAddress = https://api.eu.sendgrid.com/`.

## Önbellek: Redis

```csharp
builder.Services.AddCanRedisCache(o =>
{
    o.ConnectionString = builder.Configuration["Redis:ConnectionString"]!;
    o.InstanceName = "myapp:";
});
```

`AddCanApplication` zaten HybridCache kullanır; Redis eklenince HybridCache onu ikinci katman olarak alır. Böylece
birden fazla sunucu aynı önbelleği paylaşır, uygulama yeniden başlasa da önbellek ısınık kalır. Kod değişmez
(`ICachableRequest` / `ICacheRemoverRequest` aynen çalışır).


## Çok sunucu: Redis (scale-out)

Uygulama birden çok sunucuda (ya da konteynerde) çalışınca belleğinde tuttuğu bazı şeyler ortak olmalı. Önbellek
`Can.Core.Caching.Redis`'te; geri kalanı `Can.Core.Redis.ScaleOut`'ta (tek Redis bağlantısı paylaşılır, özellikler tek tek açılır):

```csharp
builder.Services.AddCanRedisScaleOut(o =>
    {
        o.ConnectionString = builder.Configuration["Redis:ConnectionString"]!;
        o.ApplicationName = "northwind";          // anahtar öneki; tüm sunucularda aynı
    })
    .PersistDataProtectionKeys()                  // şifreli cookie'ler her sunucuda çözülür
    .UseForRateLimiting()                         // AddCanRateLimiting sınırları ortak sayılır
    .UseForDistributedLocks();                    // IDistributedLock sunucular arası

builder.Services.AddCanSignalR().AddCanRedisBackplane(); // bildirim, kullanıcı hangi sunucudaysa ona gider
```

| Özellik | Olmazsa ne olur | Nasıl |
|---|---|---|
| Data Protection anahtarları | Bir sunucunun şifrelediği 2FA / OAuth / passkey cookie'sini diğeri çözemez; kullanıcı ikinci adımda hata alır | Anahtarlar Redis listesinde (kendi `IXmlRepository`'miz) |
| SignalR backplane | Bildirim yalnızca aynı sunucuya bağlı kullanıcılara gider | Microsoft'un SignalR Redis paketi (yazması büyük iş), bizim bağlantımızla |
| Hız sınırı | Her sunucu ayrı sayar: 3 sunucuda giriş sınırı fiilen 3 katı | Redis'te sabit/kayan pencere, tek Lua çağrısı; Redis düşerse istekler geçirilir (site durmaz) ve uyarı loglanır |
| Dağıtık kilit | Aynı iş iki sunucuda aynı anda çalışabilir | `SET NX PX` + jetonla bırakma; süre dolunca kendiliğinden düşer |

`IDistributedLock` (Application.Abstractions) uygulama kodunda kullanılır; Redis yoksa tek süreçlik bellek içi
uygulaması çalışır:

```csharp
await using IAsyncDisposable? handle = await locks.TryAcquireAsync($"reorder-report:{tenantId}", TimeSpan.FromMinutes(5), cancellationToken: ct);
if (handle is null)
    return; // başka bir sunucu çalıştırıyor
```

StackExchange.Redis 3.x çıktı, ancak Microsoft'un Redis paketleri (önbellek, SignalR) 2.x'e göre derlendiği için en son
2.x (2.13) kullanılıyor; Microsoft 3.x'e geçince yükseltilir. Gerçek Redis'e karşı testler:
`CAN_REDIS_URL=localhost:6379 dotnet test --project tests/Can.Core.Redis.ScaleOut.Tests`.

## Localization

Metinler JSON dosyalarında: `Localization/tr.json`, `Localization/en.json` (çıktıya kopyala) ya da gömülü
`*.tr.json` kaynakları (`<EmbeddedResource Include="Localization\*.json" WithCulture="false" />`; `WithCulture="false"`
olmazsa MSBuild dosyayı uydu (satellite) assembly'ye koyar). İç içe yazılabilir (`{"product": {"not_found": "..."}}` → `product.not_found`), yorum serbest.

```csharp
builder.Services.AddCanLocalization(o =>
{
    o.DefaultCulture = "tr";
    o.SupportedCultures = ["tr", "en"];
    o.ResourceAssemblies.Add(typeof(ProductErrors).Assembly);   // gömülü çeviriler (isteğe bağlı)
});

app.UseCanRequestLocalization();   // ?culture=en → cookie → Accept-Language → varsayılan; en başlara
app.UseCanExceptionHandler();
```

- Her yerde standart `IStringLocalizer` / `IStringLocalizer<T>` kullanılır (tek, paylaşılan sözlük).
- **Hatalar kodlarıyla çevrilir:** `Error.Failure("product.out_of_stock", "Stok yetersiz.").WithMetadata("name", "Chai")`
  için sözlükte `"product.out_of_stock": "{name} ürününden ..."` varsa ProblemDetails `detail`'i o dilde, yer tutucular
  metadata'dan doldurulur; yoksa `Description` kullanılır. Başlıklar ve `unauthorized` / `forbidden` / `unexpected`
  için tr/en metinler WebApi paketinde gömülü gelir; uygulama dosyaları hepsini ezer.
- FluentValidation mesajları zaten isteğin diline göre gelir (kendi çevirileri var).
- Kültür zinciri: `en-GB` → `en` → varsayılan kültür; hiç yoksa anahtarın kendisi döner (`ResourceNotFound`).

## Arama (Elasticsearch)

Tam metin arama için `ISearchIndex<TDocument>`. Motor seçilmezse bellek içi motor çalışır (geliştirme/test);
`UseElasticsearch` ile Elasticsearch 8/9 ya da OpenSearch 2+. Resmî istemci kullanılmaz: REST API HttpClient ve
System.Text.Json ile çağrılır.

```csharp
public sealed record ProductDocument(string Id, string Name, string Category, decimal Price, bool Discontinued) : ISearchDocument;

builder.Services.AddCanSearch()
    .AddIndex<ProductDocument>("products", m => m
        .Text("name", "turkish", withKeyword: true)   // Türkçe kök bulma; name.keyword: sıralama/filtre
        .Keyword("category")                          // filtre ve toplama
        .Double("price"))
    .UseElasticsearch(o => builder.Configuration.GetSection("Search:Elasticsearch").Bind(o)); // Url, ApiKey, IndexPrefix

await app.Services.EnsureCanSearchIndexesAsync(); // dizinleri (yoksa) eşlemesiyle oluştur

// yazma
await products.IndexAsync(new ProductDocument(p.Id.ToString(), p.Name, category, p.UnitPrice, false), ct);
await products.IndexManyAsync(allDocuments, ct);   // _bulk, 500'lük paketler; başarısızlar sonuçta döner
await products.DeleteAsync(id, ct);

// arama
SearchResult<ProductDocument> result = await products.SearchAsync(new SearchQuery
{
    Text = "cikolata",                       // bulanık: "çikolata" da bulunur
    Fields = { "name^3", "category" },       // ^3: addaki eşleşme 3 kat önemli
    Prefix = true,                           // yazarken arama: "çik" → "çikolata"
    Filters = { SearchFilter.Equal("discontinued", false), SearchFilter.Range("price", lte: 50) },
    Sort = { SearchSort.Desc("price") },     // boşsa alaka skoru
    Highlight = { "name" },                  // hit.Highlights["name"] → "Bitter <em>Çikolata</em>"
    Aggregations = { SearchAggregation.Terms("categories", "category"), SearchAggregation.Stats("prices", "price") },
    Page = 0,
    Size = 20,
}, ct);
```

| | |
|---|---|
| Tenant yalıtımı | Varsayılan açık: her belgeye aktif tenant yazılır (`canTenant`), kimlikler `{tenant}:{id}` olur, her sorguya tenant filtresi eklenir. Tek dizini tüm tenant'lar paylaşır; bir tenant kimlik tahmin ederek bile başkasının belgesine ulaşamaz |
| Yazmadan sonra görünürlük | `Refresh`: `None` (varsayılan, ~1 sn sonra), `WaitFor` (istek belge görünene kadar bekler), `Immediate` (yalnızca test) |
| Kimlik doğrulama | `ApiKey` (Elastic Cloud) ya da `Username`/`Password`; anahtarı user-secrets'a yaz |
| Elastic Cloud | `CloudId` verilirse adres ondan çözülür (dağıtım sayfasındaki "Cloud ID") |
| Birden çok düğüm | `Nodes`: istekler sırayla dağıtılır; ulaşılamayan ya da 502/503/504 dönen düğüm devre dışı kalır (60 sn'den 30 dk'ya artan), istek diğer düğümle tekrarlanır |
| Toplu yazma | 500'lük paketler; küme meşgulse (429) yalnızca reddedilen belgeler beklenip tekrar gönderilir (`BulkRetries`, `BulkRetryDelay`) |
| Sıkıştırma | `EnableCompression`: istek gövdeleri gzip; yanıtlar her zaman açılır |
| Derin sayfalama | `(Page + 1) × Size` en fazla 10.000 (`index.max_result_window`) |
| Dayanıklılık | `services.AddHttpClient(ElasticsearchClient.HttpClientName).AddCanStandardResilienceHandler()` |
| Özel istekler | `ElasticsearchClient.SendAsync(HttpMethod.Post, "/products/_search", jsonBody)`: alias, reindex, özel DSL |
| Gözlemlenebilirlik | `Can.Core.Search` span'ları (`search.search products`); HTTP çağrıları altında |

Veritabanı esas kaynaktır, dizin bir kopyadır: entity değişince dizini güncelle (domain event handler'ı), arama
sonucundaki kimliklerle güncel veriyi veritabanından oku ve dizini istendiğinde baştan kurabil (`DeleteAllAsync` +
`IndexManyAsync`). Bellek içi motor kelime eşleşmesi, tek harflik yazım toleransı, filtre, sıralama ve toplama yapar;
kök bulma ve gerçek alaka sıralaması için Elasticsearch gerekir.

**Resmî istemciyle karşılaştırma.** Elastic'in .NET istemcisi (Elastic.Clients.Elasticsearch, altında
Elastic.Transport; Apache 2.0) incelendi. Düğüm havuzu, devre dışı kalma eğrisi (`DeadTimeout`/`MaxDeadTimeout`),
yalnızca 502/503/504 ve bağlantı hatalarında başka düğüme geçme, toplu yazmada yalnızca 429'ları tekrar deneme ve
Cloud ID biçimi onunla aynıdır. Bilinçli farklar: tüm API'yi tipli sunmak yerine yaygın arama işlemleri
(`ISearchIndex`) + ham istek (`ElasticsearchClient.SendAsync`); düğüm keşfi (sniffing) ve ping yok (yük dengeleyici ya
da sabit düğüm listesi); `application/vnd.elasticsearch+json; compatible-with=N` yerine düz `application/json` (OpenSearch
ile de çalışsın diye).

Gerçek sunucuya karşı entegrasyon testi:
`CAN_ELASTICSEARCH_URL=http://localhost:9200 dotnet test --project tests/Can.Core.Search.Tests`.

## Raporlama

Son kullanıcının kendi raporunu kurduğu pivot raporlama (DevExpress PivotGrid / Excel PivotTable benzeri). Üç .NET
paketi ve bir React paketi: motor (`Can.Core.Reporting`), kaydedilmiş raporlar (`.EntityFrameworkCore`), HTTP uçları
(`.AspNetCore`) ve arayüz (`clients/reporting-react`, npm adı `@can-core/reporting-react`). Dış paket yok: Excel
(OpenXML) ve PDF dosyaları elle yazılır.

```csharp
// veri kaynakları: herhangi bir IQueryable (EF sorgusu: tenant filtresi kendiliğinden uygulanır)
builder.Services.AddCanReporting()
    .AddSource("sales", sp => sp.GetRequiredService<AppDbContext>().SalesRows, o =>
    {
        o.Caption = "Satışlar";
        o.Permission = "reports.sales";          // yetkisi olmayan kaynağı göremez/çalıştıramaz (Admin hepsini)
        o.Field("lineTotal", "Tutar", "N2").Hide("tenantId");
    })
    .UseEntityFrameworkStore<AppDbContext>();     // kayıtlı raporlar; DbContext'te modelBuilder.ApplyCanReportingModel()

app.MapCanReporting("/api/reporting", o => o.AdminRole = "Admin");
```

Kod içinden (arka plan işi, e-posta eki):

```csharp
ReportResult result = await reports.RunAsync(new ReportDefinition
{
    DataSource = "sales",
    CalculatedFields = { new("margin", "[lineTotal] - [cost]") },
    Filters = { ReportFilter.Between("orderDate", new DateTime(2025, 1, 1), new DateTime(2025, 12, 31)) },
    Rows = { new ReportDimension("category"), new ReportDimension("product") { Top = 5, Sort = DimensionSort.MeasureDescending, SortByMeasure = "revenue" } },
    Columns = { new ReportDimension("orderDate") { DateGrouping = DateGrouping.Quarter } },
    Measures =
    {
        new ReportMeasure("lineTotal", ReportAggregate.Sum) { Name = "revenue", Caption = "Ciro", Format = "N2" },
        new ReportMeasure("lineTotal", ReportAggregate.Sum) { Name = "share", Display = MeasureDisplay.PercentOfColumnTotal },
    },
}, ct);
byte[] xlsx = ReportExporter.Export(result, ReportExportFormat.Xlsx, new() { Title = "Çeyreklik satış" });
```

| Uç | |
|---|---|
| `GET /sources`, `GET /sources/{name}` | Kullanıcının erişebildiği kaynaklar ve alanları (ad, başlık, tip, biçim) |
| `POST /run` | `ReportDefinition` → `ReportResult` (satır/sütun başlıkları, değerler, `mode`: `database`/`memory`) |
| `POST /export` | `{ definition, format: Xlsx/Pdf/Csv, title, subtitle, pdfPageSize }` → dosya |
| `GET/POST /saved`, `GET/PUT/DELETE /saved/{id}` | Kayıtlı raporlar: kendi raporların + tenant'ta paylaşılanlar; değiştirme/silme sahibe (paylaşılanı Admin de silebilir) |

Hatalar ProblemDetails: 400 `report.invalid` (ifade hatası, bilinmeyen alan ...), 403 `report.forbidden`, 404
`report.not_found`. Başka tenant'ın ya da başkasının paylaşılmamış raporu 404 döner (varlığı bile sızmaz).
Seçenekler: `Permission` (raporlamaya genel yetki), `SharePermission` (paylaşma yetkisi), `MaxSavedReportsPerUser`.

**Nasıl hesaplanır.** DevExpress'in pivot motoru incelenerek aynı iş akışı kuruldu: alanlar dört bölgeye (filtre,
satır, sütun, değer) yerleşir; önce filtreler, sonra gruplama ve özet, en sonda gösterim dönüşümleri (yüzde, kümülatif,
sıra) ve Top N uygulanır.

- *Veritabanı modu* (DevExpress "Server Mode"): filtreler WHERE'e, tarih aralıkları ve hesaplanmış alanlar SQL
  ifadelerine, gruplar GROUP BY'a çevrilir; her grup için yalnızca n, Σx, Σx², min, max gelir. Ara ve genel toplamlar
  bu kısmi özetlerin birleştirilmesiyle bulunur (varyans dahil; Chan'in birleştirme formülü). Milyonlarca satır
  belleğe hiç gelmez.
- *Bellek modu*: ortanca, yüzdelik, farklı değer sayısı, ISO hafta gibi SQL'e çevrilemeyen tanımlar kendiliğinden
  satırları okuyup tek geçişte hesaplar. Her satır, satır yolunun ve sütun yolunun tüm öneklerine eklenir; ara toplamlar
  ham veriden gelir (ortalama ortalamaların ortalaması değildir). Varyans Welford ile sayısal olarak kararlıdır.
- İki modun sonucu aynıdır; hangisinin kullanıldığı `result.Mode`'da.

**Arayüz.** `clients/reporting-react` her React projesinde kullanılır; bağımlılığı yalnızca React:

```tsx
import { createReportingClient, ReportDesigner } from "@can-core/reporting-react";
import "@can-core/reporting-react/styles.css";

const client = createReportingClient({ baseUrl: "/api/reporting" }); // bileşen dışında bir kez
<ReportDesigner client={client} locale="tr-TR" onNotify={(m, kind) => toast[kind](m)} />
```

Sürükle-bırak tasarımcı (alan listesi, dört bölge, öğe ayarları, hesaplanmış alan), ara toplamlı ve daraltılabilir
tablo, SVG grafikler (sütun, çizgi, pasta), Excel/PDF/CSV, kaydet/paylaş. `PivotTable` ve `ReportChart` tek başına da
kullanılır (ör. panoda kayıtlı bir raporun sonucu). Metinler `labels` ile çevrilir; renkler `--cr-*` CSS
değişkenleriyle uygulamanın temasına bağlanır (örnek: Northwind `index.css`). Kurulum: uygulamanın `package.json`'ına
`"@can-core/reporting-react": "file:<yol>/clients/reporting-react"` ve `.npmrc`'ye `install-links=true` (paket kopya
olarak kurulur, React tek kopya kalır). Paket değişince `npm install @can-core/reporting-react`.

## Dosya depolama

```csharp
builder.Services.AddCanLocalFileStorage(o => o.RootPath = "/var/app-files");               // yerel / ağ diski
builder.Services.AddCanS3FileStorage(o => builder.Configuration.GetSection("Storage:S3").Bind(o)); // ya da S3 uyumlu

// handler: yollar göreli, "/" ile ayrılır
await storage.SaveAsync($"products/{id}/photo.jpg", stream, new FileSaveOptions { Overwrite = true }, ct);
await using Stream? file = await storage.OpenReadAsync($"products/{id}/photo.jpg", ct);
Uri? link = await storage.GetTemporaryUrlAsync(path, TimeSpan.FromMinutes(10), ct);   // S3: presigned; yerel: null
```

- **Tenant yalıtımı** varsayılan: yollar otomatik `tenants/{tenantId}/...` (tenant yoksa `host/...`) altına gider;
  bir tenant diğerinin dosyasını adını bilse de okuyamaz. Kapatmak: `configureStorage: o => o.TenantIsolation = false`.
- `..`, mutlak yol, ters bölü ve kontrol karakterleri reddedilir (dizin dışına çıkma yok).
- Yerel disk: yazma geçici dosyaya yapılıp yerine taşınır (yarım dosya okunmaz); içerik tipi uzantıdan çıkarılır.
  Kök klasörü `wwwroot` altına koyma; dosyaları yetki kontrolü yapan bir endpoint'ten ver.
- S3: AWS SDK yok; `HttpClient` + Signature V4. MinIO/R2 için `ForcePathStyle = true` (varsayılan), R2'de
  `Region = "auto"`. `Overwrite = false` iken `If-None-Match: *` ile yazar. Gövde imzalanmaz (`UNSIGNED-PAYLOAD`,
  HTTPS gerekir); anahtarlar user-secrets'ta.

## Anlık bildirim (SignalR)

```csharp
builder.Services.AddCanSignalR();            // SignalR ASP.NET Core'un içinde: ek paket yok
app.UseAuthentication();
app.MapCanRealtimeHub();                     // /hubs/notifications, yalnızca giriş yapmış kullanıcılar

// Application katmanı yalnızca soyutlamayı bilir (Can.Core.Realtime)
public sealed class NotifyWarehouse(IRealtimeNotifier realtime) : INotificationHandler<OrderPlaced>
{
    public Task Handle(OrderPlaced e, CancellationToken ct) =>
        realtime.SendToRoleAsync("Warehouse", new RealtimeMessage("order.placed", new { e.OrderId }), ct);
}
```

```ts
// istemci (npm i @microsoft/signalr): kimlik HttpOnly cookie'deki JWT ile gider
const connection = new HubConnectionBuilder().withUrl("/hubs/notifications").withAutomaticReconnect().build();
connection.on("notify", (m) => { /* m.type, m.data, m.createdAt */ });
await connection.start();
```

- Bağlantı açılınca kullanıcı otomatik olarak gruplara girer: tenant'ı, kendisi ve her rolü. Tüm grup adları tenant
  ile başlar; bir mağazanın bildirimi başka mağazaya gitmez (aynı kullanıcı id'si başka tenant'ta olsa bile).
- `SendToUserAsync` / `SendToRoleAsync` / `SendToTenantAsync` / `SendToGroupAsync` her zaman AKTİF tenant'a gider;
  arka plan işlerinde ve outbox handler'larında tenant zaten işin tenant'ıdır.
- Özel gruplar (`order:42`): istemci `invoke("Subscribe", "order:42")` ile katılmak ister; izin
  `IRealtimeSubscriptionAuthorizer`'dadır (varsayılan: hiçbiri).
- SignalR kurulu değilse `IRealtimeNotifier` hiçbir şey yapmaz (`AddCanRealtimeDefaults`); Application kodu her ortamda çalışır.
- Mesaj taşıyıcıdır, depolama değil: istemci bağlı değilse kaybolur. Birden fazla sunucuda backplane gerekir
  (`Can.Core.Redis.ScaleOut`: `AddCanSignalR().AddCanRedisBackplane()`).

## Dayanıklılık (resilience)

Dış servislere (ödeme, SMS, e-posta, başka bir API) yapılan çağrılar bazen yavaşlar ya da geçici hata verir.
Polly'nin stratejileri dış paket olmadan:

| Strateji | Ne yapar |
|---|---|
| Retry | Geçici hatada bekleyip tekrar dener: sabit / doğrusal / üstel bekleme, ±%25 jitter, üst sınır, `DelayGenerator` (Retry-After) |
| Circuit breaker | Kayan pencerede hata oranı eşiği aşınca devreyi açar, bir süre denemeden reddeder (`BrokenCircuitException`), sonra tek istekle yoklar; durum okuma ve elle izole/kapatma |
| Timeout | İşe süre sınırı; token'ı iptal eder, `TimeoutRejectedException` |
| Rate limiter / bulkhead | `System.Threading.RateLimiting` ile sınırlar; `AddConcurrencyLimiter(n)` yavaş bir bağımlılığın tüm kaynakları tüketmesini önler |
| Fallback | Hata olursa yedek sonuç (önbellek, varsayılan) |
| Hedging | Yanıt gecikirse aynı işi paralel başlatır, ilk iyi sonuç kazanır (idempotent işler için) |

```csharp
ResiliencePipeline pipeline = new ResiliencePipelineBuilder()
    .AddTimeout(TimeSpan.FromSeconds(30))                       // toplam (en dış)
    .AddRetry(new RetryOptions { MaxRetryAttempts = 3, BackoffType = DelayBackoffType.Exponential, UseJitter = true })
    .AddCircuitBreaker(new CircuitBreakerOptions { FailureRatio = 0.5, MinimumThroughput = 20 })
    .AddTimeout(TimeSpan.FromSeconds(5))                        // deneme başına (en iç)
    .Build();

Order order = await pipeline.ExecuteAsync(ct => client.GetOrderAsync(id, ct), cancellationToken);

// Sonuca bakan pipeline: ShouldHandle ile hangi sonuçların "hata" olduğu belirlenir
new ResiliencePipelineBuilder<HttpResponseMessage>()
    .AddRetry(new RetryOptions<HttpResponseMessage>
    {
        ShouldHandle = new PredicateBuilder<HttpResponseMessage>().Handle<HttpRequestException>().HandleResult(r => (int)r.StatusCode >= 500),
    });

// Adı verilmiş, paylaşılan pipeline (circuit breaker durumu uygulama genelinde tek)
services.AddCanResiliencePipeline("payments", (b, sp) => b.AddRetry(new RetryOptions()).AddCircuitBreaker(new CircuitBreakerOptions()));
provider.GetPipeline("payments");

// HttpClient: bulkhead → toplam süre → retry (5xx/408/429, Retry-After) → circuit breaker → deneme süresi
services.AddHttpClient<PaymentClient>().AddCanStandardResilienceHandler(o => o.Retry.MaxRetryAttempts = 5);
```

`tests/Can.Core.Resilience.Comparison.Tests` aynı senaryoları Polly ile de çalıştırıp davranışı karşılaştırır
(deneme sayısı, bekleme süreleri, devre durum geçişleri, exception tipleri) ve kaba bir ek yük ölçümü yazar
(ölçümü anlamlı yapmak için Release: `dotnet test --project tests/Can.Core.Resilience.Comparison.Tests -c Release --show-live-output on`).
Polly yalnızca bu test projesindedir.

Strateji sırası önemlidir: ilk eklenen en dıştadır. Timeout iyimserdir: iş, verilen `CancellationToken`'a uymalı.
Retry'da HTTP isteği aynı nesneyle yeniden gönderilir; gövde tekrar okunabilir olmalı (`StringContent`, `JsonContent`).

## Gözlemlenebilirlik (observability)

Paketler telemetriyi .NET'in kendi API'leriyle (`ActivitySource`, `Meter`) üretir; ek paket yok, dinleyen yoksa maliyet
yok denecek kadar az. Dışa aktarmak için tek satır:

```csharp
builder.Services.AddCanOpenTelemetry(o =>
{
    o.ServiceName = "myapp";
    o.OtlpEndpoint = new Uri("http://localhost:4317");   // Aspire Dashboard, Jaeger, Grafana Tempo/Mimir, Seq, Datadog ...
    o.AdditionalSources.Add("Npgsql");                   // SQL komutları
});
```

| Kaynak | Span | Metrik |
|---|---|---|
| Application | her mediator isteği (`can.outcome`: success / failure / exception, `can.error.code`) | `can.request.duration` |
| Mediator | `publish {Event}` (domain event yayını) | |
| BackgroundJobs | `job {İş}` (bellek içi, tekrarlayan, Hangfire) | `can.job.duration` |
| Persistence | `outbox {Event}` | `can.outbox.messages` (published / failed) |
| EventBus | `publish` (producer) → `process` (consumer); `traceparent` zarfla taşınır, servisler arası tek iz | |
| Mailing | `email send {sağlayıcı}` (adres/konu yazılmaz) | `can.email.messages` |
| Resilience | o anki span'a olay (`resilience.retry.retry` ...) | `can.resilience.events` |
| .NET | ASP.NET Core istekleri, HttpClient çağrıları | Kestrel, routing, rate limiting, HttpClient, runtime (GC, thread pool) |

Instrumentation paketlerine gerek yok: .NET 10'da ASP.NET Core, HttpClient ve runtime telemetriyi zaten üretiyor,
`AddCanOpenTelemetry` yalnızca dinler. Serilog logları `TraceId`/`SpanId` içerir (log ↔ iz eşleşir).

## Sağlık kontrolleri

```csharp
builder.Services.AddHealthChecks()
    .AddCanDbContextCheck<AppDbContext>()   // Persistence: CanConnectAsync
    .AddCanRedisCheck()                     // Caching.Redis: yaz/oku (Degraded)
    .AddCanSmtpCheck()                      // Mailing.MailKit: bağlan + giriş (Degraded)
    .AddCanHangfireCheck();                 // BackgroundJobs.Hangfire: çalışan sunucu var mı (Degraded)

app.MapCanHealthChecks();   // /health/live (kontrol yok) + /health/ready ("ready" etiketliler)
```

Ek paket yok (ASP.NET Core'un yerleşik health check'leri). `Healthy`/`Degraded` 200, `Unhealthy` 503; yanıt kısa
JSON (durum, süre, kontrol başına durum/açıklama); exception ayrıntısı yazılmaz. Kritik olmayan bağımlılıklar
`Degraded` döner: uygulama trafikten çıkarılmaz ama izleme uyarı verir.

## Hız sınırlama

```csharp
builder.Services.AddCanRateLimiting(o => o.Auth = new() { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) });

app.UseAuthentication();
app.UseCanTenantResolution();
app.UseCanRateLimiting();   // kullanıcı/tenant bilindikten sonra

auth.MapPost("/login", ...).RequireRateLimiting(CanRateLimitPolicies.Auth);        // IP başına sıkı
auth.MapPost("/sms/send", ...).RequireRateLimiting(CanRateLimitPolicies.Sensitive); // kullanıcı/IP başına çok sıkı
```

| Politika | Anahtar | Varsayılan |
|---|---|---|
| Genel (tüm istekler) | kullanıcı (yoksa IP) + tenant | 300/dk, kayan pencere |
| `Auth` | IP + tenant | 10/dk |
| `Sensitive` | kullanıcı (yoksa IP) + tenant | 5 / 15 dk |

Aşılınca 429 + `Retry-After` + ProblemDetails (`code: "rate_limited"`). Proxy arkasında `UseForwardedHeaders`
ayarlanmalı (yoksa herkes proxy'nin IP'sini paylaşır). Sınırlar örnek başınadır (bellekte); birden fazla sunucuda
her biri ayrı sayar.

## Logging

```csharp
builder.AddCanSerilog(o => o.FilePath = "logs/app-.json");   // isteğe bağlı dosya

app.UseCanRequestLogging();   // her isteğe tek özet satırı — EN DIŞTA (gerçek durum kodu loglansın)
app.UseCanExceptionHandler();
app.UseAuthentication();
app.UseCanTenantResolution();
app.UseCanLogEnrichment();    // tüm loglara ve özete UserId/TenantId
```

Varsayılanlar: Development'ta okunur konsol, diğer ortamlarda JSON; Microsoft/System kaynakları Warning.
appsettings'teki `"Serilog"` bölümü her şeyi ezebilir (Seq, Elastic vb. için ilgili sink paketini ekleyip `WriteTo`'ya yaz).
İstek/yanıt gövdeleri loglanmaz.


## Arka plan işleri

```csharp
builder.Services.AddCanBackgroundJobs(typeof(Program).Assembly);
builder.Services.AddCanRecurringJob<CleanupExpiredTokensJob>(o => o.Interval = TimeSpan.FromHours(1));

// İstek içinde: hemen döner, iş arka planda isteği atan tenant adına çalışır.
await queue.EnqueueAsync<SendWelcomeEmailJob, WelcomeEmailArgs>(new(user.Id));
```

Kuyruk bellektedir; uygulama kapanınca bekleyen işler kaybolur. Kaybolmaması gereken işler için outbox kullan.
Tekrarlayan işler `PerTenant = true` ile her aktif tenant için ayrı çalıştırılabilir.

### Hangfire

İşler kaybolmamalı, hata alınca yeniden denenmeli ya da birden fazla sunucu varsa Hangfire'a geç. İş sınıfları ve
`IBackgroundJobQueue` kullanımı değişmez; yalnızca kayıt değişir:

```csharp
builder.Services.AddCanBackgroundJobs(typeof(Program).Assembly);
builder.Services.AddCanHangfire(
    c => c.UsePostgreSqlStorage(o => o.UseNpgsqlConnection(connectionString)),   // Hangfire.PostgreSql, SqlServer ...
    o => o.WorkerCount = 4);                                                      // RunServer = false: yalnızca kuyruğa at

builder.Services.AddCanHangfireRecurringJob<DailyReportJob>("0 7 * * *", o =>
{
    o.PerTenant = true;                                                            // her aktif tenant için ayrı iş
    o.TimeZone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul");
});

app.UseAuthentication();
...
app.MapCanHangfireDashboard("/hangfire", "Admin");                                 // giriş + rol gerekli
```

- Kuyruğa atan isteğin tenant id'si işle saklanır; iş çalışırken `ITenantStore`'dan tenant bulunup scope'a yüklenir.
- `PerTenant` tekrarlayan iş, çalıştığında her aktif tenant için ayrı bir iş açar: biri hata alırsa diğerleri etkilenmez,
  her biri kendi başına yeniden denenir.
- Tekrarlayan işler birden fazla sunucuda da tek kez çalışır; dashboard'dan elle tetiklenebilir.
- Argümanlar (`TArgs`) JSON olarak veritabanında durur ve dashboard'da görünür: gizli bilgi koyma, id gönder.
- Dashboard tenant'a göre filtrelenmez, tüm tenant'ların işlerini gösterir; yalnızca sistem yöneticilerine aç.
