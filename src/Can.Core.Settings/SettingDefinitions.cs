using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Can.Core.Settings;

/// <summary>Ayarın değiştirilebildiği düzeyler.</summary>
[Flags]
[JsonConverter(typeof(JsonStringEnumConverter<SettingScope>))]
public enum SettingScope
{
    None = 0,

    /// <summary>Uygulama geneli (tüm tenant'lar; tenant değeri yoksa bu kullanılır).</summary>
    Global = 1,

    /// <summary>Tenant başına.</summary>
    Tenant = 2,
}

/// <summary>Değerin nereden geldiği.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SettingSource>))]
public enum SettingSource
{
    Default,
    Global,
    Tenant,
}

/// <summary>Bir ayarın tanımı (tipi bilinmeden: listeleme ve uç noktalar için). Tipli hâli <see cref="SettingKey{T}"/>.</summary>
public abstract partial class SettingDefinition
{
    private protected SettingDefinition(string name, Type valueType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!NamePattern().IsMatch(name))
            throw new ArgumentException($"Ayar adı '{name}' geçersiz: küçük harf, rakam, '.', '-', '_' (en çok 200).", nameof(name));
        Name = name;
        ValueType = valueType;
    }

    /// <summary>Sabit ad (<c>orders.max-lines</c>); veritabanında ve uç noktalarda bu kullanılır.</summary>
    public string Name { get; }

    public Type ValueType { get; }

    /// <summary>Hangi düzeylerde değiştirilebilir (varsayılan ikisi de).</summary>
    public SettingScope Scopes { get; init; } = SettingScope.Global | SettingScope.Tenant;

    public string? DisplayName { get; init; }

    public string? Description { get; init; }

    /// <summary>Yönetim ekranında gruplama (ör. "Siparişler").</summary>
    public string? Group { get; init; }

    /// <summary>Gizli (API anahtarı, şifre): şifreli saklanır, listede değeri gösterilmez, istemciye verilmez.</summary>
    public bool IsSecret { get; init; }

    /// <summary>Giriş yapmış her kullanıcıya verilir (<c>/settings/client</c>; arayüz ayarları). Gizli ayarda yok sayılır.</summary>
    public bool IsVisibleToClients { get; init; }

    /// <summary>Değiştirmek için ek yetki (düzeyin yetkisine ek olarak).</summary>
    public string? Permission { get; init; }

    /// <summary>Varsayılan değer (kayıt yoksa).</summary>
    public abstract object? DefaultValueObject { get; }

    /// <summary>Arayüz için tip: <c>boolean</c>, <c>integer</c>, <c>number</c>, <c>string</c>, <c>json</c>.</summary>
    public string Kind => ValueType switch
    {
        _ when ValueType == typeof(bool) => "boolean",
        _ when ValueType == typeof(int) || ValueType == typeof(long) || ValueType == typeof(short) => "integer",
        _ when ValueType == typeof(decimal) || ValueType == typeof(double) || ValueType == typeof(float) => "number",
        _ when ValueType == typeof(string) || ValueType.IsEnum || ValueType == typeof(TimeSpan) || ValueType == typeof(Guid) => "string",
        _ => "json",
    };

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal abstract object? Deserialize(string json);

    internal abstract string Serialize(object? value);

    /// <summary>Hata mesajı ya da geçerliyse <see langword="null"/>.</summary>
    internal abstract string? Check(object? value);

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,199}$")]
    private static partial Regex NamePattern();
}

/// <summary>
/// Tipli ayar anahtarı. Statik alan olarak tanımlanır ve <c>AddCanSettings</c>'te kaydedilir.
/// </summary>
/// <example>
/// <code>
/// public static class OrderSettings
/// {
///     public static readonly SettingKey&lt;int&gt; MaxLines = new SettingKey&lt;int&gt;("orders.max-lines", 100)
///     {
///         DisplayName = "Siparişte en fazla satır",
///         Scopes = SettingScope.Tenant,
///     }.Validate(v =&gt; v is &gt; 0 and &lt;= 1000, "1 ile 1000 arasında olmalı.");
/// }
/// int max = await settings.GetAsync(OrderSettings.MaxLines, ct);
/// </code>
/// </example>
public sealed class SettingKey<T> : SettingDefinition
{
    private readonly List<(Func<T, bool> Rule, string Message)> _rules = [];

    public SettingKey(string name, T defaultValue)
        : base(name, typeof(T)) => DefaultValue = defaultValue;

    public T DefaultValue { get; }

    public override object? DefaultValueObject => DefaultValue;

    /// <summary>Doğrulama kuralı ekler (kaydederken uygulanır).</summary>
    public SettingKey<T> Validate(Func<T, bool> rule, string message)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        _rules.Add((rule, message));
        return this;
    }

    internal override object? Deserialize(string json) => JsonSerializer.Deserialize<T>(json, Json);

    internal override string Serialize(object? value) => JsonSerializer.Serialize((T?)value, Json);

    internal override string? Check(object? value)
    {
        if (value is null && default(T) is not null)
            return "Değer boş olamaz.";
        if (value is not null && value is not T)
            return $"Değer {typeof(T).Name} olmalı.";
        T typed = (T)value!;
        return _rules.FirstOrDefault(r => !r.Rule(typed)).Message;
    }
}
