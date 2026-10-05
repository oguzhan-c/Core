using Can.Core.Application;
using Can.Core.MultiTenancy;
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
    /// builder.Services.AddCanWebApi();
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

        // Aktif tenant TenantContext'te tutulur (UseCanTenantResolution doldurur). AddCanMultiTenancy(...) ayrıca
        // çağrıldıysa onun ayarları korunur; çağrılmadıysa varsayılanlarla (tek veritabanı) kaydedilir.
        if (!services.Any(d => d.ServiceType == typeof(MultiTenancyOptions)))
            services.AddCanMultiTenancy();

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
    /// Aktif tenant'ı istek başında çözer (gerekirse tenant deposundan yükler) ve yetkisiz seçimi 403 ile reddeder.
    /// <c>UseAuthentication()</c>'dan SONRA ekle.
    /// </summary>
    public static IApplicationBuilder UseCanTenantResolution(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<TenantResolutionMiddleware>();
    }
}
