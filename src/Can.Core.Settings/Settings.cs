using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Can.Core.Application;
using Can.Core.Application.Exceptions;
using Microsoft.Extensions.Caching.Hybrid;

namespace Can.Core.Settings;

/// <summary>Ayarları okur: o anki tenant'ın değeri → uygulama geneli değer → varsayılan (scoped).</summary>
public interface ISettings
{
    Task<T> GetAsync<T>(SettingKey<T> key, CancellationToken cancellationToken = default);

    /// <summary>
    /// <see cref="SettingDefinition.IsVisibleToClients"/> ayarların değerleri (gizliler hariç): arayüz bunlarla çalışır.
    /// </summary>
    Task<IReadOnlyDictionary<string, object?>> GetClientValuesAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Ayarları değiştirir ve listeler. Yetki denetimi YAPMAZ: çağıran denetler (uç noktalar <c>MapCanSettings</c>'te
/// denetler). <see cref="SettingScope.Tenant"/> o anki tenant içindir.
/// </summary>
public interface ISettingManager
{
    IReadOnlyList<SettingDefinition> Definitions { get; }

    Task SetAsync<T>(SettingKey<T> key, T value, SettingScope scope, CancellationToken cancellationToken = default);

    /// <summary>JSON değerle (uç noktalar); tip ve kurallar denetlenir.</summary>
    /// <exception cref="NotFoundException">Ayar tanımlı değil.</exception>
    /// <exception cref="ValidationException">Değer geçersiz ya da düzey desteklenmiyor.</exception>
    Task SetAsync(string name, JsonElement value, SettingScope scope, CancellationToken cancellationToken = default);

    /// <summary>Düzeydeki değeri siler (bir üst düzeye ya da varsayılana döner).</summary>
    Task ResetAsync(string name, SettingScope scope, CancellationToken cancellationToken = default);

    /// <summary>Tüm ayarlar, o düzeyde görülen etkin değerleriyle (gizli değerler gösterilmez).</summary>
    Task<IReadOnlyList<SettingView>> ListAsync(SettingScope scope, CancellationToken cancellationToken = default);
}

/// <summary>Yönetim ekranı için ayar.</summary>
/// <param name="Name">Ayarın adı.</param>
/// <param name="DisplayName">Görünen ad.</param>
/// <param name="Description">Açıklama.</param>
/// <param name="Group">Grup.</param>
/// <param name="Kind">Arayüz tipi (<see cref="SettingDefinition.Kind"/>).</param>
/// <param name="Scopes">Değiştirilebildiği düzeyler.</param>
/// <param name="IsSecret">Gizli mi.</param>
/// <param name="Value">Etkin değer (gizli ayarda <see langword="null"/>).</param>
/// <param name="DefaultValue">Varsayılan değer (gizli ayarda <see langword="null"/>).</param>
/// <param name="Source">Değerin geldiği düzey.</param>
/// <param name="IsOverridden">Bu düzeyde kayıtlı bir değer var mı (sıfırlanabilir).</param>
public sealed record SettingView(
    string Name,
    string? DisplayName,
    string? Description,
    string? Group,
    string Kind,
    SettingScope Scopes,
    bool IsSecret,
    object? Value,
    object? DefaultValue,
    SettingSource Source,
    bool IsOverridden);

/// <summary>Ayar değerlerinin deposu. Tenant anahtarı: tenant kimliği, uygulama geneli için boş metin.</summary>
public interface ISettingStore
{
    Task<Dictionary<string, string>> GetAllAsync(string tenantKey, CancellationToken cancellationToken = default);

    Task SetAsync(string name, string tenantKey, string value, string? userId, CancellationToken cancellationToken = default);

    Task RemoveAsync(string name, string tenantKey, CancellationToken cancellationToken = default);
}

/// <summary>Gizli ayarları saklamadan önce şifreler (ASP.NET Data Protection: <c>UseDataProtection()</c>).</summary>
public interface ISettingProtector
{
    string Protect(string value);

    string Unprotect(string value);
}

/// <summary>Bellek içi depo (testler ve tek süreçli denemeler; yeniden başlayınca unutur).</summary>
public sealed class InMemorySettingStore : ISettingStore
{
    private readonly ConcurrentDictionary<(string Name, string Tenant), string> _values = new();

    public Task<Dictionary<string, string>> GetAllAsync(string tenantKey, CancellationToken cancellationToken = default) =>
        Task.FromResult(_values.Where(v => v.Key.Tenant == tenantKey).ToDictionary(v => v.Key.Name, v => v.Value, StringComparer.Ordinal));

    public Task SetAsync(string name, string tenantKey, string value, string? userId, CancellationToken cancellationToken = default)
    {
        _values[(name, tenantKey)] = value;
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string name, string tenantKey, CancellationToken cancellationToken = default)
    {
        _values.TryRemove((name, tenantKey), out _);
        return Task.CompletedTask;
    }
}

/// <summary>Kayıtlı tanımlar.</summary>
public sealed class SettingRegistry
{
    private readonly Dictionary<string, SettingDefinition> _definitions = new(StringComparer.Ordinal);

    internal SettingRegistry() { }

    public IReadOnlyList<SettingDefinition> All => [.. _definitions.Values.OrderBy(d => d.Group).ThenBy(d => d.Name, StringComparer.Ordinal)];

    internal void Add(SettingDefinition definition)
    {
        if (definition.Scopes == SettingScope.None)
            throw new ArgumentException($"'{definition.Name}' en az bir düzeyde değiştirilebilmeli.", nameof(definition));
        if (!_definitions.TryAdd(definition.Name, definition))
            throw new InvalidOperationException($"'{definition.Name}' ayarı iki kez tanımlandı.");
    }

    public SettingDefinition? Find(string name) => _definitions.GetValueOrDefault(name);
}

/// <summary>Ayar seçenekleri.</summary>
public sealed class CanSettingsOptions
{
    /// <summary>Önbellek süresi (Redis gibi ikinci düzey dahil).</summary>
    public TimeSpan CacheExpiration { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Sunucunun kendi belleğindeki kopyanın süresi: değişiklik diğer sunuculara en geç bu kadar sonra yansır (aynı
    /// sunucuda hemen).
    /// </summary>
    public TimeSpan LocalCacheExpiration { get; set; } = TimeSpan.FromMinutes(1);
}

internal sealed class SettingService : ISettings, ISettingManager
{
    private const string CacheTag = "can-settings";

    private readonly SettingRegistry _registry;
    private readonly ISettingStore _store;
    private readonly HybridCache _cache;
    private readonly ICurrentTenant _tenant;
    private readonly ICurrentUser _user;
    private readonly ISettingProtector? _protector;
    private readonly HybridCacheEntryOptions _entry;

    public SettingService(SettingRegistry registry, ISettingStore store, HybridCache cache, ICurrentTenant tenant, ICurrentUser user, CanSettingsOptions options, ISettingProtector? protector = null)
    {
        _registry = registry;
        _store = store;
        _cache = cache;
        _tenant = tenant;
        _user = user;
        _protector = protector;
        _entry = new HybridCacheEntryOptions { Expiration = options.CacheExpiration, LocalCacheExpiration = options.LocalCacheExpiration };
    }

    public IReadOnlyList<SettingDefinition> Definitions => _registry.All;

    private string? TenantKey => _tenant.Id is { } id ? Convert.ToString(id, CultureInfo.InvariantCulture) : null;

    // ------------------------------------------------------------ okuma

    public async Task<T> GetAsync<T>(SettingKey<T> key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        SettingDefinition definition = Require(key.Name);
        (string? raw, _) = await ResolveAsync(definition, SettingScope.Global | SettingScope.Tenant, cancellationToken).ConfigureAwait(false);
        return raw is null ? key.DefaultValue : (T)Read(definition, raw)!;
    }

    public async Task<IReadOnlyDictionary<string, object?>> GetClientValuesAsync(CancellationToken cancellationToken = default)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (SettingDefinition definition in _registry.All.Where(d => d.IsVisibleToClients && !d.IsSecret))
        {
            (string? raw, _) = await ResolveAsync(definition, SettingScope.Global | SettingScope.Tenant, cancellationToken).ConfigureAwait(false);
            values[definition.Name] = raw is null ? definition.DefaultValueObject : Read(definition, raw);
        }

        return values;
    }

    /// <summary><paramref name="upTo"/> düzeyinden aşağı (tenant → global → varsayılan) ilk kayıtlı değer.</summary>
    private async Task<(string? Raw, SettingSource Source)> ResolveAsync(SettingDefinition definition, SettingScope upTo, CancellationToken cancellationToken)
    {
        if (upTo.HasFlag(SettingScope.Tenant) && definition.Scopes.HasFlag(SettingScope.Tenant) && TenantKey is { } tenant
            && (await LoadAsync(tenant, cancellationToken).ConfigureAwait(false)).TryGetValue(definition.Name, out string? tenantValue))
            return (tenantValue, SettingSource.Tenant);

        if (definition.Scopes.HasFlag(SettingScope.Global)
            && (await LoadAsync(string.Empty, cancellationToken).ConfigureAwait(false)).TryGetValue(definition.Name, out string? globalValue))
            return (globalValue, SettingSource.Global);

        return (null, SettingSource.Default);
    }

    private ValueTask<Dictionary<string, string>> LoadAsync(string tenantKey, CancellationToken cancellationToken) =>
        _cache.GetOrCreateAsync(
            CacheKey(tenantKey),
            (_store, tenantKey),
            static (state, ct) => new ValueTask<Dictionary<string, string>>(state._store.GetAllAsync(state.tenantKey, ct)),
            _entry,
            [CacheTag],
            cancellationToken);

    private object? Read(SettingDefinition definition, string raw)
    {
        if (definition.IsSecret)
            raw = (_protector ?? throw NoProtector(definition)).Unprotect(raw);
        return definition.Deserialize(raw);
    }

    // ------------------------------------------------------------ yazma

    public Task SetAsync<T>(SettingKey<T> key, T value, SettingScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        return WriteAsync(Require(key.Name), value, scope, cancellationToken);
    }

    public Task SetAsync(string name, JsonElement value, SettingScope scope, CancellationToken cancellationToken = default)
    {
        SettingDefinition definition = _registry.Find(name) ?? throw new NotFoundException($"'{name}' ayarı yok.") { Code = "setting_not_found" };
        object? typed;
        try
        {
            typed = definition.Deserialize(value.GetRawText());
        }
        catch (JsonException)
        {
            throw new ValidationException("value", $"Değer {definition.Kind} olmalı.") { Code = "setting_invalid" };
        }

        return WriteAsync(definition, typed, scope, cancellationToken);
    }

    public async Task ResetAsync(string name, SettingScope scope, CancellationToken cancellationToken = default)
    {
        SettingDefinition definition = _registry.Find(name) ?? throw new NotFoundException($"'{name}' ayarı yok.") { Code = "setting_not_found" };
        string tenantKey = TenantKeyFor(definition, scope);
        await _store.RemoveAsync(definition.Name, tenantKey, cancellationToken).ConfigureAwait(false);
        await _cache.RemoveAsync(CacheKey(tenantKey), cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteAsync(SettingDefinition definition, object? value, SettingScope scope, CancellationToken cancellationToken)
    {
        string tenantKey = TenantKeyFor(definition, scope);
        if (definition.Check(value) is { } error)
            throw new ValidationException("value", error) { Code = "setting_invalid" };

        string raw = definition.Serialize(value);
        if (definition.IsSecret)
            raw = (_protector ?? throw NoProtector(definition)).Protect(raw);

        await _store.SetAsync(definition.Name, tenantKey, raw, _user.Id, cancellationToken).ConfigureAwait(false);
        await _cache.RemoveAsync(CacheKey(tenantKey), cancellationToken).ConfigureAwait(false);
    }

    private string TenantKeyFor(SettingDefinition definition, SettingScope scope)
    {
        if (scope is not (SettingScope.Global or SettingScope.Tenant) || !definition.Scopes.HasFlag(scope))
            throw new ValidationException("scope", $"'{definition.Name}' {scope} düzeyinde değiştirilemez.") { Code = "setting_scope_invalid" };
        if (scope == SettingScope.Global)
            return string.Empty;
        return TenantKey ?? throw new ValidationException("scope", "Tenant düzeyi için aktif tenant gerekli.") { Code = "setting_scope_invalid" };
    }

    // ------------------------------------------------------------ listeleme

    public async Task<IReadOnlyList<SettingView>> ListAsync(SettingScope scope, CancellationToken cancellationToken = default)
    {
        if (scope is not (SettingScope.Global or SettingScope.Tenant))
            throw new ValidationException("scope", "Düzey Global ya da Tenant olmalı.") { Code = "setting_scope_invalid" };
        if (scope == SettingScope.Tenant && TenantKey is null)
            throw new ValidationException("scope", "Tenant düzeyi için aktif tenant gerekli.") { Code = "setting_scope_invalid" };

        var views = new List<SettingView>();
        foreach (SettingDefinition definition in _registry.All.Where(d => d.Scopes.HasFlag(scope)))
        {
            (string? raw, SettingSource source) = await ResolveAsync(definition, scope, cancellationToken).ConfigureAwait(false);
            object? value = definition.IsSecret ? null : raw is null ? definition.DefaultValueObject : Read(definition, raw);
            bool overridden = scope == SettingScope.Tenant ? source == SettingSource.Tenant : source == SettingSource.Global;
            views.Add(new SettingView(
                definition.Name, definition.DisplayName, definition.Description, definition.Group, definition.Kind, definition.Scopes,
                definition.IsSecret, value, definition.IsSecret ? null : definition.DefaultValueObject, source, overridden));
        }

        return views;
    }

    // ------------------------------------------------------------ yardımcılar

    private SettingDefinition Require(string name) =>
        _registry.Find(name) ?? throw new InvalidOperationException($"'{name}' ayarı AddCanSettings'te tanımlı değil.");

    private static string CacheKey(string tenantKey) => "can:settings:" + (tenantKey.Length == 0 ? "-" : tenantKey);

    private static InvalidOperationException NoProtector(SettingDefinition definition) =>
        new($"'{definition.Name}' gizli bir ayar; ISettingProtector kayıtlı değil (Can.Core.Settings.AspNetCore: UseDataProtection()).");
}
