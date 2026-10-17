using Can.Core.Application.Features;
using Can.Core.WebApi.Features;
#if (Reporting)
using Can.Core.Reporting.AspNetCore;
#endif
#if (Settings)
using Can.Core.Settings.AspNetCore;
#endif
using Can.Core.WebApi.Security;
using CanAppTemplate.Domain.Identity;

namespace CanAppTemplate.WebApi.Endpoints;

internal static class EndpointRegistration
{
    /// <summary>
    /// Tüm API <c>/api</c> altında. Kimlik ve altyapı uçları sürümsüzdür; uygulama uçları sürümlüdür
    /// (<c>/api/v1/...</c>; sürümsüz istek varsayılan sürüme gider).
    /// </summary>
    public static IEndpointRouteBuilder MapCanAppTemplateEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder api = app.MapGroup("/api");

        api.MapAuthEndpoints();          // /api/auth/...
#if (ExternalLogin)
        api.MapExternalLoginEndpoints(); // /api/auth/external/...
#endif
        api.MapAccountEndpoints();       // /api/auth/account/...

        // Uygulama uçları (generator her entity için bir satır ekler).
        RouteGroupBuilder v1 = app.MapGroup("/api/v1");
        // can-gen:endpoints
        _ = v1;

        // Core: aktif oturumlar ("bu cihazdan / diğer cihazlardan çıkış") ve özellik bayrakları.
        app.MapCanSessions("/api/sessions").RequireAuthorization();
        app.MapCanFeatures("/api/features");
#if (Settings)
        app.MapCanSettings("/api/settings", o => o.AdminRole = Roles.Admin);
#endif
#if (Reporting)
        app.MapCanReporting("/api/reporting", o => o.AdminRole = Roles.Admin); // kaynaklar, çalıştır, dışa aktar, kayıtlı raporlar
#endif

        // Bilinmeyen API adresleri 404 dönsün.
        api.MapFallback(() => TypedResults.NotFound()).ExcludeFromDescription();
        return app;
    }
}
