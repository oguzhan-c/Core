using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Can.Core.Application.Exceptions;
using Can.Core.Domain.Results;
using Can.Core.Mediator;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Primitives;

namespace Can.Core.Application.Features;

/// <summary><c>AddCanFeatureFlags(...)</c> ile koddan verilen tanımlar.</summary>
public sealed class CanFeatureFlagOptions
{
    public IDictionary<string, FeatureDefinition> Features { get; } = new Dictionary<string, FeatureDefinition>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Kullanıcı, rol, tenant ve yüzdelik kurallarını o anki isteğe göre değerlendirir (scoped).</summary>
public sealed class FeatureFlags : IFeatureFlags
{
    private readonly IFeatureDefinitionProvider _provider;
    private readonly ICurrentUser _user;
    private readonly ICurrentTenant _tenant;
    private readonly TimeProvider _time;

    public FeatureFlags(IFeatureDefinitionProvider provider, ICurrentUser user, ICurrentTenant tenant, TimeProvider? timeProvider = null)
    {
        _provider = provider;
        _user = user;
        _tenant = tenant;
        _time = timeProvider ?? TimeProvider.System;
    }

    public async Task<bool> IsEnabledAsync(string feature, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(feature);
        IReadOnlyDictionary<string, FeatureDefinition> all = await _provider.GetAllAsync(cancellationToken).ConfigureAwait(false);
        return all.TryGetValue(feature, out FeatureDefinition? definition) && Evaluate(feature, definition);
    }

    public async Task<IReadOnlyList<string>> GetEnabledAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyDictionary<string, FeatureDefinition> all = await _provider.GetAllAsync(cancellationToken).ConfigureAwait(false);
        return all.Where(f => Evaluate(f.Key, f.Value)).Select(f => f.Key).Order(StringComparer.Ordinal).ToArray();
    }

    private bool Evaluate(string feature, FeatureDefinition definition)
    {
        DateTimeOffset now = _time.GetUtcNow();
        if (definition.EnabledFrom > now || definition.EnabledUntil <= now)
            return false;

        string? tenant = _tenant.Id is { } id ? Convert.ToString(id, CultureInfo.InvariantCulture) : null;
        if (tenant is not null && definition.ExcludedTenants.Contains(tenant, StringComparer.OrdinalIgnoreCase))
            return false;

        if (definition.Enabled)
            return true;
        if (_user.Id is { } user && definition.Users.Contains(user, StringComparer.OrdinalIgnoreCase))
            return true;
        if (definition.Roles.Any(_user.IsInRole))
            return true;
        if (tenant is not null && definition.Tenants.Contains(tenant, StringComparer.OrdinalIgnoreCase))
            return true;

        return definition.RolloutPercentage is { } percentage && (_user.Id ?? tenant) is { } subject && Bucket(feature, subject) < percentage;
    }

    /// <summary>Özellik + kişiye göre sabit 0-99 arası dilim (özellik başına farklı kişiler ilk dilimde olsun diye ad da katılır).</summary>
    internal static int Bucket(string feature, string subject)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(feature.ToLowerInvariant() + "\n" + subject), hash);
        return (int)(BinaryPrimitives.ReadUInt32BigEndian(hash) % 100);
    }
}

/// <summary>Koddan verilen tanımlar.</summary>
internal sealed class OptionsFeatureDefinitionProvider(CanFeatureFlagOptions options) : IFeatureDefinitionProvider
{
    private readonly IReadOnlyDictionary<string, FeatureDefinition> _features =
        new Dictionary<string, FeatureDefinition>(options.Features, StringComparer.OrdinalIgnoreCase);

    public ValueTask<IReadOnlyDictionary<string, FeatureDefinition>> GetAllAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(_features);
}

/// <summary>
/// Ayar bölümünden tanımlar; dosya değişince (appsettings <c>reloadOnChange</c>) yeniden okunur, uygulama yeniden
/// başlatılmaz. Kısa yazım: <c>"dark-mode": true</c>.
/// </summary>
internal sealed class ConfigurationFeatureDefinitionProvider : IFeatureDefinitionProvider, IDisposable
{
    private readonly IConfiguration _section;
    private readonly IDisposable _subscription;
    private volatile IReadOnlyDictionary<string, FeatureDefinition> _features;

    public ConfigurationFeatureDefinitionProvider(IConfiguration section)
    {
        _section = section;
        _features = Load(section);
        _subscription = ChangeToken.OnChange(section.GetReloadToken, () => _features = Load(_section));
    }

    public ValueTask<IReadOnlyDictionary<string, FeatureDefinition>> GetAllAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(_features);

    internal static IReadOnlyDictionary<string, FeatureDefinition> Load(IConfiguration section)
    {
        var features = new Dictionary<string, FeatureDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (IConfigurationSection child in section.GetChildren())
        {
            if (child.Value is { } value)
            {
                features[child.Key] = new FeatureDefinition { Enabled = bool.TryParse(value, out bool enabled) && enabled };
                continue;
            }

            var definition = new FeatureDefinition();
            child.Bind(definition);
            features[child.Key] = definition;
        }

        return features;
    }

    public void Dispose() => _subscription.Dispose();
}

/// <summary>
/// <see cref="IFeatureGatedRequest"/>: gerekli özelliklerden biri kapalıysa <see cref="NotFoundException"/>
/// (<c>feature_disabled</c>; Result dönen isteklerde <c>Error.NotFound</c>). Yetkilendirmeden ÖNCE çalışır.
/// </summary>
public sealed class FeatureGateBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IFeatureGatedRequest
{
    private readonly IFeatureFlags _features;

    public FeatureGateBehavior(IFeatureFlags features) => _features = features;

    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        foreach (string feature in request.RequiredFeatures)
        {
            if (await _features.IsEnabledAsync(feature, cancellationToken).ConfigureAwait(false))
                continue;

            Error error = Error.NotFound("feature_disabled", "Bu özellik kullanılamıyor.");
            return ResultTypes.TryCreateFailure([error], out TResponse failure)
                ? failure
                : throw new NotFoundException(error.Description) { Code = error.Code };
        }

        return await next().ConfigureAwait(false);
    }
}

public static class FeatureFlagServiceCollectionExtensions
{
    /// <summary>Özellik tanımlarını koddan verir.</summary>
    /// <example>
    /// <code>
    /// services.AddCanFeatureFlags(o =&gt; o.Features["new-checkout"] = new FeatureDefinition { Tenants = { "acme" }, RolloutPercentage = 10 });
    /// </code>
    /// </example>
    public static IServiceCollection AddCanFeatureFlags(this IServiceCollection services, Action<CanFeatureFlagOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        var options = new CanFeatureFlagOptions();
        configure(options);
        return services.AddCanFeatureFlags(new OptionsFeatureDefinitionProvider(options));
    }

    /// <summary>Özellik tanımlarını ayar bölümünden okur (değişince yeniden yüklenir).</summary>
    /// <example>
    /// <code>
    /// // appsettings.json
    /// "Features": {
    ///   "dark-mode": true,
    ///   "new-checkout": { "Tenants": [ "acme" ], "RolloutPercentage": 10, "ExcludedTenants": [ "globex" ] }
    /// }
    /// builder.Services.AddCanFeatureFlags(builder.Configuration.GetSection("Features"));
    /// </code>
    /// </example>
    public static IServiceCollection AddCanFeatureFlags(this IServiceCollection services, IConfiguration section)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(section);
        return services.AddCanFeatureFlags(new ConfigurationFeatureDefinitionProvider(section));
    }

    /// <summary>Kendi tanım kaynağın (ör. veritabanı; önbelleğe almayı kendisi yapmalı).</summary>
    public static IServiceCollection AddCanFeatureFlags(this IServiceCollection services, IFeatureDefinitionProvider provider)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(provider);
        services.RemoveAll<IFeatureDefinitionProvider>();
        services.AddSingleton(provider);
        services.TryAddScoped<IFeatureFlags, FeatureFlags>();
        return services;
    }

    /// <summary>Tanım kaynağı kayıtlı değilse: hiçbir özellik tanımlı değil, hepsi kapalı.</summary>
    internal static IServiceCollection AddCanFeatureFlagDefaults(this IServiceCollection services)
    {
        services.TryAddSingleton<IFeatureDefinitionProvider>(new OptionsFeatureDefinitionProvider(new CanFeatureFlagOptions()));
        services.TryAddScoped<IFeatureFlags, FeatureFlags>();
        return services;
    }
}
