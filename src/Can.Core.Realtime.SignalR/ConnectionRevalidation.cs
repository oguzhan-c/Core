using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Claims;
using Can.Core.MultiTenancy;
using Can.Core.Security.Authorization;
using Can.Core.Security.Tokens;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Can.Core.Realtime.SignalR;

/// <summary>
/// Açık bir bağlantının hâlâ geçerli olup olmadığına karar verir ve kullanıcının GÜNCEL rollerini döner. Bağlantı saatlerce
/// açık kalabilir; bu denetim olmadan rolü alınan kullanıcı eski rolünün mesajlarını, çıkış yapan kullanıcı da
/// bildirimleri almaya devam ederdi.
/// </summary>
/// <remarks>
/// İsteğin tenant'ına ayarlanmış bir scope'ta çağrılır (EF sorguları doğru tenant'a gider). Varsayılan:
/// <see cref="CanRealtimeConnectionValidator"/>; uygulama kendi kuralı için değiştirebilir.
/// </remarks>
public interface IRealtimeConnectionValidator
{
    /// <returns><see langword="null"/>: oturum geçersiz, bağlantı kapatılır. Aksi halde kullanıcının güncel rolleri.</returns>
    ValueTask<IReadOnlyCollection<string>?> ValidateAsync(ClaimsPrincipal user, CancellationToken cancellationToken);
}

/// <summary>
/// HTTP tarafındaki denetimlerin aynısı (kayıtlı olanlar uygulanır): token süresi (<c>exp</c>), geri alınmış token /
/// kapatılmış oturum (<see cref="IAccessTokenDenylist"/>), güvenlik damgası (<see cref="ISecurityStampValidator"/>),
/// kullanıcının varlığı ve rolleri (<see cref="IUserAuthorizationStore"/>). Hiçbiri kayıtlı değilse roller token'dan okunur.
/// </summary>
public sealed class CanRealtimeConnectionValidator(IServiceProvider services, CanSignalROptions options) : IRealtimeConnectionValidator
{
    public async ValueTask<IReadOnlyCollection<string>?> ValidateAsync(ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        TimeProvider time = services.GetService<TimeProvider>() ?? TimeProvider.System;

        if (user.FindFirstValue("exp") is { } exp
            && long.TryParse(exp, NumberStyles.Integer, CultureInfo.InvariantCulture, out long seconds)
            && DateTimeOffset.FromUnixTimeSeconds(seconds) <= time.GetUtcNow())
        {
            return null;
        }

        if (services.GetService<IAccessTokenDenylist>() is { } denylist)
        {
            if (user.FindFirstValue("jti") is { Length: > 0 } tokenId && await denylist.IsRevokedAsync(tokenId, cancellationToken).ConfigureAwait(false))
                return null;
            if (user.FindFirstValue(TokenService.SessionIdClaimType) is { Length: > 0 } sessionId
                && await denylist.IsRevokedAsync(AccessTokenDenylistKeys.Session(sessionId), cancellationToken).ConfigureAwait(false))
            {
                return null;
            }
        }

        string? userId = user.FindFirstValue(options.UserIdClaimType);
        if (userId is null)
            return Roles(user);

        if (services.GetService<ISecurityStampValidator>() is { } stamps
            && user.FindFirstValue(TokenService.SecurityStampClaimType) is { Length: > 0 } stamp
            && !await stamps.IsCurrentAsync(userId, stamp, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        if (services.GetService<IUserAuthorizationStore>() is { } store)
            return (await store.GetAsync(userId, cancellationToken).ConfigureAwait(false))?.Roles;

        return Roles(user);
    }

    private string[] Roles(ClaimsPrincipal user) => [.. user.FindAll(options.RoleClaimType).Select(c => c.Value)];
}

/// <summary>Bu sunucudaki açık bağlantı (yeniden doğrulama için).</summary>
internal sealed class TrackedConnection(HubCallerContext context, string? tenantId, IEnumerable<string> roles)
{
    public HubCallerContext Context { get; } = context;

    /// <summary>Bağlantının tenant'ı (token'daki claim; grup adlarında da bu kullanılır).</summary>
    public string? TenantId { get; } = tenantId;

    public HashSet<string> Roles { get; } = new(roles, StringComparer.OrdinalIgnoreCase);
}

/// <summary>Bu sunucudaki açık bağlantılar (her sunucu kendi bağlantılarını denetler; backplane gerekmez).</summary>
internal sealed class RealtimeConnectionRegistry
{
    private readonly ConcurrentDictionary<string, TrackedConnection> _connections = new(StringComparer.Ordinal);

    public int Count => _connections.Count;

    public void Add(TrackedConnection connection) => _connections[connection.Context.ConnectionId] = connection;

    public void Remove(string connectionId) => _connections.TryRemove(connectionId, out _);

    public TrackedConnection[] Snapshot() => [.. _connections.Values];
}

/// <summary>
/// Açık bağlantıları <see cref="CanSignalROptions.RevalidationInterval"/> aralıkla yeniden doğrular: geçersiz oturumun
/// bağlantısını kapatır, rolü değişen kullanıcının rol gruplarını günceller (bağlantı kopmaz).
/// </summary>
internal sealed partial class RealtimeConnectionMonitor(
    RealtimeConnectionRegistry registry,
    IServiceScopeFactory scopes,
    IHubContext<CanNotificationHub> hub,
    CanSignalROptions options,
    ILogger<RealtimeConnectionMonitor> logger) : BackgroundService
{
    private const int Parallelism = 8;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.RevalidationInterval <= TimeSpan.Zero)
            return;

        using var timer = new PeriodicTimer(options.RevalidationInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            await RevalidateAsync(stoppingToken).ConfigureAwait(false);
    }

    /// <summary>Tüm bağlantıları bir kez denetler (testler doğrudan çağırır).</summary>
    internal Task RevalidateAsync(CancellationToken cancellationToken) =>
        Parallel.ForEachAsync(
            registry.Snapshot(),
            new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = cancellationToken },
            async (connection, ct) => await RevalidateAsync(connection, ct).ConfigureAwait(false));

    private async Task RevalidateAsync(TrackedConnection connection, CancellationToken cancellationToken)
    {
        if (connection.Context.ConnectionAborted.IsCancellationRequested)
        {
            registry.Remove(connection.Context.ConnectionId);
            return;
        }

        ClaimsPrincipal user = connection.Context.User ?? new ClaimsPrincipal();
        IReadOnlyCollection<string>? roles;
        try
        {
            await using AsyncServiceScope scope = scopes.CreateAsyncScope();
            roles = await ApplyTenantAsync(scope.ServiceProvider, connection.TenantId, cancellationToken).ConfigureAwait(false)
                ? await scope.ServiceProvider.GetRequiredService<IRealtimeConnectionValidator>().ValidateAsync(user, cancellationToken).ConfigureAwait(false)
                : null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Geçici bir hata (veritabanı) herkesin bağlantısını koparmasın: bir sonraki turda tekrar denenir.
            LogRevalidationFailed(exception, connection.Context.ConnectionId);
            return;
        }

        if (roles is null)
        {
            LogConnectionRevoked(connection.Context.ConnectionId, connection.Context.UserIdentifier);
            registry.Remove(connection.Context.ConnectionId);
            connection.Context.Abort();
            return;
        }

        await SyncRolesAsync(connection, roles, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Scope'u bağlantının tenant'ına ayarlar (tenant deposu varsa tanımıyla: tenant başına veritabanı). Tenant artık
    /// yoksa ya da pasifse <see langword="false"/> (HTTP tarafında da 403).
    /// </summary>
    private static async Task<bool> ApplyTenantAsync(IServiceProvider services, string? tenantId, CancellationToken cancellationToken)
    {
        if (services.GetService<TenantContext>() is not { } tenantContext)
            return true;

        if (tenantId is null || services.GetService<ITenantStore>() is not { } store)
        {
            tenantContext.Set(tenantId);
            return true;
        }

        TenantInfo? tenant = await store.FindAsync(tenantId, cancellationToken).ConfigureAwait(false);
        if (tenant is null || !tenant.IsActive || !string.Equals(tenant.Id, tenantId, StringComparison.OrdinalIgnoreCase))
            return false;

        tenantContext.Set(tenant);
        return true;
    }

    private async Task SyncRolesAsync(TrackedConnection connection, IReadOnlyCollection<string> current, CancellationToken cancellationToken)
    {
        var target = new HashSet<string>(current, StringComparer.OrdinalIgnoreCase);
        string[] removed;
        string[] added;
        lock (connection.Roles)
        {
            removed = [.. connection.Roles.Where(r => !target.Contains(r))];
            added = [.. target.Where(r => !connection.Roles.Contains(r))];
            connection.Roles.Clear();
            connection.Roles.UnionWith(target);
        }

        if (removed.Length == 0 && added.Length == 0)
            return;

        string connectionId = connection.Context.ConnectionId;
        foreach (string role in removed)
            await hub.Groups.RemoveFromGroupAsync(connectionId, RealtimeGroups.Role(connection.TenantId, role), cancellationToken).ConfigureAwait(false);
        foreach (string role in added)
            await hub.Groups.AddToGroupAsync(connectionId, RealtimeGroups.Role(connection.TenantId, role), cancellationToken).ConfigureAwait(false);

        LogRolesChanged(connectionId, removed.Length, added.Length);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Anlık bildirim bağlantısı {ConnectionId} kapatıldı: oturum artık geçerli değil (kullanıcı {UserId}).")]
    private partial void LogConnectionRevoked(string connectionId, string? userId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Anlık bildirim bağlantısı {ConnectionId}: rol grupları güncellendi ({Removed} çıkarıldı, {Added} eklendi).")]
    private partial void LogRolesChanged(string connectionId, int removed, int added);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Anlık bildirim bağlantısı {ConnectionId} doğrulanamadı; bir sonraki turda tekrar denenecek.")]
    private partial void LogRevalidationFailed(Exception exception, string connectionId);
}

/// <summary>Bağlantı açılış / kapanış logları (mesaj içerikleri loglanmaz).</summary>
internal static partial class RealtimeLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Anlık bildirim bağlantısı {ConnectionId} açıldı (kullanıcı {UserId}, tenant {TenantId}).")]
    public static partial void Connected(ILogger logger, string connectionId, string? userId, string? tenantId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Anlık bildirim bağlantısı {ConnectionId} kapandı (kullanıcı {UserId}).")]
    public static partial void Disconnected(ILogger logger, Exception? exception, string connectionId, string? userId);
}
