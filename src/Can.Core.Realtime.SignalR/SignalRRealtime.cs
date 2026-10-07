using System.Security.Claims;
using Can.Core.MultiTenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Can.Core.Realtime.SignalR;

public sealed class CanSignalROptions
{
    /// <summary>Kullanıcı kimliğinin claim'i (JWT <c>sub</c>).</summary>
    public string UserIdClaimType { get; set; } = "sub";

    /// <summary>Oturumun tenant'ının claim'i.</summary>
    public string TenantClaimType { get; set; } = "tenant_id";

    /// <summary>Rol claim'i; kullanıcı her rolü için bir gruba eklenir.</summary>
    public string RoleClaimType { get; set; } = "role";

    /// <summary>İstemciye giden metodun adı: <c>connection.on("notify", mesaj =&gt; ...)</c>.</summary>
    public string ClientMethod { get; set; } = "notify";
}

/// <summary>
/// İstemcinin özel bir gruba (<c>Subscribe("order:42")</c>) katılıp katılamayacağına karar verir. Varsayılan: hayır.
/// Örn. kullanıcının siparişi mi, kontrol edip izin ver.
/// </summary>
public interface IRealtimeSubscriptionAuthorizer
{
    ValueTask<bool> CanSubscribeAsync(ClaimsPrincipal user, string group, CancellationToken cancellationToken);
}

internal sealed class DenyAllSubscriptions : IRealtimeSubscriptionAuthorizer
{
    public ValueTask<bool> CanSubscribeAsync(ClaimsPrincipal user, string group, CancellationToken cancellationToken) => ValueTask.FromResult(false);
}

/// <summary>
/// Bildirim hub'ı. Yalnızca giriş yapmış kullanıcılar bağlanır (kimlik normal kimlik doğrulamadan, ör. cookie'deki JWT);
/// bağlantı tenant'ın, kullanıcının ve rollerinin gruplarına otomatik eklenir. İstemci mesaj göndermez, yalnızca dinler.
/// </summary>
[Authorize]
public sealed class CanNotificationHub(CanSignalROptions options, IRealtimeSubscriptionAuthorizer subscriptions) : Hub
{
    public override async Task OnConnectedAsync()
    {
        ClaimsPrincipal user = Context.User ?? new ClaimsPrincipal();
        string? tenant = user.FindFirstValue(options.TenantClaimType);
        string? userId = user.FindFirstValue(options.UserIdClaimType);

        await Groups.AddToGroupAsync(Context.ConnectionId, RealtimeGroups.Tenant(tenant), Context.ConnectionAborted).ConfigureAwait(false);

        if (userId is not null)
            await Groups.AddToGroupAsync(Context.ConnectionId, RealtimeGroups.User(tenant, userId), Context.ConnectionAborted).ConfigureAwait(false);

        foreach (string role in user.FindAll(options.RoleClaimType).Select(c => c.Value).Distinct(StringComparer.OrdinalIgnoreCase))
            await Groups.AddToGroupAsync(Context.ConnectionId, RealtimeGroups.Role(tenant, role), Context.ConnectionAborted).ConfigureAwait(false);

        await base.OnConnectedAsync().ConfigureAwait(false);
    }

    /// <summary>Uygulamanın tanımladığı bir gruba katılır (yetki <see cref="IRealtimeSubscriptionAuthorizer"/>'da).</summary>
    public async Task<bool> Subscribe(string group)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        ClaimsPrincipal user = Context.User ?? new ClaimsPrincipal();

        if (!await subscriptions.CanSubscribeAsync(user, group, Context.ConnectionAborted).ConfigureAwait(false))
            return false;

        string? tenant = user.FindFirstValue(options.TenantClaimType);
        await Groups.AddToGroupAsync(Context.ConnectionId, RealtimeGroups.Custom(tenant, group), Context.ConnectionAborted).ConfigureAwait(false);
        return true;
    }

    public Task Unsubscribe(string group)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        string? tenant = (Context.User ?? new ClaimsPrincipal()).FindFirstValue(options.TenantClaimType);
        return Groups.RemoveFromGroupAsync(Context.ConnectionId, RealtimeGroups.Custom(tenant, group), Context.ConnectionAborted);
    }
}

/// <summary>Mesajları aktif tenant'ın gruplarına gönderir (scoped: tenant isteğin/işin tenant'ı).</summary>
internal sealed class SignalRRealtimeNotifier : IRealtimeNotifier
{
    private readonly IHubContext<CanNotificationHub> _hub;
    private readonly CanSignalROptions _options;
    private readonly TenantContext? _tenantContext;

    public SignalRRealtimeNotifier(IHubContext<CanNotificationHub> hub, CanSignalROptions options, IServiceProvider services)
    {
        _hub = hub;
        _options = options;
        _tenantContext = services.GetService<TenantContext>();
    }

    /// <summary>Gönderim anındaki tenant (bildirici tenant seçilmeden önce oluşturulmuş olabilir).</summary>
    private string? TenantId => _tenantContext?.TenantId;

    public Task SendToUserAsync(string userId, RealtimeMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        return SendAsync(RealtimeGroups.User(TenantId, userId), message, cancellationToken);
    }

    public Task SendToRoleAsync(string role, RealtimeMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        return SendAsync(RealtimeGroups.Role(TenantId, role), message, cancellationToken);
    }

    public Task SendToTenantAsync(RealtimeMessage message, CancellationToken cancellationToken = default) =>
        SendAsync(RealtimeGroups.Tenant(TenantId), message, cancellationToken);

    public Task SendToGroupAsync(string group, RealtimeMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        return SendAsync(RealtimeGroups.Custom(TenantId, group), message, cancellationToken);
    }

    private Task SendAsync(string group, RealtimeMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        return _hub.Clients.Group(group).SendAsync(_options.ClientMethod, message, cancellationToken);
    }
}

public static class SignalRRealtimeExtensions
{
    /// <summary>
    /// SignalR'ı ve <see cref="IRealtimeNotifier"/>'ı kaydeder. Tek sunucu için yeterli; birden fazla sunucuda
    /// bir backplane gerekir (ör. <c>Microsoft.AspNetCore.SignalR.StackExchangeRedis</c> ile <c>.AddStackExchangeRedis(...)</c>).
    /// </summary>
    /// <example>
    /// <code>
    /// builder.Services.AddCanSignalR();
    /// app.MapCanRealtimeHub();   // UseAuthentication'dan sonra; varsayılan /hubs/notifications
    ///
    /// // handler
    /// await realtime.SendToRoleAsync("Warehouse", new RealtimeMessage("order.placed", new { orderId }), ct);
    /// </code>
    /// </example>
    public static ISignalRServerBuilder AddCanSignalR(this IServiceCollection services, Action<CanSignalROptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new CanSignalROptions();
        configure?.Invoke(options);

        services.RemoveAll<CanSignalROptions>();
        services.AddSingleton(options);
        services.TryAddSingleton<IRealtimeSubscriptionAuthorizer, DenyAllSubscriptions>();
        services.RemoveAll<IRealtimeNotifier>();
        services.AddScoped<IRealtimeNotifier, SignalRRealtimeNotifier>();

        return services.AddSignalR();
    }

    /// <summary>Hub'ı adresler (yalnızca giriş yapmış kullanıcılar). WebSocket yoksa SSE / long polling'e düşer.</summary>
    public static HubEndpointConventionBuilder MapCanRealtimeHub(this IEndpointRouteBuilder endpoints, string pattern = "/hubs/notifications")
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        return endpoints.MapHub<CanNotificationHub>(pattern, o => o.Transports = HttpTransportType.WebSockets | HttpTransportType.ServerSentEvents | HttpTransportType.LongPolling);
    }
}
