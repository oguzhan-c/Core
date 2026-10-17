using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
#if (Hangfire)
using CanAppTemplate.Infrastructure.BackgroundJobs;
#endif
#if (Redis)
using CanAppTemplate.Infrastructure.Caching;
#endif
#if (FileStorage)
using CanAppTemplate.Infrastructure.FileStorage;
#endif
using CanAppTemplate.Infrastructure.Mailing;
#if (EventBus)
using CanAppTemplate.Infrastructure.Messaging;
#endif
#if (Realtime)
using CanAppTemplate.Infrastructure.Realtime;
#endif
#if (Search)
using CanAppTemplate.Infrastructure.Search;
#endif
#if (Sms)
using CanAppTemplate.Infrastructure.Sms;
#endif

namespace CanAppTemplate.Infrastructure;

/// <summary>
/// Dış servisler. Her dış bağlantı kendi klasöründe ve kendi kayıt sınıfındadır (Mailing/, Sms/, FileStorage/, Search/,
/// Messaging/, BackgroundJobs/, Caching/, Realtime/); burası yalnızca hepsini sırayla çağırır. Her biri appsettings'ten
/// seçilir; ayarı boş olan servis için geliştirmeye uygun yerel karşılığı kullanılır (e-posta klasöre, SMS klasöre, dosya
/// diske, arama bellekte, event bus süreç içinde, iş kuyruğu bellekte). Böylece uygulama yalnızca veritabanıyla açılır.
/// </summary>
/// <remarks>
/// Yeni dış bağlantı (ör. ödeme, kargo, harita): kendi klasörünü aç (Payments/), içine istemci / adaptör sınıflarını ve
/// <c>PaymentsServiceRegistration.AddPayments(config)</c> uzantısını yaz, aşağıdaki işaretin üstüne çağrısını ekle.
/// </remarks>
public static class InfrastructureServiceRegistration
{
    /// <summary>
    /// Dış servisleri kaydeder ve hangilerinin gerçekten açık olduğunu döner (sağlık kontrolleri ve Hangfire paneli için).
    /// </summary>
    public static InfrastructureStatus AddInfrastructureServices(this IServiceCollection services, IConfiguration config)
    {
#if (Redis)
        string? redis = services.AddRedis(config);
#else
        string? redis = null;
#endif
        services.AddMail(config);
#if (Sms)
        services.AddSms(config);
#endif
#if (FileStorage)
        services.AddFileStorage(config);
#endif
#if (Search)
        services.AddSearch(config);
#endif
#if (EventBus)
        bool brokerEnabled = services.AddEventBus(config);
#else
        bool brokerEnabled = false;
#endif
#if (Hangfire)
        bool hangfireEnabled = services.AddJobs(config);
        services.AddRecurringJobs(hangfireEnabled);
#else
        bool hangfireEnabled = false;
#endif
#if (Realtime)
        services.AddRealtime(redis);
#endif
        // can-gen:infrastructure-services

        return new InfrastructureStatus(redis, brokerEnabled, hangfireEnabled);
    }
}
