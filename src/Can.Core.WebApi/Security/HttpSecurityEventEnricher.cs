using Can.Core.MultiTenancy;
using Can.Core.Security.Events;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Can.Core.WebApi.Security;

/// <summary>
/// Güvenlik olaylarına isteğin bilgisini ekler (yalnızca boş alanları): IP (<c>UseForwardedHeaders</c> sonrası gerçek
/// istemci), User-Agent, tenant, oturum (<c>sid</c>) ve giriş yapmış kullanıcı. <c>AddCanWebApi()</c> kaydeder.
/// </summary>
internal sealed class HttpSecurityEventEnricher(IHttpContextAccessor accessor) : ISecurityEventEnricher
{
    public SecurityEvent Enrich(SecurityEvent securityEvent)
    {
        if (accessor.HttpContext is not { } http)
            return securityEvent;

        string? userAgent = http.Request.Headers.UserAgent.ToString();
        return securityEvent with
        {
            IpAddress = securityEvent.IpAddress ?? http.Connection.RemoteIpAddress?.ToString(),
            UserAgent = securityEvent.UserAgent ?? (string.IsNullOrEmpty(userAgent) ? null : userAgent.Length <= 512 ? userAgent : userAgent[..512]),
            TenantId = securityEvent.TenantId ?? http.RequestServices.GetService<TenantContext>()?.TenantId,
            SessionId = securityEvent.SessionId ?? http.GetSessionId(),
            UserId = securityEvent.UserId ?? (http.User.Identity?.IsAuthenticated == true ? (http.User.FindFirst("sub") ?? http.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier))?.Value : null),
        };
    }
}

/// <summary>Middleware'lerin olay yayını.</summary>
internal static class SecurityEventPublishing
{
    public static ValueTask PublishAsync(HttpContext context, SecurityEvent securityEvent) =>
        context.RequestServices.GetService<ISecurityEvents>()?.PublishAsync(securityEvent, context.RequestAborted) ?? ValueTask.CompletedTask;
}
