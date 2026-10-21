using CanFx.Application.DependencyInjection;
using CanFx.BackgroundJobs;
using CanFx.Mapping.DependencyInjection;
#if (Realtime)
using CanFx.Realtime;
#endif
#if (Settings)
using CanFx.Settings;
using CanAppTemplate.Application.Services.Settings;
#endif
using Microsoft.Extensions.DependencyInjection;
using CanAppTemplate.Application.Services.AuthService;
using CanAppTemplate.Domain.Constants;

namespace CanAppTemplate.Application;

public static class ApplicationServiceRegistration
{
    /// <summary>
    /// Handler'lar, pipeline davranışları (yetki, doğrulama, önbellek, transaction, log), validator'lar, eşleme profilleri
    /// (Profiles/MappingProfiles), iş kuralı sınıfları (BaseBusinessRules) ve arka plan işleri. Bu assembly'deki handler,
    /// validator, profil, iş kuralı ve <c>IBackgroundJob</c>'lar kendiliğinden kaydedilir.
    /// </summary>
    public static IServiceCollection AddApplicationServices(this IServiceCollection services)
    {
        var assembly = typeof(ApplicationServiceRegistration).Assembly;

        services.AddCanApplication(o => o.AdminRole = Roles.Admin, assembly);
        services.AddCanMapping(assembly);
        services.AddCanBackgroundJobs(assembly);
#if (Realtime)
        services.AddCanRealtimeDefaults(); // WebAPI SignalR'ı kurar; diğer ortamlarda (test, iş sunucusu) bildirim gönderilmez
#endif
#if (Settings)
        services.AddCanSettings(s => s.Add(RuntimeSettings.All));
#endif

        // Kimlik
        services.AddScoped<AuthTokenIssuer>();
        services.AddScoped<EmailVerification>();
        services.AddScoped<TwoFactorCodeVerifier>();
        services.AddScoped<RecoveryCodeIssuer>();

        // can-gen:services
        return services;
    }
}
