using System.Text.Json;
using Can.Core.Security.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Can.Core.Security.EntityFrameworkCore;

/// <summary>
/// Veritabanındaki güvenlik olayı (<c>SecurityEvents</c> tablosu). Kullanıcının "hesap etkinliği" sayfası, denetim ve
/// olay incelemesi için sorgulanır.
/// </summary>
public class SecurityEventRecord
{
    public const int TypeMaxLength = 64;

    protected SecurityEventRecord()
    {
        Type = string.Empty;
    }

    public SecurityEventRecord(SecurityEvent securityEvent)
    {
        ArgumentNullException.ThrowIfNull(securityEvent);
        Id = Guid.CreateVersion7(securityEvent.OccurredAt);
        Type = Limit(securityEvent.Type, TypeMaxLength)!;
        UserId = Limit(securityEvent.UserId, 128);
        Severity = securityEvent.Severity;
        OccurredAt = securityEvent.OccurredAt;
        Reason = Limit(securityEvent.Reason, 128);
        TenantId = Limit(securityEvent.TenantId, 128);
        IpAddress = Limit(securityEvent.IpAddress, 64);
        UserAgent = Limit(securityEvent.UserAgent, 512);
        SessionId = Limit(securityEvent.SessionId, 64);
        DeviceId = Limit(securityEvent.DeviceId, 64);
        TraceId = Limit(securityEvent.TraceId, 64);
        Properties = securityEvent.Properties.Count == 0 ? null : Limit(JsonSerializer.Serialize(securityEvent.Properties), 4000);
    }

    /// <summary>Zamana göre sıralı (UUID v7): index sonuna eklenir.</summary>
    public Guid Id { get; protected set; }

    public string Type { get; protected set; }

    public string? UserId { get; protected set; }

    public SecurityEventSeverity Severity { get; protected set; }

    public DateTimeOffset OccurredAt { get; protected set; }

    public string? Reason { get; protected set; }

    public string? TenantId { get; protected set; }

    public string? IpAddress { get; protected set; }

    public string? UserAgent { get; protected set; }

    public string? SessionId { get; protected set; }

    public string? DeviceId { get; protected set; }

    public string? TraceId { get; protected set; }

    /// <summary>Ek bilgiler (JSON).</summary>
    public string? Properties { get; protected set; }

    private static string? Limit(string? value, int maxLength) =>
        value is null ? null : value.Length <= maxLength ? value : value[..maxLength];
}

/// <summary>
/// Olayları <typeparamref name="TContext"/>'e yazar. İsteğin DbContext'i kullanılmaz (uygulamanın kaydedilmemiş
/// değişikliklerini de kaydetmesin diye): her olay kendi scope'unda, kendi DbContext'iyle kaydedilir.
/// </summary>
/// <remarks>
/// Tenant başına veritabanı kullanılıyorsa olaylar varsayılan (ortak) veritabanına yazılır; tenant'ı <c>TenantId</c>
/// sütunundadır.
/// </remarks>
public sealed class EfSecurityEventSink<TContext>(IServiceScopeFactory scopes) : ISecurityEventSink
    where TContext : DbContext
{
    public async ValueTask WriteAsync(SecurityEvent securityEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(securityEvent);
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        TContext context = scope.ServiceProvider.GetRequiredService<TContext>();
        context.Add(new SecurityEventRecord(securityEvent));
        await context.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false); // istek iptal edilse de olay kaybolmasın
    }
}

public static class SecurityEventStoreExtensions
{
    /// <summary>
    /// <c>SecurityEvents</c> tablosunu modele ekler. Index'ler: kullanıcı + zaman ("hesap etkinliğim"), tip + zaman
    /// (inceleme), zaman (saklama süresi temizliği).
    /// </summary>
    public static ModelBuilder ApplyCanSecurityEventLog(this ModelBuilder modelBuilder, Action<SecurityModelOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        var options = new SecurityModelOptions();
        configure?.Invoke(options);

        modelBuilder.Entity<SecurityEventRecord>(b =>
        {
            b.ToTable(options.TablePrefix + "SecurityEvents", options.Schema);
            b.HasKey(e => e.Id);
            b.Property(e => e.Id).ValueGeneratedNever();
            b.Property(e => e.Type).HasMaxLength(SecurityEventRecord.TypeMaxLength).IsRequired();
            b.Property(e => e.UserId).HasMaxLength(128);
            b.Property(e => e.Severity).HasConversion<string>().HasMaxLength(16);
            b.Property(e => e.Reason).HasMaxLength(128);
            b.Property(e => e.TenantId).HasMaxLength(128);
            b.Property(e => e.IpAddress).HasMaxLength(64);
            b.Property(e => e.UserAgent).HasMaxLength(512);
            b.Property(e => e.SessionId).HasMaxLength(64);
            b.Property(e => e.DeviceId).HasMaxLength(64);
            b.Property(e => e.TraceId).HasMaxLength(64);
            b.Property(e => e.Properties).HasMaxLength(4000);
            b.HasIndex(e => new { e.UserId, e.OccurredAt });
            b.HasIndex(e => new { e.Type, e.OccurredAt });
            b.HasIndex(e => e.OccurredAt);
        });

        return modelBuilder;
    }

    /// <summary>
    /// Güvenlik olaylarını veritabanına da yazar (<c>AddCanSecurityEvents()</c> ile birlikte). Tablo:
    /// <c>modelBuilder.ApplyCanSecurityEventLog()</c>.
    /// </summary>
    public static IServiceCollection AddCanSecurityEventStore<TContext>(this IServiceCollection services)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISecurityEventSink, EfSecurityEventSink<TContext>>());
        return services;
    }
}
