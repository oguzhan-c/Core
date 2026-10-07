using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json.Serialization;

namespace Can.Core.Reporting;

[JsonConverter(typeof(JsonStringEnumConverter<ReportDataType>))]
public enum ReportDataType
{
    String,
    Number,
    Date,
    Boolean,
}

/// <summary>Veri kaynağındaki bir alan.</summary>
/// <param name="Name">Alan adı (ifadelerde <c>[name]</c>).</param>
/// <param name="Type">Tipi.</param>
public sealed record ReportField(string Name, ReportDataType Type)
{
    public string? Caption { get; init; }

    public string? Format { get; init; }

    /// <summary>Hesaplanmış alan mı.</summary>
    public bool IsCalculated { get; init; }

    public static ReportDataType TypeOf(Type type)
    {
        Type t = Nullable.GetUnderlyingType(type) ?? type;
        if (t == typeof(bool))
            return ReportDataType.Boolean;
        if (t == typeof(DateTime) || t == typeof(DateTimeOffset) || t == typeof(DateOnly))
            return ReportDataType.Date;
        if (t.IsPrimitive && t != typeof(char) || t == typeof(decimal))
            return ReportDataType.Number;
        return ReportDataType.String;
    }
}

/// <summary>Motorun işlediği veri: alanlar + satırlar (her satır alan sırasında değerler).</summary>
public sealed class ReportDataSet
{
    private readonly Dictionary<string, int> _indexes;

    public ReportDataSet(IReadOnlyList<ReportField> fields, IReadOnlyList<object?[]> rows)
    {
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(rows);
        Fields = fields;
        Rows = rows;
        _indexes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < fields.Count; i++)
        {
            if (!_indexes.TryAdd(fields[i].Name, i))
                throw new ArgumentException($"'{fields[i].Name}' alanı iki kez tanımlı.", nameof(fields));
        }
    }

    public IReadOnlyList<ReportField> Fields { get; }

    public IReadOnlyList<object?[]> Rows { get; }

    /// <summary>Alanın sırası (büyük/küçük harf duyarsız); yoksa -1.</summary>
    public int IndexOf(string name) => _indexes.GetValueOrDefault(name, -1);

    /// <summary>Nesnelerden: herkese açık özellikler alan olur (adlar camelCase: <c>UnitPrice</c> → <c>unitPrice</c>).</summary>
    public static ReportDataSet From<T>(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        (ReportField[] fields, Func<T, object?[]> read) = Accessor<T>.Instance;
        return new ReportDataSet(fields, items.Select(read).ToArray());
    }

    /// <summary>Alan adı → tip eşlemesi; satırlar alan sırasında.</summary>
    public static ReportDataSet From(IEnumerable<(string Name, ReportDataType Type)> fields, IEnumerable<object?[]> rows) =>
        new(fields.Select(f => new ReportField(f.Name, f.Type)).ToArray(), rows.ToArray());

    internal static string CamelCase(string name) =>
        string.IsNullOrEmpty(name) || char.IsLower(name[0]) ? name : char.ToLowerInvariant(name[0]) + name[1..];

    /// <summary>Tip başına bir kez derlenen okuyucu (yansıma yerine derlenmiş ifade).</summary>
    private static class Accessor<T>
    {
        public static readonly (ReportField[] Fields, Func<T, object?[]> Read) Instance = Build();

        private static (ReportField[], Func<T, object?[]>) Build()
        {
            PropertyInfo[] properties = typeof(T)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
                .ToArray();

            ParameterExpression item = Expression.Parameter(typeof(T), "item");
            NewArrayExpression values = Expression.NewArrayInit(
                typeof(object),
                properties.Select(p => Expression.Convert(Expression.Property(item, p), typeof(object)))
            );

            return (
                properties.Select(p => new ReportField(CamelCase(p.Name), ReportField.TypeOf(p.PropertyType))).ToArray(),
                Expression.Lambda<Func<T, object?[]>>(values, item).Compile()
            );
        }
    }
}
