namespace Can.Core.Persistence.Dynamic;

// nArchitecture ile aynı JSON şekli; mevcut ön yüzler değişmeden çalışır:
//
// {
//   "sort":   [{ "field": "price", "dir": "desc" }],
//   "filter": {
//     "field": "name", "operator": "contains", "value": "kalem",
//     "logic": "or",
//     "filters": [{ "field": "category.name", "operator": "eq", "value": "Ofis" }]
//   }
// }

/// <summary>İstemciden gelen filtre + sıralama.</summary>
public sealed class DynamicQuery
{
    public DynamicQuery() { }

    public DynamicQuery(IEnumerable<Sort>? sort, Filter? filter)
    {
        Sort = sort;
        Filter = filter;
    }

    public IEnumerable<Sort>? Sort { get; set; }

    public Filter? Filter { get; set; }
}

/// <summary>
/// Tek bir koşul ve isteğe bağlı alt koşullar. Sonuç: <c>(bu koşul) LOGIC (alt1 LOGIC alt2 ...)</c>.
/// <see cref="Field"/> boş bırakılırsa düğüm sadece alt koşulları gruplar.
/// </summary>
public sealed class Filter
{
    public Filter() { }

    public Filter(string field, string @operator, string? value = null)
    {
        Field = field;
        Operator = @operator;
        Value = value;
    }

    /// <summary>Property adı; iç içe için nokta: <c>category.name</c>. Büyük/küçük harf duyarsız.</summary>
    public string? Field { get; set; }

    /// <summary><see cref="FilterOperators"/> içindeki değerlerden biri.</summary>
    public string? Operator { get; set; }

    /// <summary>Değer. <c>in</c> için virgülle ayrılmış liste, <c>between</c> için <c>"alt,üst"</c>.</summary>
    public string? Value { get; set; }

    /// <summary><c>and</c> (varsayılan) ya da <c>or</c>.</summary>
    public string? Logic { get; set; }

    /// <summary>Metin karşılaştırmalarında (contains, startswith, endswith, doesnotcontain, in) büyük/küçük harf duyarlılığı.</summary>
    public bool CaseSensitive { get; set; }

    public IEnumerable<Filter>? Filters { get; set; }
}

public sealed class Sort
{
    public Sort() { }

    public Sort(string field, string dir = "asc")
    {
        Field = field;
        Dir = dir;
    }

    public string Field { get; set; } = string.Empty;

    /// <summary><c>asc</c> ya da <c>desc</c>.</summary>
    public string Dir { get; set; } = "asc";
}

public static class FilterOperators
{
    public const string Equal = "eq";
    public const string NotEqual = "neq";
    public const string LessThan = "lt";
    public const string LessThanOrEqual = "lte";
    public const string GreaterThan = "gt";
    public const string GreaterThanOrEqual = "gte";
    public const string IsNull = "isnull";
    public const string IsNotNull = "isnotnull";
    public const string StartsWith = "startswith";
    public const string EndsWith = "endswith";
    public const string Contains = "contains";
    public const string DoesNotContain = "doesnotcontain";
    public const string In = "in";
    public const string Between = "between";
}
