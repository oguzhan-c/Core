using Can.Core.Application;
using Can.Core.WebApi.CurrentUser;
using Can.Core.WebApi.ExceptionHandling;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Can.Core.WebApi.DependencyInjection;

public static class WebApiExtensions
{
    /// <summary>
    /// ProblemDetails tabanlı hata yönetimini ve HTTP'ye bağlı <see cref="ICurrentUser"/> /
    /// <see cref="ICurrentTenant"/> implementasyonlarını kaydeder (diğer paketlerin "boş" varsayılanlarının yerine geçer).
    /// </summary>
    /// <example>
    /// <code>
    /// builder.Services.AddCanWebApi(o =&gt; o.TenantHeaderName = "X-Tenant-Id");
    ///
    /// app.UseCanExceptionHandler();   // en başta
    /// app.UseAuthentication();
    /// app.UseCanTenantResolution();   // authentication'dan sonra
    /// app.UseAuthorization();
    /// </code>
    /// </example>
    public static IServiceCollection AddCanWebApi(this IServiceCollection services, Action<CanWebApiOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new CanWebApiOptions();
        configure?.Invoke(options);

        services.TryAddSingleton(options);
        services.AddHttpContextAccessor();

        services.RemoveAll<ICurrentUser>();
        services.AddScoped<ICurrentUser, HttpCurrentUser>();

        services.RemoveAll<ICurrentTenant>();
        services.AddScoped<ICurrentTenant, HttpCurrentTenant>();

        services.AddProblemDetails();
        services.AddExceptionHandler<CanExceptionHandler>();

        return services;
    }

    /// <summary>Hataları ProblemDetails yanıtına çevirir. Pipeline'ın en başına ekle.</summary>
    public static IApplicationBuilder UseCanExceptionHandler(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseExceptionHandler();
    }

    /// <summary>
    /// Aktif tenant'ı istek başında çözer ve yetkisiz tenant seçimini 403 ile reddeder.
    /// <c>UseAuthentication()</c>'dan SONRA ekle.
    /// </summary>
    public static IApplicationBuilder UseCanTenantResolution(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<TenantResolutionMiddleware>();
    }
}
