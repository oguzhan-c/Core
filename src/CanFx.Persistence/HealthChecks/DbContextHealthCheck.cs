using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace CanFx.Persistence.HealthChecks;

/// <summary>Veritabanına bağlanılabiliyor mu (<c>Database.CanConnectAsync</c>; ortak veritabanı).</summary>
public sealed class DbContextHealthCheck<TContext> : IHealthCheck
    where TContext : DbContext
{
    private readonly IServiceScopeFactory _scopeFactory;

    public DbContextHealthCheck(IServiceScopeFactory scopeFactory) => _scopeFactory = scopeFactory;

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
        TContext db = scope.ServiceProvider.GetRequiredService<TContext>();

        return await db.Database.CanConnectAsync(cancellationToken).ConfigureAwait(false)
            ? HealthCheckResult.Healthy()
            : new HealthCheckResult(context.Registration.FailureStatus, "Veritabanına bağlanılamadı.");
    }
}

public static class PersistenceHealthCheckExtensions
{
    /// <summary>Veritabanı bağlantı kontrolü ekler (varsayılan etiket: <c>ready</c>).</summary>
    public static IHealthChecksBuilder AddCanDbContextCheck<TContext>(
        this IHealthChecksBuilder builder,
        string name = "database",
        HealthStatus failureStatus = HealthStatus.Unhealthy,
        params string[] tags)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddCheck<DbContextHealthCheck<TContext>>(name, failureStatus, tags.Length > 0 ? tags : new[] { "ready" });
    }

    /// <summary>
    /// Outbox durum kontrolü ekler: ölü mesaj ya da geride kalmış yayın varsa <see cref="HealthStatus.Degraded"/>.
    /// Varsayılan etiket <c>ready</c>: <c>/health/ready</c>'de görünür; Degraded 200 döndüğü için trafiği kesmez.
    /// </summary>
    /// <typeparam name="TContext">Outbox tablosunu içeren DbContext.</typeparam>
    /// <param name="builder">Sağlık kontrolü oluşturucusu.</param>
    /// <param name="configure">Eşikleri ayarlar.</param>
    /// <param name="name">Kontrolün adı.</param>
    /// <param name="tags">Etiketler (boşsa <c>ready</c>).</param>
    /// <returns>Zincirleme için aynı oluşturucu.</returns>
    public static IHealthChecksBuilder AddCanOutboxCheck<TContext>(
        this IHealthChecksBuilder builder,
        Action<OutboxHealthCheckOptions>? configure = null,
        string name = "outbox",
        params string[] tags)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(builder);
        var options = new OutboxHealthCheckOptions();
        configure?.Invoke(options);
        return builder.Add(new HealthCheckRegistration(
            name,
            sp => new OutboxHealthCheck<TContext>(sp.GetRequiredService<IServiceScopeFactory>(), options, sp.GetService<TimeProvider>()),
            HealthStatus.Degraded,
            tags.Length > 0 ? tags : new[] { "ready" }));
    }
}
