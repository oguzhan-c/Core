using CanFx.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CanFx.Persistence.RequestLogging;

/// <summary>Veritabanındaki istek kaydı (<c>RequestActivities</c> tablosu).</summary>
public class RequestActivityRecord
{
    protected RequestActivityRecord()
    {
        Method = string.Empty;
        Path = string.Empty;
    }

    public RequestActivityRecord(RequestActivity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        Id = Guid.CreateVersion7(activity.Timestamp);
        Timestamp = activity.Timestamp;
        Method = activity.Method;
        Path = activity.Path;
        Route = activity.Route;
        Query = activity.Query;
        StatusCode = activity.StatusCode;
        DurationMs = activity.DurationMs;
        UserId = activity.UserId;
        TenantId = activity.TenantId;
        SessionId = activity.SessionId;
        IpAddress = activity.IpAddress;
        UserAgent = activity.UserAgent;
        TraceId = activity.TraceId;
    }

    /// <summary>Zamana göre sıralı (UUID v7).</summary>
    public Guid Id { get; protected set; }

    public DateTimeOffset Timestamp { get; protected set; }

    public string Method { get; protected set; }

    public string Path { get; protected set; }

    public string? Route { get; protected set; }

    public string? Query { get; protected set; }

    public int StatusCode { get; protected set; }

    public double DurationMs { get; protected set; }

    public string? UserId { get; protected set; }

    public string? TenantId { get; protected set; }

    public string? SessionId { get; protected set; }

    public string? IpAddress { get; protected set; }

    public string? UserAgent { get; protected set; }

    public string? TraceId { get; protected set; }
}

/// <summary>
/// Kayıtları <typeparamref name="TContext"/>'e parti halinde yazar (kendi scope'unda, kendi DbContext'iyle).
/// Tenant başına veritabanında kayıtlar ortak veritabanına yazılır (<c>TenantId</c> sütunuyla).
/// </summary>
public sealed class EfRequestActivitySink<TContext>(IServiceScopeFactory scopes) : IRequestActivitySink
    where TContext : DbContext
{
    public async ValueTask WriteAsync(IReadOnlyList<RequestActivity> batch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (batch.Count == 0)
            return;

        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        TContext context = scope.ServiceProvider.GetRequiredService<TContext>();
        context.ChangeTracker.AutoDetectChangesEnabled = false;
        context.AddRange(batch.Select(a => new RequestActivityRecord(a)));
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}

public static class RequestActivityStoreExtensions
{
    /// <summary>
    /// <c>RequestActivities</c> tablosunu modele ekler. Index'ler: kullanıcı + zaman, tenant + zaman, zaman (saklama
    /// süresi temizliği: <c>o.Keep&lt;RequestActivityRecord&gt;(süre, a =&gt; a.Timestamp)</c>).
    /// </summary>
    public static ModelBuilder AddCanRequestActivityLog(this ModelBuilder modelBuilder, string tableName = "RequestActivities", string? schema = null)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<RequestActivityRecord>(b =>
        {
            b.ToTable(tableName, schema);
            b.HasKey(a => a.Id);
            b.Property(a => a.Id).ValueGeneratedNever();
            b.Property(a => a.Method).HasMaxLength(16).IsRequired();
            b.Property(a => a.Path).HasMaxLength(512).IsRequired();
            b.Property(a => a.Route).HasMaxLength(512);
            b.Property(a => a.Query).HasMaxLength(1024);
            b.Property(a => a.UserId).HasMaxLength(128);
            b.Property(a => a.TenantId).HasMaxLength(128);
            b.Property(a => a.SessionId).HasMaxLength(64);
            b.Property(a => a.IpAddress).HasMaxLength(64);
            b.Property(a => a.UserAgent).HasMaxLength(512);
            b.Property(a => a.TraceId).HasMaxLength(64);
            b.HasIndex(a => new { a.UserId, a.Timestamp });
            b.HasIndex(a => new { a.TenantId, a.Timestamp });
            b.HasIndex(a => a.Timestamp);
        });
        return modelBuilder;
    }

    /// <summary>İstek kayıtlarını veritabanına da yazar (<c>AddCanRequestActivityLog()</c> ile birlikte). Tablo: <c>modelBuilder.AddCanRequestActivityLog()</c>.</summary>
    public static IServiceCollection AddCanRequestActivityStore<TContext>(this IServiceCollection services)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IRequestActivitySink, EfRequestActivitySink<TContext>>());
        return services;
    }
}
