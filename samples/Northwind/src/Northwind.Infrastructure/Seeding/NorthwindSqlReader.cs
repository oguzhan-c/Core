using System.Globalization;
using System.Text;

namespace Northwind.Infrastructure.Seeding;

/// <summary>
/// Gömülü <c>northwind.sql</c> dosyasındaki <c>INSERT INTO tablo VALUES (...);</c> satırlarını okur.
/// Yalnızca bu dosyanın biçimini destekler (tek satırlık INSERT'ler, tek tırnaklı metinler, NULL ve sayılar).
/// </summary>
internal sealed class NorthwindSqlReader
{
    private readonly Dictionary<string, List<string?[]>> _tables = new(StringComparer.Ordinal);

    private NorthwindSqlReader() { }

    public static NorthwindSqlReader Load()
    {
        using Stream stream =
            typeof(NorthwindSqlReader).Assembly.GetManifestResourceStream("Northwind.Infrastructure.Seeding.northwind.sql")
            ?? throw new InvalidOperationException("Gömülü northwind.sql bulunamadı.");

        using var reader = new StreamReader(stream, Encoding.UTF8);
        var result = new NorthwindSqlReader();

        while (reader.ReadLine() is { } line)
        {
            if (!line.StartsWith("INSERT INTO ", StringComparison.Ordinal))
                continue;

            int valuesAt = line.IndexOf(" VALUES (", StringComparison.Ordinal);
            string table = line["INSERT INTO ".Length..valuesAt];
            string values = line[(valuesAt + " VALUES (".Length)..line.LastIndexOf(");", StringComparison.Ordinal)];

            if (!result._tables.TryGetValue(table, out List<string?[]>? rows))
                result._tables[table] = rows = [];

            rows.Add(ParseValues(values));
        }

        return result;
    }

    public IReadOnlyList<Row> Rows(string table) =>
        _tables.TryGetValue(table, out List<string?[]>? rows) ? rows.Select(r => new Row(r)).ToList() : [];

    private static string?[] ParseValues(string values)
    {
        var result = new List<string?>();
        int i = 0;

        while (i < values.Length)
        {
            while (i < values.Length && values[i] == ' ')
                i++;

            if (i < values.Length && values[i] == '\'')
            {
                var text = new StringBuilder();
                i++;
                while (i < values.Length)
                {
                    if (values[i] == '\'')
                    {
                        if (i + 1 < values.Length && values[i + 1] == '\'')
                        {
                            text.Append('\'');
                            i += 2;
                            continue;
                        }

                        i++;
                        break;
                    }

                    text.Append(values[i++]);
                }

                result.Add(text.ToString().Replace("\\n", "\n", StringComparison.Ordinal));
            }
            else
            {
                int start = i;
                while (i < values.Length && values[i] != ',')
                    i++;

                string token = values[start..i].Trim();
                result.Add(token == "NULL" ? null : token);
            }

            // virgülü atla
            while (i < values.Length && values[i] != ',')
                i++;
            i++;
        }

        return result.ToArray();
    }

    /// <summary>Bir satırın değerleri; sütunlar sırayla okunur.</summary>
    internal readonly struct Row
    {
        private readonly string?[] _values;

        public Row(string?[] values) => _values = values;

        public string? Text(int index)
        {
            string? value = _values[index];
            return string.IsNullOrWhiteSpace(value) || value == "\\x" ? null : value.Trim();
        }

        public string RequiredText(int index) => Text(index) ?? throw new InvalidDataException($"Sütun {index} boş.");

        public int Int(int index) => int.Parse(RequiredText(index), CultureInfo.InvariantCulture);

        public int? NullableInt(int index) => Text(index) is { } v ? int.Parse(v, CultureInfo.InvariantCulture) : null;

        /// <summary>Kaynakta <c>real</c> olan değerleri (ör. 9.80000019) belirtilen hassasiyete yuvarlar.</summary>
        public decimal Decimal(int index, int decimals = 2) =>
            Math.Round(decimal.Parse(RequiredText(index), NumberStyles.Float, CultureInfo.InvariantCulture), decimals, MidpointRounding.AwayFromZero);

        public DateOnly? Date(int index) =>
            Text(index) is { } v ? DateOnly.ParseExact(v, "yyyy-MM-dd", CultureInfo.InvariantCulture) : null;
    }
}
