using Hangfire;
using Hangfire.Dashboard;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace Can.Core.BackgroundJobs.Hangfire;

public static class HangfireDashboardExtensions
{
    /// <summary>
    /// Hangfire dashboard'unu yalnızca giriş yapmış ve (verildiyse) rollerden birine sahip kullanıcılara açar.
    /// <c>UseAuthentication()</c>'dan sonra çağrılmalı; kimlik, uygulamanın normal kimlik doğrulamasından
    /// (ör. cookie'deki JWT) okunur.
    /// </summary>
    /// <remarks>
    /// Dashboard tenant'a göre filtrelenmez: tüm tenant'ların işlerini (ve argümanlarını) gösterir. Bu yüzden
    /// yalnızca sistem yöneticilerine aç.
    /// </remarks>
    public static IEndpointConventionBuilder MapCanHangfireDashboard(
        this IEndpointRouteBuilder endpoints,
        string pattern = "/hangfire",
        params string[] roles)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        return endpoints.MapHangfireDashboard(
            pattern,
            new DashboardOptions
            {
                Authorization = [new RoleDashboardAuthorizationFilter(roles)],
                DisplayStorageConnectionString = false,
                AppPath = "/",
            }
        );
    }
}

/// <summary>Kullanıcı giriş yapmış ve rollerden en az birine sahipse izin verir.</summary>
internal sealed class RoleDashboardAuthorizationFilter : IDashboardAuthorizationFilter
{
    private readonly string[] _roles;

    public RoleDashboardAuthorizationFilter(string[] roles) => _roles = roles ?? [];

    public bool Authorize(DashboardContext context)
    {
        System.Security.Claims.ClaimsPrincipal user = context.GetHttpContext().User;

        if (user.Identity?.IsAuthenticated != true)
            return false;

        return _roles.Length == 0 || _roles.Any(user.IsInRole);
    }
}
