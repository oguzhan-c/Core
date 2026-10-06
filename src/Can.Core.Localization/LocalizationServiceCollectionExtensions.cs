using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Localization;

namespace Can.Core.Localization;

public static class LocalizationServiceCollectionExtensions
{
    /// <summary>
    /// JSON tabanlı çeviriyi kaydeder: <see cref="IStringLocalizer"/>, <see cref="IStringLocalizer{T}"/> ve
    /// <see cref="IStringLocalizerFactory"/>. Uygulama metinleri <c>Localization/tr.json</c>, <c>Localization/en.json</c>
    /// dosyalarına yazılır (çıktıya kopyalanmalı) ya da assembly'ye gömülür.
    /// </summary>
    /// <example>
    /// <code>
    /// builder.Services.AddCanLocalization(o =&gt;
    /// {
    ///     o.SupportedCultures = ["tr", "en"];
    ///     o.ResourceAssemblies.Add(typeof(Program).Assembly);   // gömülü *.tr.json / *.en.json
    /// });
    /// app.UseCanRequestLocalization();                          // WebApi: Accept-Language / ?culture= / cookie
    /// </code>
    /// </example>
    public static IServiceCollection AddCanLocalization(this IServiceCollection services, Action<CanLocalizationOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        CanLocalizationOptions options = GetOrAddOptions(services);
        configure?.Invoke(options);

        services.TryAddSingleton<JsonLocalizationStore>();
        services.RemoveAll<IStringLocalizerFactory>();
        services.AddSingleton<IStringLocalizerFactory, JsonStringLocalizerFactory>();
        services.TryAddSingleton<IStringLocalizer>(sp => sp.GetRequiredService<IStringLocalizerFactory>().Create(typeof(object)));
        services.TryAdd(ServiceDescriptor.Transient(typeof(IStringLocalizer<>), typeof(StringLocalizer<>)));

        return services;
    }

    /// <summary>
    /// Bir paketin gömülü çevirilerini ekler (paketler kendi varsayılan metinleri için çağırır). Uygulamanın kendi
    /// assembly'leri ve çeviri klasörü bunları ezer.
    /// </summary>
    public static IServiceCollection AddCanLocalizationResources(this IServiceCollection services, Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(assembly);

        CanLocalizationOptions options = GetOrAddOptions(services);
        if (!options.ResourceAssemblies.Contains(assembly))
            options.ResourceAssemblies.Insert(0, assembly); // paket metinleri en düşük öncelikte

        return services;
    }

    private static CanLocalizationOptions GetOrAddOptions(IServiceCollection services)
    {
        if (services.FirstOrDefault(d => d.ServiceType == typeof(CanLocalizationOptions))?.ImplementationInstance is CanLocalizationOptions existing)
            return existing;

        var options = new CanLocalizationOptions();
        services.AddSingleton(options);
        return options;
    }
}
