using Can.Core.Application.DependencyInjection;
using Can.Core.BackgroundJobs;
using Can.Core.Mapping.DependencyInjection;
#if (Realtime)
using Can.Core.Realtime;
#endif
#if (Settings)
using Can.Core.Settings;
using CanAppTemplate.Application.Common;
#endif
using Microsoft.Extensions.DependencyInjection;
using CanAppTemplate.Application.Features.Account;
using CanAppTemplate.Application.Features.Auth;
using CanAppTemplate.Domain.Identity;

namespace CanAppTemplate.Application;

public static class DependencyInjection
{
    /// <summary>
    /// Handler'lar, pipeline davranışları (yetki, doğrulama, önbellek, transaction, log), validator'lar, eşlemeler ve
    /// arka plan işleri. Bu assembly'deki handler, validator ve <c>IBackgroundJob</c>'lar kendiliğinden kaydedilir.
    /// </summary>
    public static IServiceCollection AddCanAppTemplateApplication(this IServiceCollection services)
    {
        var assembly = typeof(DependencyInjection).Assembly;

        services.AddCanApplication(o => o.AdminRole = Roles.Admin, assembly);
        services.AddCanMapping(assembly);
        services.AddCanBackgroundJobs(assembly);
#if (Realtime)
        services.AddCanRealtimeDefaults(); // WebApi SignalR'ı kurar; diğer ortamlarda (test, iş sunucusu) bildirim gönderilmez
#endif
#if (Settings)
        services.AddCanSettings(s => s.Add(AppSettings.All));
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
