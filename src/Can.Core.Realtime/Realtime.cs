using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Can.Core.Realtime;

/// <summary>
/// İstemciye giden anlık mesaj. <see cref="Type"/> istemcinin ne yapacağını belirler (<c>"order.shipped"</c>);
/// <see cref="Data"/> JSON'a çevrilir (kişisel veri koyma, id yeterli: istemci gerekirse API'den okur).
/// </summary>
public sealed record RealtimeMessage(string Type, object? Data = null)
{
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Bağlı istemcilere anlık mesaj gönderir. Tüm hedefler AKTİF tenant ile sınırlıdır: bir tenant'ın mesajı başka
/// tenant'ın kullanıcılarına gitmez. Arka plan işlerinde ve outbox handler'larında tenant zaten işin tenant'ıdır.
/// </summary>
/// <remarks>Gönderim "ateşle ve unut"tur: istemci bağlı değilse mesaj kaybolur (kalıcı bildirim gerekiyorsa veritabanına da yaz).</remarks>
public interface IRealtimeNotifier
{
    Task SendToUserAsync(string userId, RealtimeMessage message, CancellationToken cancellationToken = default);

    Task SendToRoleAsync(string role, RealtimeMessage message, CancellationToken cancellationToken = default);

    /// <summary>Tenant'taki herkese.</summary>
    Task SendToTenantAsync(RealtimeMessage message, CancellationToken cancellationToken = default);

    /// <summary>Uygulamanın tanımladığı gruba (ör. <c>"order:42"</c>; istemci <c>Subscribe</c> ile katılır).</summary>
    Task SendToGroupAsync(string group, RealtimeMessage message, CancellationToken cancellationToken = default);
}

/// <summary>Anlık bildirim kurulu değilken: hiçbir şey göndermez (Application kodu her ortamda çalışsın).</summary>
public sealed class NullRealtimeNotifier : IRealtimeNotifier
{
    public static readonly NullRealtimeNotifier Instance = new();

    public Task SendToUserAsync(string userId, RealtimeMessage message, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task SendToRoleAsync(string role, RealtimeMessage message, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task SendToTenantAsync(RealtimeMessage message, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task SendToGroupAsync(string group, RealtimeMessage message, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

/// <summary>Grup adları: hepsi tenant ile başlar, böylece tenant'lar arası sızıntı olmaz.</summary>
public static class RealtimeGroups
{
    public const string HostTenant = "host";

    public static string Tenant(string? tenantId) => $"t:{tenantId ?? HostTenant}";

    public static string User(string? tenantId, string userId) => $"{Tenant(tenantId)}:u:{userId}";

    public static string Role(string? tenantId, string role) => $"{Tenant(tenantId)}:r:{role.ToUpperInvariant()}";

    public static string Custom(string? tenantId, string group) => $"{Tenant(tenantId)}:g:{group}";
}

public static class RealtimeServiceCollectionExtensions
{
    /// <summary>Taşıyıcı kurulu değilse boş bildirici (SignalR paketi bunun yerine geçer).</summary>
    public static IServiceCollection AddCanRealtimeDefaults(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IRealtimeNotifier>(NullRealtimeNotifier.Instance);
        return services;
    }
}
