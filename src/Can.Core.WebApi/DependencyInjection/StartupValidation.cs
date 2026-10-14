using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Can.Core.WebApi.DependencyInjection;

/// <summary>
/// Açılışta (ilk istekten önce) eksik kayıtları bulur: bir paketin ihtiyaç duyduğu servis kaydedilmemişse uygulama hiç
/// açılmaz ve eksiklerin hepsi tek mesajda listelenir. Böylece hata ilk kullanıcının isteğinde 500 olarak değil, deploy
/// anında görünür (Kubernetes'te pod açılmaz, eski sürüm çalışmaya devam eder).
/// </summary>
internal static class StartupValidation
{
    /// <summary>Uygulama açılırken <typeparamref name="TService"/> kayıtlı olmalı.</summary>
    /// <param name="services">Servisler.</param>
    /// <param name="requiredBy">Gereksinimi koyan çağrı (ör. <c>AddCanJwtAuthentication</c>).</param>
    /// <param name="fix">Eksikse yapılacak (ör. <c>builder.Services.AddCanSecurity(...)</c>).</param>
    public static void Require<TService>(IServiceCollection services, string requiredBy, string fix)
    {
        services.AddSingleton(new StartupRequirement(typeof(TService), requiredBy, fix));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IStartupFilter, StartupRequirementFilter>());
    }

    /// <summary>Middleware eklenirken (pipeline kurulurken) bağımlılığı kayıtlı mı; değilse hemen hata.</summary>
    public static void Require<TService>(IApplicationBuilder app, string requiredBy, string fix)
    {
        if (app.ApplicationServices.GetService<IServiceProviderIsService>() is { } check && !check.IsService(typeof(TService)))
            throw new InvalidOperationException($"{requiredBy}: {typeof(TService).Name} kayıtlı değil. Çözüm: {fix}");
    }
}

internal sealed record StartupRequirement(Type Service, string RequiredBy, string Fix);

/// <summary>Pipeline kurulmadan önce tüm gereksinimleri denetler.</summary>
internal sealed class StartupRequirementFilter(IServiceProvider services, IEnumerable<StartupRequirement> requirements) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        if (services.GetService<IServiceProviderIsService>() is { } check)
        {
            string[] missing = requirements
                .Where(r => !check.IsService(r.Service))
                .Select(r => $"- {r.RequiredBy}: {r.Service.Name} kayıtlı değil. Çözüm: {r.Fix}")
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (missing.Length > 0)
                throw new InvalidOperationException("Uygulama yapılandırması eksik:\n" + string.Join('\n', missing));
        }

        return next;
    }
}
