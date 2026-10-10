using Can.Core.Persistence.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Can.Core.Persistence.HealthChecks;

/// <summary>
/// Outbox sağlık kontrolünün eşikleri.
/// </summary>
public sealed class OutboxHealthCheckOptions
{
    /// <summary>
    /// En eski bekleyen mesaj bundan yaşlıysa outbox geride kalmış sayılır (yayın durmuş ya da yetişemiyor).
    /// </summary>
    public TimeSpan MaxPendingAge { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Ölü mesaj varsa sonuç <see cref="HealthStatus.Degraded"/> olsun (varsayılan). Kapalıysa ölü mesajlar yalnızca
    /// bilgi olarak raporlanır.
    /// </summary>
    public bool DegradeOnDeadMessages { get; set; } = true;
}

/// <summary>
/// Outbox'ın durumu (ortak veritabanı): ölü mesaj sayısı, bekleyen mesaj sayısı ve en eski bekleyen mesajın yaşı.
/// Ölü mesaj varsa ya da en eski bekleyen mesaj <see cref="OutboxHealthCheckOptions.MaxPendingAge"/>'den yaşlıysa
/// <see cref="HealthStatus.Degraded"/> döner: uygulama çalışır ama event'ler yayınlanmıyor demektir.
/// </summary>
public sealed class OutboxHealthCheck<TContext> : IHealthCheck
    where TContext : DbContext
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly OutboxHealthCheckOptions _options;
    private readonly TimeProvider _time;

    /// <summary>
    /// Sağlık kontrolünü oluşturur.
    /// </summary>
    /// <param name="scopeFactory">Her kontrolde yeni DbContext almak için scope fabrikası.</param>
    /// <param name="options">Eşikler.</param>
    /// <param name="timeProvider">Saat (testlerde sabitlenir).</param>
    public OutboxHealthCheck(IServiceScopeFactory scopeFactory, OutboxHealthCheckOptions options, TimeProvider? timeProvider = null)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Outbox durumunu kontrol eder.
    /// </summary>
    /// <param name="context">Kontrolün kaydı (hata durumu buradan okunur).</param>
    /// <param name="cancellationToken">İptal belirteci.</param>
    /// <returns>Sonuç; <c>pending</c>, <c>dead</c> ve <c>oldestPendingSeconds</c> verileriyle.</returns>
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
        IQueryable<OutboxMessage> messages = scope.ServiceProvider.GetRequiredService<TContext>().Set<OutboxMessage>().AsNoTracking();

        int dead = await messages.CountAsync(m => m.Status == OutboxMessageStatus.Dead, cancellationToken).ConfigureAwait(false);
        int pending = await messages.CountAsync(m => m.Status == OutboxMessageStatus.Pending, cancellationToken).ConfigureAwait(false);
        DateTime? oldest = await messages
            .Where(m => m.Status == OutboxMessageStatus.Pending)
            .OrderBy(m => m.OccurredAt)
            .Select(m => (DateTime?)m.OccurredAt)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        double oldestAge = oldest is { } at ? Math.Max(0, (_time.GetUtcNow().UtcDateTime - at).TotalSeconds) : 0;
        var data = new Dictionary<string, object>
        {
            ["pending"] = pending,
            ["dead"] = dead,
            ["oldestPendingSeconds"] = Math.Round(oldestAge),
        };

        var problems = new List<string>();
        if (dead > 0 && _options.DegradeOnDeadMessages)
            problems.Add($"{dead} ölü mesaj var (yeniden kuyruğa alınana kadar yayınlanmayacak).");
        if (oldestAge > _options.MaxPendingAge.TotalSeconds)
            problems.Add($"En eski bekleyen mesaj {TimeSpan.FromSeconds(oldestAge):g} önce oluştu; outbox geride kalmış.");

        return problems.Count == 0
            ? HealthCheckResult.Healthy(data: data)
            : HealthCheckResult.Degraded(string.Join(" ", problems), data: data);
    }
}
