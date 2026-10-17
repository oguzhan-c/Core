#if (Hangfire)
using Can.Core.BackgroundJobs.Hangfire;
#endif
#if (Redis)
using Can.Core.Caching.Redis;
#endif
#if (EventBus)
using Can.Core.EventBus;
using Can.Core.EventBus.RabbitMQ;
#endif
#if (FileStorage)
using Can.Core.FileStorage;
using Can.Core.FileStorage.AzureBlob;
using Can.Core.FileStorage.S3;
#endif
using Can.Core.Mailing.MailKit;
using Can.Core.Mailing.SendGrid;
#if (EventBus)
using Can.Core.Persistence.DependencyInjection;
#endif
#if (Realtime)
using Can.Core.Realtime.SignalR;
#endif
#if (Redis)
using Can.Core.Redis.ScaleOut;
#endif
#if (Search)
using Can.Core.Search;
using Can.Core.Search.Elasticsearch;
#endif
#if (Sms)
using Can.Core.Sms;
#endif
#if (Hangfire)
using Hangfire;
#endif
#if (Hangfire && UsePostgreSql)
using Hangfire.PostgreSql;
#endif
#if (EventBus)
using CanAppTemplate.Persistence;
#endif

namespace CanAppTemplate.WebApi.Configuration;

/// <summary>
/// Dış servisler. Her biri appsettings'ten seçilir; ayarı boş olan servis için geliştirmeye uygun yerel karşılığı
/// kullanılır (e-posta klasöre, SMS klasöre, dosya diske, arama bellekte, event bus süreç içinde, iş kuyruğu bellekte).
/// Böylece uygulama yalnızca veritabanıyla açılır; docker compose'daki servisler tek tek açılabilir.
/// </summary>
internal static class ExternalServices
{
#if (Redis)
    /// <summary>Redis: doluysa çok sunuculu kurulum (önbellek, Data Protection anahtarları, hız sınırı, kilit, çıktı önbelleği).</summary>
    public static string? AddRedis(this IServiceCollection services, IConfiguration config)
    {
        string? redis = config["Redis:ConnectionString"] is { Length: > 0 } value ? value : null;
        if (redis is null)
            return null;

        services.AddCanRedisCache(o =>
        {
            o.ConnectionString = redis;
            o.InstanceName = "canapptemplate:";
        });

        services.AddCanRedisScaleOut(o =>
            {
                o.ConnectionString = redis;
                o.ApplicationName = "canapptemplate";
            })
            .PersistDataProtectionKeys()
            .UseForRateLimiting()
            .UseForDistributedLocks()
            .UseForOutputCache();

        return redis;
    }

#endif
    /// <summary>E-posta: <c>Mail:Provider</c> = Pickup | Smtp | SendGrid (boşsa Pickup: <c>mails</c> klasörüne .eml).</summary>
    public static void AddMail(this IServiceCollection services, IConfiguration config)
    {
        string provider = config["Mail:Provider"] is { Length: > 0 } value ? value : "Pickup";
        // TODO(can-gen): Gönderen adresini kendi alan adınla değiştir.
        string from = config["Mail:From"] ?? "no-reply@canapptemplate.local";

        switch (provider.ToUpperInvariant())
        {
            case "SMTP":
                // Mailpit (docker compose) ya da gerçek SMTP; şifre user-secrets'ta: "Mail:Smtp:Password"
                services.AddCanMailKit(o => config.GetSection("Mail:Smtp").Bind(o));
                break;
            case "SENDGRID":
                // dotnet user-secrets set "Mail:SendGrid:ApiKey" "<anahtar>"
                services.AddCanSendGrid(o => config.GetSection("Mail:SendGrid").Bind(o));
                break;
            default:
                services.AddCanEmailPickupDirectory(config["Mail:PickupDirectory"] is { Length: > 0 } dir ? dir : "mails", from, "CanAppTemplate");
                break;
        }
    }
#if (Sms)

    /// <summary>SMS: geliştirmede her mesaj <c>sms</c> klasörüne JSON olarak yazılır.</summary>
    /// <remarks>TODO(can-gen): Gerçek SMS sağlayıcısını (Can.Core.Sms ISmsSender) bağla.</remarks>
    public static void AddSms(this IServiceCollection services, IConfiguration config) =>
        services
            .AddCanSms(o =>
            {
                o.DefaultSender = "APP";
                o.TransliterateToGsm = true;
                o.MaxSegments = 3;
            })
            .UsePickupDirectory(config["Sms:PickupDirectory"] is { Length: > 0 } dir ? dir : "sms");
#endif
#if (FileStorage)

    /// <summary>Dosyalar: <c>Storage:Provider</c> = Local | S3 | Azure. Dosyalar varsayılan olarak tenant klasörüne yazılır.</summary>
    public static void AddFileStorage(this IServiceCollection services, IConfiguration config)
    {
        switch ((config["Storage:Provider"] ?? "Local").ToUpperInvariant())
        {
            case "S3":
                // SeaweedFS / MinIO / AWS / R2; anahtar user-secrets'ta: "Storage:S3:SecretAccessKey"
                services.AddCanS3FileStorage(o => config.GetSection("Storage:S3").Bind(o));
                break;
            case "AZURE":
                // Azurite: "UseDevelopmentStorage=true"; gerçek hesap anahtarı user-secrets'ta
                services.AddCanAzureBlobFileStorage(o => config.GetSection("Storage:Azure").Bind(o));
                break;
            default:
                services.AddCanLocalFileStorage(o => o.RootPath = config["Storage:Local:RootPath"] is { Length: > 0 } root ? root : "files");
                break;
        }
    }
#endif
#if (Search)

    /// <summary>Arama: <c>Search:Elasticsearch:Url</c> doluysa Elasticsearch, yoksa bellek içi motor.</summary>
    /// <remarks>TODO(can-gen): Aranacak belgeleri ve dizinleri tanımla (Can.Core.Search).</remarks>
    public static void AddSearch(this IServiceCollection services, IConfiguration config)
    {
        CanSearchBuilder search = services.AddCanSearch();
        if (config["Search:Elasticsearch:Url"] is { Length: > 0 })
            search.UseElasticsearch(o => config.GetSection("Search:Elasticsearch").Bind(o));
    }
#endif
#if (EventBus)

    /// <summary>
    /// Event bus: outbox'taki entegrasyon event'leri bus'a verilir. <c>EventBus:RabbitMq:ConnectionString</c> doluysa
    /// RabbitMQ (servisler arası, inbox ile tam bir kez işleme); yoksa süreç içi taşıyıcı.
    /// </summary>
    public static bool AddEventBus(this IServiceCollection services, IConfiguration config)
    {
        services.AddCanEventBus(typeof(CanAppTemplate.Domain.Identity.AppUser).Assembly, typeof(CanAppTemplate.Application.DependencyInjection).Assembly);

        if (config["EventBus:RabbitMq:ConnectionString"] is not { Length: > 0 })
            return false;

        services.AddCanInbox<AppDbContext>();
        services.AddCanRabbitMqTransport(o => config.GetSection("EventBus:RabbitMq").Bind(o));
        return true;
    }
#endif
#if (Hangfire)

    /// <summary>Arka plan işleri: <c>Hangfire:Enabled</c> ise kalıcı kuyruk + cron + <c>/hangfire</c>, değilse bellek içi.</summary>
    public static bool AddJobs(this IServiceCollection services, IConfiguration config)
    {
        if (!config.GetValue("Hangfire:Enabled", false))
            return false;

#if (UsePostgreSql)
        string connectionString = config.GetConnectionString("CanAppTemplate") ?? "";
        services.AddCanHangfire(
            c => c.UsePostgreSqlStorage(o => o.UseNpgsqlConnection(connectionString)),
            o => o.WorkerCount = config.GetValue<int?>("Hangfire:WorkerCount")
        );
#elif (UseSqlServer)
        string connectionString = config.GetConnectionString("CanAppTemplate") ?? "";
        services.AddCanHangfire(
            c => c.UseSqlServerStorage(connectionString),
            o => o.WorkerCount = config.GetValue<int?>("Hangfire:WorkerCount")
        );
#else
        // TODO(can-gen): SQLite için kalıcı Hangfire deposu yok; bellek içi depo kullanılıyor (yeniden başlatınca işler kaybolur).
        services.AddCanHangfire(
            c => c.UseInMemoryStorage(),
            o => o.WorkerCount = config.GetValue<int?>("Hangfire:WorkerCount")
        );
#endif
        return true;
    }

    /// <summary>
    /// Tekrarlayan işler: Hangfire açıksa cron ile (çok sunucuda tek kez, dashboard'dan elle tetiklenebilir), kapalıysa
    /// <c>Can.Core.BackgroundJobs</c>'ın bellek içi zamanlayıcısıyla (her örnekte çalışır).
    /// </summary>
    public static void AddRecurringJobs(this IServiceCollection services, bool hangfire)
    {
        // TODO(can-gen): Tekrarlayan işlerini kaydet. Örnek:
        //   if (hangfire) services.AddCanHangfireRecurringJob<CleanupJob>("30 3 * * *", o => o.JobId = "cleanup");
        //   else services.AddCanRecurringJob<CleanupJob>(o => o.Interval = TimeSpan.FromHours(6));
        _ = services;
        _ = hangfire;
    }
#endif
#if (Realtime)

    /// <summary>Anlık bildirim (SignalR); Redis varsa sunucular arası backplane.</summary>
    public static void AddRealtime(this IServiceCollection services, string? redis)
    {
        var signalR = services.AddCanSignalR();
#if (Redis)
        if (redis is not null)
            signalR.AddCanRedisBackplane();
#else
        _ = redis;
        _ = signalR;
#endif
    }
#endif
}
