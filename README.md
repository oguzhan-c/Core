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
| `Can.Core.MultiTenancy` | yok | `TenantInfo`, `ITenantStore`, `TenantContext`, tenant başına bağlantı dizesi, `CreateTenantScope` |
| `Can.Core.Mailing` | yok | `IEmailSender`, `EmailMessage`, testler için `InMemoryEmailSender` |
| `Can.Core.Mailing.MailKit` | MailKit | SMTP göndericisi, geliştirme için `.eml` klasörü |
| `Can.Core.Logging.Serilog` | Serilog | `AddCanSerilog()`, `UseCanRequestLogging()` |
| `Can.Core.WebApi` | ASP.NET Core | Hata → ProblemDetails, `HttpCurrentUser`, token'dan tenant çözümleme, cookie tabanlı `AddCanJwtAuthentication()` |
| `Can.Core.Persistence.Abstractions` | Domain | `IRepository<TEntity,TId>`, `IUnitOfWork`, `DynamicQuery` (filtre/sıralama), `IPaginate<T>` — EF'e bağımlı değil |
| `Can.Core.Persistence` | EF Core | `CanDbContext`, `EfRepository`, `UnitOfWork`, audit ve domain event interceptor'ları, outbox, değişiklik geçmişi, seed, `AddCanPersistence(...)` |

```bash
dotnet build Core.slnx
dotnet test Core.slnx
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

**Hatalar** exception olarak fırlatılır; WebApi katmanı HTTP koduna çevirir:
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
`EmailAuthenticator<TId>`, `UserPasskey<TId>`.

Tabloları kurmak için (`Can.Core.Security.EntityFrameworkCore`):

```csharp
protected override void ConfigureModel(ModelBuilder modelBuilder) =>
    modelBuilder.ApplyCanSecurityModel<AppUser, Guid>(o => o.Schema = "identity");
```

Roller JWT'ye `role` claim'i olarak yazılır; `ISecuredRequest.Roles`, `ICurrentUser.IsInRole` ve
`[Authorize(Roles = ...)]` aynı claim'i kullanır.

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

## Mailing

```csharp
builder.Services.AddCanMailKit(o => builder.Configuration.GetSection("Mailing:Smtp").Bind(o));   // üretim
builder.Services.AddCanEmailPickupDirectory("mails");                                            // geliştirme: .eml dosyaları
builder.Services.AddCanInMemoryEmail();                                                          // testler

var message = new EmailMessage("Doğrulama kodun") { HtmlBody = $"<p>Kodun: <b>{code}</b></p>", TextBody = $"Kodun: {code}" };
message.To.Add(new EmailAddress(user.Email));
await emailSender.SendAsync(message, ct);
```

E-postayı istek içinde değil, domain event handler'ından (ileride arka plan işinden) göndermek önerilir.

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
