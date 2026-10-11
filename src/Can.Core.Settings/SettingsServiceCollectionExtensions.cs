using Can.Core.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Can.Core.Settings;

/// <summary>Ayar tanımları ve depo seçimi.</summary>
public sealed class CanSettingsBuilder
{
    internal CanSettingsBuilder(IServiceCollection services, SettingRegistry registry, CanSettingsOptions options)
    {
        Services = services;
        Registry = registry;
        Options = options;
    }

    public IServiceCollection Services { get; }

    public CanSettingsOptions Options { get; }

    internal SettingRegistry Registry { get; }

    /// <summary>Ayarları tanımlar.</summary>
    public CanSettingsBuilder Add(params SettingDefinition[] definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        foreach (SettingDefinition definition in definitions)
            Registry.Add(definition);
        return this;
    }

    /// <summary>Kendi deponu kullan (scoped).</summary>
    public CanSettingsBuilder UseStore<TStore>()
        where TStore : class, ISettingStore
    {
        Services.RemoveAll<ISettingStore>();
        Services.AddScoped<ISettingStore, TStore>();
        return this;
    }
}

public static class SettingsServiceCollectionExtensions
{
    /// <summary>
    /// Ayarlar. Depo verilmezse bellek içi (EF Core: <c>Can.Core.Settings.EntityFrameworkCore</c> →
    /// <c>.UseEntityFrameworkStore&lt;TContext&gt;()</c>). Birden çok çağrı tanımları birleştirir.
    /// </summary>
    /// <example>
    /// <code>
    /// builder.Services.AddCanSettings(s =&gt; s
    ///     .Add(OrderSettings.MaxLines, OrderSettings.Currency, SmsSettings.ApiKey)
    ///     .UseEntityFrameworkStore&lt;AppDbContext&gt;());
    /// </code>
    /// </example>
    public static CanSettingsBuilder AddCanSettings(this IServiceCollection services, Action<CanSettingsBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        SettingRegistry registry = services.FirstOrDefault(d => d.ServiceType == typeof(SettingRegistry))?.ImplementationInstance as SettingRegistry ?? Register(services);
        CanSettingsOptions options = (CanSettingsOptions)services.First(d => d.ServiceType == typeof(CanSettingsOptions)).ImplementationInstance!;
        var builder = new CanSettingsBuilder(services, registry, options);
        configure?.Invoke(builder);
        return builder;
    }

    private static SettingRegistry Register(IServiceCollection services)
    {
        var registry = new SettingRegistry();
        services.AddSingleton(registry);
        services.AddSingleton(new CanSettingsOptions());
        services.AddHybridCache();
        services.TryAddSingleton<ICurrentUser>(NullCurrentUser.Instance);       // AddCanWebApi / AddCanMultiTenancy gerçeklerini kaydeder
        services.TryAddSingleton<ICurrentTenant>(NullCurrentTenant.Instance);
        services.TryAddSingleton<ISettingStore, InMemorySettingStore>();
        services.TryAddScoped<SettingService>();
        services.TryAddScoped<ISettings>(sp => sp.GetRequiredService<SettingService>());
        services.TryAddScoped<ISettingManager>(sp => sp.GetRequiredService<SettingService>());
        return registry;
    }
}
