using Can.Core.BackgroundJobs.Hangfire;
using Hangfire;
#if (UsePostgreSql)
using Hangfire.PostgreSql;
#endif
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CanAppTemplate.Infrastructure.BackgroundJobs;

/// <summary>Arka plan işleri: <c>Hangfire:Enabled</c> ise kalıcı kuyruk + cron + <c>/hangfire</c>, değilse bellek içi.</summary>
public static class HangfireServiceRegistration
{
    /// <returns>Hangfire açıksa <see langword="true"/>.</returns>
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
    public static IServiceCollection AddRecurringJobs(this IServiceCollection services, bool hangfire)
    {
        // TODO(can-gen): Tekrarlayan işlerini kaydet. Örnek:
        //   if (hangfire) services.AddCanHangfireRecurringJob<CleanupJob>("30 3 * * *", o => o.JobId = "cleanup");
        //   else services.AddCanRecurringJob<CleanupJob>(o => o.Interval = TimeSpan.FromHours(6));
        _ = hangfire;
        return services;
    }
}
