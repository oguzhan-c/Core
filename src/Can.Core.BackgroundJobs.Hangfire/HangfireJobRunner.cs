using Can.Core.MultiTenancy;
using Hangfire;
using Microsoft.Extensions.DependencyInjection;

namespace Can.Core.BackgroundJobs.Hangfire;

// Hangfire generic metotları saklayamaz (yalnızca parametre tiplerini saklar), generic sınıfları saklayabilir.
// Bu yüzden iş tipi sınıfın generic argümanıdır: HangfireJobRunner<SendWelcomeEmailJob>.RunAsync(...).

/// <summary>
/// Hangfire'ın çalıştırdığı parametresiz iş girişi. Hangfire her iş için ayrı bir DI scope'u açar; runner bu scope'ta
/// tenant'ı geri yükler ve asıl <typeparamref name="TJob"/>'u çözüp çalıştırır.
/// </summary>
/// <remarks>
/// Kuyrukta yalnızca iş tipi, tenant id'si ve (varsa) argümanlar saklanır; tenant'ın bağlantı cümlesi gibi ayrıntılar
/// saklanmaz, çalıştırma anında <see cref="ITenantStore"/>'dan okunur.
/// </remarks>
public sealed class HangfireJobRunner<TJob>
    where TJob : class, IBackgroundJob
{
    private readonly IServiceProvider _services;
    private readonly IBackgroundJobClient _client;

    public HangfireJobRunner(IServiceProvider services, IBackgroundJobClient client)
    {
        _services = services;
        _client = client;
    }

    /// <summary>İşi <paramref name="tenantId"/> adına çalıştırır.</summary>
    [JobDisplayName("{0} · tenant: {1}")]
    public async Task RunAsync(string jobName, string? tenantId, CancellationToken cancellationToken)
    {
        await HangfireTenantScope.RestoreAsync(_services, tenantId, cancellationToken).ConfigureAwait(false);
        await ActivatorUtilities.GetServiceOrCreateInstance<TJob>(_services).ExecuteAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Her aktif tenant için ayrı bir iş kuyruğa atar. Böylece bir tenant'taki hata diğerlerini etkilemez ve
    /// her biri kendi başına yeniden denenir.
    /// </summary>
    [JobDisplayName("{0} · tüm tenant'lar")]
    public async Task RunForEachTenantAsync(string jobName, CancellationToken cancellationToken)
    {
        ITenantStore store =
            _services.GetService<ITenantStore>()
            ?? throw new InvalidOperationException($"{jobName} tenant başına çalışacak şekilde ayarlanmış ama ITenantStore kayıtlı değil.");

        IReadOnlyList<TenantInfo> tenants = await store.GetAllAsync(cancellationToken).ConfigureAwait(false);

        foreach (TenantInfo tenant in tenants.Where(t => t.IsActive))
        {
            string tenantId = tenant.Id;
            _client.Enqueue<HangfireJobRunner<TJob>>(r => r.RunAsync(jobName, tenantId, CancellationToken.None));
        }
    }
}

/// <summary>Hangfire'ın çalıştırdığı parametreli iş girişi (bkz. <see cref="HangfireJobRunner{TJob}"/>).</summary>
public sealed class HangfireJobRunner<TJob, TArgs>
    where TJob : class, IBackgroundJob<TArgs>
{
    private readonly IServiceProvider _services;

    public HangfireJobRunner(IServiceProvider services) => _services = services;

    /// <summary>İşi <paramref name="tenantId"/> adına, verilen argümanlarla çalıştırır.</summary>
    [JobDisplayName("{0} · tenant: {1}")]
    public async Task RunAsync(string jobName, string? tenantId, TArgs args, CancellationToken cancellationToken)
    {
        await HangfireTenantScope.RestoreAsync(_services, tenantId, cancellationToken).ConfigureAwait(false);
        await ActivatorUtilities.GetServiceOrCreateInstance<TJob>(_services).ExecuteAsync(args, cancellationToken).ConfigureAwait(false);
    }
}

internal static class HangfireTenantScope
{
    /// <summary>İşin scope'unu kuyruğa atıldığı andaki tenant adına ayarlar (çoklu kiracılık kayıtlıysa).</summary>
    public static async Task RestoreAsync(IServiceProvider services, string? tenantId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(tenantId) || services.GetService<TenantContext>() is not { } context)
            return;

        TenantInfo? tenant = services.GetService<ITenantStore>() is { } store
            ? await store.FindAsync(tenantId, cancellationToken).ConfigureAwait(false)
            : null;

        if (tenant is not null)
            context.Set(tenant);
        else
            context.Set(tenantId);
    }
}
