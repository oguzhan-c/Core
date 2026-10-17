namespace Can.Core.Generator;

/// <summary>Domain'deki bir entity'nin üretim için gereken özeti.</summary>
public sealed record EntityModel
{
    public required string Name { get; init; }

    public required string Namespace { get; init; }

    /// <summary>Kimlik tipi (<c>Entity&lt;TId&gt;</c>'deki TId), ör. <c>Guid</c>.</summary>
    public required string IdType { get; init; }

    /// <summary>Taban sınıf adı (generic argümansız), ör. <c>FullAuditedAggregateRoot</c>.</summary>
    public required string BaseType { get; init; }

    public required string FilePath { get; init; }

    /// <summary><c>CreatedAt</c> / <c>UpdatedAt</c> alanları var mı (Audited taban sınıflar).</summary>
    public bool IsAudited { get; init; }

    /// <summary>Silinince kayıt kalır, filtrelenir (FullAudited ya da <c>ISoftDeletable</c>).</summary>
    public bool IsSoftDeletable { get; init; }

    public bool IsMultiTenant { get; init; }

    /// <summary>Kendi tanımladığı, okunabilen özellikler (taban sınıftakiler hariç).</summary>
    public required IReadOnlyList<EntityProperty> Properties { get; init; }

    /// <summary><c>public static Result&lt;T&gt; Create(...)</c> ya da <c>public static T Create(...)</c>.</summary>
    public EntityMethod? Factory { get; init; }

    /// <summary><c>public ... Update(...)</c>.</summary>
    public EntityMethod? Update { get; init; }

    /// <summary>Entity'deki <c>const int</c> alanlar (ör. <c>NameMaxLength</c>).</summary>
    public required IReadOnlySet<string> IntConstants { get; init; }

    /// <summary>Tabloya düz sütun olarak yazılan (DTO'ya girebilen) özellikler.</summary>
    public IEnumerable<EntityProperty> ScalarProperties => Properties.Where(p => p.Kind is PropertyKind.Scalar or PropertyKind.Enum);

    /// <summary>Özelliğin uzunluk sabiti (<c>{Ad}MaxLength</c>) varsa adı.</summary>
    public string? MaxLengthConstant(string propertyName)
    {
        string name = Names.Pascal(propertyName) + "MaxLength";
        return IntConstants.Contains(name) ? name : null;
    }
}

public enum PropertyKind
{
    /// <summary>string, sayı, bool, Guid, tarih/saat.</summary>
    Scalar,

    /// <summary>Domain'de tanımlı enum.</summary>
    Enum,

    /// <summary>Liste / koleksiyon (alt entity'ler).</summary>
    Collection,

    /// <summary>Başka bir entity ya da value object.</summary>
    Complex,
}

public sealed record EntityProperty(string Name, string Type, bool IsNullable, PropertyKind Kind, bool HasSetter)
{
    /// <summary>Nullable işareti olmadan tip (ör. <c>int?</c> → <c>int</c>).</summary>
    public string BareType => Type.TrimEnd('?');

    public bool IsString => BareType is "string" or "String";
}

public sealed record EntityMethod(string Name, IReadOnlyList<MethodParameter> Parameters, string ReturnType)
{
    /// <summary>Dönüş tipi <c>Result</c> / <c>Result&lt;T&gt;</c> mi.</summary>
    public bool ReturnsResult => ReturnType == "Result" || ReturnType.StartsWith("Result<", StringComparison.Ordinal);

    public bool ReturnsVoid => ReturnType == "void";
}

public sealed record MethodParameter(string Name, string Type)
{
    public bool IsNullable => Type.EndsWith('?');

    public string BareType => Type.TrimEnd('?');

    public bool IsString => BareType is "string" or "String";
}
