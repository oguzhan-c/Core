using CanFx.BackgroundJobs.Hangfire;
using Hangfire;
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

        // TODO(can-gen): Hangfire deposu bellek içi (yeniden başlatınca işler kaybolur). Kalıcı depo için veritabanına uygun
        // paketi ekle ve aşağıdakini değiştir (bağlantı: config.GetConnectionString("CanAppTemplate")):
        //   PostgreSQL (Hangfire.PostgreSql): c => c.UsePostgreSqlStorage(o => o.UseNpgsqlConnection(connectionString))
        //   SQL Server (Hangfire.SqlServer) : c => c.UseSqlServerStorage(connectionString)
        services.AddCanHangfire(
            c => c.UseInMemoryStorage(),
            o => o.WorkerCount = config.GetValue<int?>("Hangfire:WorkerCount")
        );
        return true;
    }

    /// <summary>
    /// Tekrarlayan işler: Hangfire açıksa cron ile (çok sunucuda tek kez, dashboard'dan elle tetiklenebilir), kapalıysa
    /// <c>CanFx.BackgroundJobs</c>'ın bellek içi zamanlayıcısıyla (her örnekte çalışır).
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
