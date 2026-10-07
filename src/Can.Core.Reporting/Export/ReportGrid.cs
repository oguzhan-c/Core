using System.Globalization;

namespace Can.Core.Reporting.Export;

internal enum GridStyle
{
    /// <summary>Sütun/satır başlığı.</summary>
    Header,
    Normal,

    /// <summary>Grup (ara toplam) satırı.</summary>
    Group,

    /// <summary>Genel toplam.</summary>
    Total,
}

/// <summary>Tablodaki bir hücre: görünen metin + (sayı/tarih ise) ham değer ve biçim.</summary>
internal sealed record GridCell(string Text, object? Value, string? Format, GridStyle Style, bool Numeric)
{
    public static GridCell Empty(GridStyle style) => new(string.Empty, null, null, style, false);
}

/// <summary>Yatay birleştirme: başlık satırında aynı üst gruba ait sütunlar.</summary>
internal sealed record GridMerge(int Row, int FirstColumn, int LastColumn);

/// <summary>
/// Rapor sonucunu iki boyutlu tabloya çevirir (CSV, Excel ve PDF ortak kullanır):
/// <code>
///            | 2024           | 2025           | Genel toplam
/// Kategori   | Ciro  | Pay    | Ciro  | Pay    | Ciro  | Pay
/// İçecek     | 186   | %75,6  | ...
/// </code>
/// Solda satır grubu başına bir sütun (her satırın etiketi kendi düzeyinin sütununda), üstte sütun grubu başına bir
/// satır ve birden çok değer varsa değer adları satırı.
/// </summary>
internal sealed class ReportGrid
{
    private ReportGrid(int headerRows, int rowHeaderColumns, IReadOnlyList<GridCell[]> rows, IReadOnlyList<GridMerge> merges)
    {
        HeaderRows = headerRows;
        RowHeaderColumns = rowHeaderColumns;
        Rows = rows;
        Merges = merges;
    }

    public int HeaderRows { get; }

    public int RowHeaderColumns { get; }

    public IReadOnlyList<GridCell[]> Rows { get; }

    public IReadOnlyList<GridMerge> Merges { get; }

    public int ColumnCount => Rows.Count == 0 ? 0 : Rows[0].Length;

    public static ReportGrid Build(ReportResult result, CultureInfo culture, string totalLabel = "Toplam")
    {
        int measures = result.Measures.Count;
        int rowHeaderColumns = Math.Max(1, result.RowDimensions.Count);
        int columnLevels = result.ColumnDimensions.Count;
        bool measureRow = measures > 1 || columnLevels == 0;
        int headerRows = columnLevels + (measureRow ? 1 : 0);
        int width = rowHeaderColumns + (result.Columns.Count * measures);

        var rows = new List<GridCell[]>();
        var merges = new List<GridMerge>();

        // ------------------------------------------------------------ başlık satırları
        for (int level = 1; level <= columnLevels; level++)
        {
            var line = NewLine(width, GridStyle.Header);
            if (level == columnLevels && !measureRow)
                FillRowCaptions(result, line);

            int c = 0;
            while (c < result.Columns.Count)
            {
                string label = ColumnLabel(result.Columns[c], level, totalLabel);
                int end = c;
                // aynı üst gruba ait sütunlar birleşir
                while (end + 1 < result.Columns.Count && SameGroup(result.Columns[c], result.Columns[end + 1], level))
                    end++;

                int first = rowHeaderColumns + (c * measures);
                int last = rowHeaderColumns + ((end + 1) * measures) - 1;
                line[first] = new GridCell(label, null, null, GridStyle.Header, false);
                if (last > first)
                    merges.Add(new GridMerge(rows.Count, first, last));
                c = end + 1;
            }

            rows.Add(line);
        }

        if (measureRow)
        {
            var line = NewLine(width, GridStyle.Header);
            FillRowCaptions(result, line);
            for (int c = 0; c < result.Columns.Count; c++)
            {
                for (int m = 0; m < measures; m++)
                    line[rowHeaderColumns + (c * measures) + m] = new GridCell(result.Measures[m].Caption, null, null, GridStyle.Header, false);
            }

            rows.Add(line);
        }

        // ------------------------------------------------------------ gövde
        for (int r = 0; r < result.Rows.Count; r++)
        {
            ReportHeader header = result.Rows[r];
            GridStyle style = header.Kind switch
            {
                ReportHeaderKind.GrandTotal => GridStyle.Total,
                ReportHeaderKind.Group => GridStyle.Group,
                _ => GridStyle.Normal,
            };

            var line = NewLine(width, style);
            int labelColumn = header.Kind == ReportHeaderKind.GrandTotal ? 0 : Math.Clamp(header.Level - 1, 0, rowHeaderColumns - 1);
            line[labelColumn] = new GridCell(header.Label, null, null, style, false);

            for (int c = 0; c < result.Columns.Count; c++)
            {
                for (int m = 0; m < measures; m++)
                {
                    object? value = result.Value(r, c, m);
                    string? format = result.Measures[m].Format;
                    line[rowHeaderColumns + (c * measures) + m] = new GridCell(Text(value, format, culture), value, format, style, value is decimal or DateTime);
                }
            }

            rows.Add(line);
        }

        return new ReportGrid(headerRows, rowHeaderColumns, rows, merges);
    }

    /// <summary>Değerin görünen metni (biçim .NET biçim dizesi; yoksa sayılar en çok 2 ondalık).</summary>
    public static string Text(object? value, string? format, CultureInfo culture) =>
        value switch
        {
            null => string.Empty,
            decimal d => d.ToString(string.IsNullOrEmpty(format) ? "#,0.##" : format, culture),
            DateTime t => t.TimeOfDay == TimeSpan.Zero ? t.ToString(string.IsNullOrEmpty(format) ? "d" : format, culture) : t.ToString(format ?? "g", culture),
            bool b => b ? "✓" : string.Empty,
            IFormattable f => f.ToString(format, culture),
            _ => value.ToString() ?? string.Empty,
        };

    private static GridCell[] NewLine(int width, GridStyle style)
    {
        var line = new GridCell[width];
        for (int i = 0; i < width; i++)
            line[i] = GridCell.Empty(style);
        return line;
    }

    private static void FillRowCaptions(ReportResult result, GridCell[] line)
    {
        for (int i = 0; i < result.RowDimensions.Count; i++)
            line[i] = new GridCell(result.RowDimensions[i].Caption, null, null, GridStyle.Header, false);
    }

    private static string ColumnLabel(ReportHeader column, int level, string totalLabel)
    {
        if (column.Kind == ReportHeaderKind.GrandTotal)
            return level == 1 ? column.Label : string.Empty;
        if (level <= column.Labels.Count)
            return column.Labels[level - 1];
        return level == column.Labels.Count + 1 ? totalLabel : string.Empty; // ara toplam sütunu
    }

    private static bool SameGroup(ReportHeader a, ReportHeader b, int level)
    {
        if (a.Kind == ReportHeaderKind.GrandTotal || b.Kind == ReportHeaderKind.GrandTotal)
            return false;
        if (a.Labels.Count < level || b.Labels.Count < level)
            return false;
        if (level == a.Labels.Count && level == b.Labels.Count)
            return false; // en alt düzey: her sütun kendi başlığı
        for (int i = 0; i < level; i++)
        {
            if (!Equals(a.Keys.ElementAtOrDefault(i), b.Keys.ElementAtOrDefault(i)) || a.Labels[i] != b.Labels[i])
                return false;
        }

        return true;
    }
}
