using System.Globalization;
using Can.Core.Application;
using Can.Core.Localization;
using Can.Core.MultiTenancy;
using Can.Core.WebApi.CurrentUser;
using Can.Core.WebApi.ExceptionHandling;
using Can.Core.WebApi.Localization;
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

        services.AddProblemDetails(o => o.CustomizeProblemDetails = ProblemDetailsLocalizer.Customize);
        services.AddExceptionHandler<CanExceptionHandler>();

        // Problem başlıkları ve ortak hata kodlarının tr/en metinleri (AddCanLocalization çağrılırsa kullanılır).
        services.AddCanLocalizationResources(typeof(WebApiExtensions).Assembly);

        return services;
    }

    /// <summary>Hataları ProblemDetails yanıtına çevirir. Pipeline'ın en başına ekle.</summary>
    public static IApplicationBuilder UseCanExceptionHandler(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseExceptionHandler();
    }

    /// <summary>
    /// İsteğin dilini belirler (<c>AddCanLocalization</c> ayarlarındaki kültürlerden): sırasıyla <c>?culture=en</c>,
    /// <c>.AspNetCore.Culture</c> cookie'si, <c>Accept-Language</c>; hiçbiri yoksa varsayılan kültür. Çeviri yapan her
    /// şeyden (endpoint'ler, exception handler yanıtı) önce ekle.
    /// </summary>
    public static IApplicationBuilder UseCanRequestLocalization(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        CanLocalizationOptions options =
            app.ApplicationServices.GetService<CanLocalizationOptions>() ?? new CanLocalizationOptions();

        CultureInfo[] cultures = options.SupportedCultures.Select(CultureInfo.GetCultureInfo).ToArray();
        return app.UseRequestLocalization(o =>
        {
            o.DefaultRequestCulture = new Microsoft.AspNetCore.Localization.RequestCulture(options.DefaultCulture);
            o.SupportedCultures = cultures;
            o.SupportedUICultures = cultures;
            o.FallBackToParentCultures = true;
            o.FallBackToParentUICultures = true;
        });
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
