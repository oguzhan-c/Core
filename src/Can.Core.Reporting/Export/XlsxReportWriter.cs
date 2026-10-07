using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace Can.Core.Reporting.Export;

/// <summary>
/// Excel (.xlsx) yazıcı. XLSX, belirli adlarda XML dosyaları içeren bir ZIP'tir (Office Open XML / ECMA-376): içerik
/// tipleri, ilişkiler, çalışma kitabı, stiller ve sayfa. Sayılar sayı olarak yazılır (Excel'de hesaplanabilir), biçimleri
/// .NET biçim dizesinden Excel biçim koduna çevrilir; başlıklar birleştirilir, başlık satırları ve satır başlıkları dondurulur.
/// </summary>
internal static class XlsxReportWriter
{
    private const string Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string Relationships = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    private enum Font
    {
        Normal,
        Bold,
        Title,
    }

    private enum Fill
    {
        None = 0,
        Gray125 = 1,
        Header = 2,
        Group = 3,
        Total = 4,
    }

    public static byte[] Write(ReportGrid grid, ReportResult result, CultureInfo culture, ReportExportOptions options)
    {
        var styles = new StyleTable(culture);
        string title = options.Title ?? result.Title ?? "Rapor";
        int offset = 0; // tablo hangi satırdan başlıyor (0 tabanlı)
        var preamble = new List<(string Text, int Style)>();
        preamble.Add((title, styles.Get(Font.Title, Fill.None, border: false, numberFormat: 0)));
        if (!string.IsNullOrWhiteSpace(options.Subtitle))
            preamble.Add((options.Subtitle!, styles.Get(Font.Normal, Fill.None, false, 0)));
        offset = preamble.Count + 1; // bir satır boşluk

        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            string sheet = SheetXml(grid, styles, preamble, offset);
            Add(zip, "[Content_Types].xml", ContentTypes());
            Add(zip, "_rels/.rels", RootRelationships());
            Add(zip, "docProps/core.xml", CoreProperties(title, options.GeneratedAt ?? DateTimeOffset.UtcNow));
            Add(zip, "xl/workbook.xml", Workbook(SheetName(title)));
            Add(zip, "xl/_rels/workbook.xml.rels", WorkbookRelationships());
            Add(zip, "xl/worksheets/sheet1.xml", sheet);
            Add(zip, "xl/styles.xml", styles.ToXml()); // sayfa yazılırken eklenen stiller dahil
        }

        return stream.ToArray();
    }

    // ---------------------------------------------------------------- sayfa

    private static string SheetXml(ReportGrid grid, StyleTable styles, List<(string Text, int Style)> preamble, int offset)
    {
        return Xml(w =>
        {
            w.WriteStartElement("worksheet", Main);
            w.WriteAttributeString("xmlns", "r", null, Relationships);

            // donmuş bölme: başlık satırları + satır başlığı sütunları
            w.WriteStartElement("sheetViews");
            w.WriteStartElement("sheetView");
            w.WriteAttributeString("workbookViewId", "0");
            int frozenRows = offset + grid.HeaderRows;
            int frozenColumns = grid.RowHeaderColumns;
            w.WriteStartElement("pane");
            w.WriteAttributeString("xSplit", frozenColumns.ToString(CultureInfo.InvariantCulture));
            w.WriteAttributeString("ySplit", frozenRows.ToString(CultureInfo.InvariantCulture));
            w.WriteAttributeString("topLeftCell", Reference(frozenRows, frozenColumns));
            w.WriteAttributeString("activePane", "bottomRight");
            w.WriteAttributeString("state", "frozen");
            w.WriteEndElement();
            w.WriteEndElement();
            w.WriteEndElement();

            // sütun genişlikleri (karakter cinsinden, içeriğe göre)
            w.WriteStartElement("cols");
            for (int c = 0; c < grid.ColumnCount; c++)
            {
                int longest = grid.Rows.Max(r => r[c].Text.Length);
                double width = Math.Clamp((longest * 1.15) + 2, 8, 50);
                w.WriteStartElement("col");
                w.WriteAttributeString("min", (c + 1).ToString(CultureInfo.InvariantCulture));
                w.WriteAttributeString("max", (c + 1).ToString(CultureInfo.InvariantCulture));
                w.WriteAttributeString("width", width.ToString("0.##", CultureInfo.InvariantCulture));
                w.WriteAttributeString("customWidth", "1");
                w.WriteEndElement();
            }

            w.WriteEndElement();

            w.WriteStartElement("sheetData");
            for (int i = 0; i < preamble.Count; i++)
            {
                StartRow(w, i);
                InlineString(w, Reference(i, 0), preamble[i].Text, preamble[i].Style);
                w.WriteEndElement();
            }

            for (int r = 0; r < grid.Rows.Count; r++)
            {
                int rowIndex = offset + r;
                StartRow(w, rowIndex);
                GridCell[] row = grid.Rows[r];
                for (int c = 0; c < row.Length; c++)
                {
                    GridCell cell = row[c];
                    string reference = Reference(rowIndex, c);
                    switch (cell.Value)
                    {
                        case decimal number:
                            Number(w, reference, number.ToString(CultureInfo.InvariantCulture), styles.For(cell, number));
                            break;
                        case DateTime date:
                            Number(w, reference, date.ToOADate().ToString(CultureInfo.InvariantCulture), styles.For(cell, date));
                            break;
                        default:
                            if (cell.Text.Length > 0)
                                InlineString(w, reference, cell.Text, styles.For(cell, null));
                            else
                                Blank(w, reference, styles.For(cell, null));
                            break;
                    }
                }

                w.WriteEndElement();
            }

            w.WriteEndElement(); // sheetData

            if (grid.Merges.Count > 0)
            {
                w.WriteStartElement("mergeCells");
                w.WriteAttributeString("count", grid.Merges.Count.ToString(CultureInfo.InvariantCulture));
                foreach (GridMerge merge in grid.Merges)
                {
                    w.WriteStartElement("mergeCell");
                    w.WriteAttributeString("ref", $"{Reference(offset + merge.Row, merge.FirstColumn)}:{Reference(offset + merge.Row, merge.LastColumn)}");
                    w.WriteEndElement();
                }

                w.WriteEndElement();
            }

            w.WriteEndElement(); // worksheet
        });
    }

    private static void StartRow(XmlWriter w, int index)
    {
        w.WriteStartElement("row");
        w.WriteAttributeString("r", (index + 1).ToString(CultureInfo.InvariantCulture));
    }

    private static void Number(XmlWriter w, string reference, string value, int style)
    {
        w.WriteStartElement("c");
        w.WriteAttributeString("r", reference);
        w.WriteAttributeString("s", style.ToString(CultureInfo.InvariantCulture));
        w.WriteElementString("v", value);
        w.WriteEndElement();
    }

    private static void InlineString(XmlWriter w, string reference, string text, int style)
    {
        w.WriteStartElement("c");
        w.WriteAttributeString("r", reference);
        w.WriteAttributeString("s", style.ToString(CultureInfo.InvariantCulture));
        w.WriteAttributeString("t", "inlineStr");
        w.WriteStartElement("is");
        w.WriteStartElement("t");
        w.WriteAttributeString("xml", "space", null, "preserve");
        w.WriteString(text);
        w.WriteEndElement();
        w.WriteEndElement();
        w.WriteEndElement();
    }

    private static void Blank(XmlWriter w, string reference, int style)
    {
        if (style == 0)
            return;
        w.WriteStartElement("c");
        w.WriteAttributeString("r", reference);
        w.WriteAttributeString("s", style.ToString(CultureInfo.InvariantCulture));
        w.WriteEndElement();
    }

    /// <summary>0 tabanlı satır/sütun → <c>A1</c>.</summary>
    internal static string Reference(int row, int column) => ColumnName(column) + (row + 1).ToString(CultureInfo.InvariantCulture);

    internal static string ColumnName(int column)
    {
        var name = new StringBuilder();
        for (int n = column + 1; n > 0; n = (n - 1) / 26)
            name.Insert(0, (char)('A' + ((n - 1) % 26)));
        return name.ToString();
    }

    private static string SheetName(string title)
    {
        string cleaned = new(title.Where(c => !"[]:*?/\\".Contains(c)).ToArray());
        cleaned = cleaned.Trim().Trim('\'');
        return cleaned.Length == 0 ? "Rapor" : cleaned.Length > 31 ? cleaned[..31] : cleaned;
    }

    // ---------------------------------------------------------------- stiller

    /// <summary>Hücre stilleri: (yazı, dolgu, kenarlık, sayı biçimi) birleşimi başına bir <c>xf</c>.</summary>
    private sealed class StyleTable(CultureInfo culture)
    {
        private readonly List<(Font Font, Fill Fill, bool Border, int NumberFormat)> _xfs = [(Font.Normal, Fill.None, false, 0)];
        private readonly Dictionary<string, int> _numberFormats = new(StringComparer.Ordinal);

        public int Get(Font font, Fill fill, bool border, int numberFormat)
        {
            int index = _xfs.IndexOf((font, fill, border, numberFormat));
            if (index >= 0)
                return index;
            _xfs.Add((font, fill, border, numberFormat));
            return _xfs.Count - 1;
        }

        public int For(GridCell cell, object? value)
        {
            (Font font, Fill fill) = cell.Style switch
            {
                GridStyle.Header => (Font.Bold, Fill.Header),
                GridStyle.Group => (Font.Bold, Fill.Group),
                GridStyle.Total => (Font.Bold, Fill.Total),
                _ => (Font.Normal, Fill.None),
            };

            int numberFormat = value switch
            {
                decimal d => NumberFormat(ExcelNumberFormat(cell.Format, d)),
                DateTime t => NumberFormat(ExcelDateFormat(cell.Format, t)),
                _ => 0,
            };
            return Get(font, fill, border: true, numberFormat);
        }

        private int NumberFormat(string code)
        {
            if (!_numberFormats.TryGetValue(code, out int id))
            {
                id = 164 + _numberFormats.Count; // 0-163 Excel'in yerleşik biçimleri
                _numberFormats[code] = id;
            }

            return id;
        }

        /// <summary>.NET biçim dizesi → Excel biçim kodu (kodlarda ayırıcılar her zaman <c>,</c> binlik ve <c>.</c> ondalık).</summary>
        private string ExcelNumberFormat(string? format, decimal value)
        {
            if (string.IsNullOrEmpty(format))
                return value % 1 == 0 ? "#,##0" : "#,##0.00";

            char kind = char.ToUpperInvariant(format[0]);
            int digits = format.Length > 1 && int.TryParse(format.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : -1;
            string Decimals(int fallback) => (digits < 0 ? fallback : digits) is var d and > 0 ? "." + new string('0', d) : string.Empty;

            switch (kind)
            {
                case 'N' when digits >= -1 && format.Length <= 3:
                    return "#,##0" + Decimals(2);
                case 'F' when format.Length <= 3:
                    return "0" + Decimals(2);
                case 'P' when format.Length <= 3:
                    return "0" + Decimals(2) + "%";
                case 'C' when format.Length <= 3:
                    string number = "#,##0" + Decimals(culture.NumberFormat.CurrencyDecimalDigits);
                    string symbol = $"\"{culture.NumberFormat.CurrencySymbol}\"";
                    return culture.NumberFormat.CurrencyPositivePattern switch
                    {
                        0 => symbol + number,
                        1 => number + symbol,
                        2 => symbol + " " + number,
                        _ => number + " " + symbol,
                    };
                default:
                    return format.Replace("#,0", "#,##0", StringComparison.Ordinal); // özel biçim (çoğu Excel'de de geçerli)
            }
        }

        private string ExcelDateFormat(string? format, DateTime value)
        {
            string pattern = string.IsNullOrEmpty(format) || format.Length == 1
                ? (value.TimeOfDay == TimeSpan.Zero ? culture.DateTimeFormat.ShortDatePattern : culture.DateTimeFormat.ShortDatePattern + " " + culture.DateTimeFormat.ShortTimePattern)
                : format;
            // .NET: M ay, m dakika, H saat → Excel: m ay (bağlama göre), h saat
            return pattern.Replace("M", "m", StringComparison.Ordinal).Replace("H", "h", StringComparison.Ordinal);
        }

        public string ToXml() => Xml(w =>
        {
            w.WriteStartElement("styleSheet", Main);

            if (_numberFormats.Count > 0)
            {
                w.WriteStartElement("numFmts");
                w.WriteAttributeString("count", _numberFormats.Count.ToString(CultureInfo.InvariantCulture));
                foreach ((string code, int id) in _numberFormats)
                {
                    w.WriteStartElement("numFmt");
                    w.WriteAttributeString("numFmtId", id.ToString(CultureInfo.InvariantCulture));
                    w.WriteAttributeString("formatCode", code);
                    w.WriteEndElement();
                }

                w.WriteEndElement();
            }

            w.WriteStartElement("fonts");
            w.WriteAttributeString("count", "3");
            WriteFont(w, bold: false, size: 11);
            WriteFont(w, bold: true, size: 11);
            WriteFont(w, bold: true, size: 14);
            w.WriteEndElement();

            w.WriteStartElement("fills");
            w.WriteAttributeString("count", "5");
            WritePatternFill(w, "none", null);
            WritePatternFill(w, "gray125", null);
            WritePatternFill(w, "solid", "FFD9E1F2"); // başlık
            WritePatternFill(w, "solid", "FFF2F2F2"); // grup
            WritePatternFill(w, "solid", "FFDDEBF7"); // genel toplam
            w.WriteEndElement();

            w.WriteStartElement("borders");
            w.WriteAttributeString("count", "2");
            w.WriteStartElement("border");
            foreach (string side in new[] { "left", "right", "top", "bottom", "diagonal" })
                w.WriteElementString(side, Main, string.Empty);
            w.WriteEndElement();
            w.WriteStartElement("border");
            foreach (string side in new[] { "left", "right", "top", "bottom" })
            {
                w.WriteStartElement(side);
                w.WriteAttributeString("style", "thin");
                w.WriteStartElement("color");
                w.WriteAttributeString("rgb", "FFBFBFBF");
                w.WriteEndElement();
                w.WriteEndElement();
            }

            w.WriteElementString("diagonal", Main, string.Empty);
            w.WriteEndElement();
            w.WriteEndElement();

            w.WriteStartElement("cellStyleXfs");
            w.WriteAttributeString("count", "1");
            w.WriteStartElement("xf");
            w.WriteAttributeString("numFmtId", "0");
            w.WriteAttributeString("fontId", "0");
            w.WriteAttributeString("fillId", "0");
            w.WriteAttributeString("borderId", "0");
            w.WriteEndElement();
            w.WriteEndElement();

            w.WriteStartElement("cellXfs");
            w.WriteAttributeString("count", _xfs.Count.ToString(CultureInfo.InvariantCulture));
            foreach ((Font font, Fill fill, bool border, int numberFormat) in _xfs)
            {
                w.WriteStartElement("xf");
                w.WriteAttributeString("numFmtId", numberFormat.ToString(CultureInfo.InvariantCulture));
                w.WriteAttributeString("fontId", ((int)font).ToString(CultureInfo.InvariantCulture));
                w.WriteAttributeString("fillId", ((int)fill).ToString(CultureInfo.InvariantCulture));
                w.WriteAttributeString("borderId", border ? "1" : "0");
                w.WriteAttributeString("xfId", "0");
                if (numberFormat != 0)
                    w.WriteAttributeString("applyNumberFormat", "1");
                if (font != Font.Normal)
                    w.WriteAttributeString("applyFont", "1");
                if (fill != Fill.None)
                    w.WriteAttributeString("applyFill", "1");
                if (border)
                    w.WriteAttributeString("applyBorder", "1");
                w.WriteEndElement();
            }

            w.WriteEndElement();

            w.WriteStartElement("cellStyles");
            w.WriteAttributeString("count", "1");
            w.WriteStartElement("cellStyle");
            w.WriteAttributeString("name", "Normal");
            w.WriteAttributeString("xfId", "0");
            w.WriteAttributeString("builtinId", "0");
            w.WriteEndElement();
            w.WriteEndElement();

            w.WriteEndElement();
        });

        private static void WriteFont(XmlWriter w, bool bold, int size)
        {
            w.WriteStartElement("font");
            if (bold)
                w.WriteElementString("b", Main, string.Empty);
            w.WriteStartElement("sz");
            w.WriteAttributeString("val", size.ToString(CultureInfo.InvariantCulture));
            w.WriteEndElement();
            w.WriteStartElement("name");
            w.WriteAttributeString("val", "Calibri");
            w.WriteEndElement();
            w.WriteStartElement("family");
            w.WriteAttributeString("val", "2");
            w.WriteEndElement();
            w.WriteEndElement();
        }

        private static void WritePatternFill(XmlWriter w, string pattern, string? color)
        {
            w.WriteStartElement("fill");
            w.WriteStartElement("patternFill");
            w.WriteAttributeString("patternType", pattern);
            if (color is not null)
            {
                w.WriteStartElement("fgColor");
                w.WriteAttributeString("rgb", color);
                w.WriteEndElement();
                w.WriteStartElement("bgColor");
                w.WriteAttributeString("indexed", "64");
                w.WriteEndElement();
            }

            w.WriteEndElement();
            w.WriteEndElement();
        }
    }

    // ---------------------------------------------------------------- paket parçaları

    private static string ContentTypes() => """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/><Override PartName="/docProps/core.xml" ContentType="application/vnd.openxmlformats-package.core-properties+xml"/></Types>
        """;

    private static string RootRelationships() => """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties" Target="docProps/core.xml"/></Relationships>
        """;

    private static string WorkbookRelationships() => """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/></Relationships>
        """;

    private static string Workbook(string sheetName) => Xml(w =>
    {
        w.WriteStartElement("workbook", Main);
        w.WriteAttributeString("xmlns", "r", null, Relationships);
        w.WriteStartElement("sheets");
        w.WriteStartElement("sheet");
        w.WriteAttributeString("name", sheetName);
        w.WriteAttributeString("sheetId", "1");
        w.WriteAttributeString("id", Relationships, "rId1");
        w.WriteEndElement();
        w.WriteEndElement();
        w.WriteEndElement();
    });

    private static string CoreProperties(string title, DateTimeOffset created) => Xml(w =>
    {
        const string cp = "http://schemas.openxmlformats.org/package/2006/metadata/core-properties";
        const string dc = "http://purl.org/dc/elements/1.1/";
        const string dcterms = "http://purl.org/dc/terms/";
        const string xsi = "http://www.w3.org/2001/XMLSchema-instance";
        w.WriteStartElement("cp", "coreProperties", cp);
        w.WriteAttributeString("xmlns", "dc", null, dc);
        w.WriteAttributeString("xmlns", "dcterms", null, dcterms);
        w.WriteAttributeString("xmlns", "xsi", null, xsi);
        w.WriteElementString("title", dc, title);
        w.WriteElementString("creator", dc, "Can.Core.Reporting");
        w.WriteStartElement("created", dcterms);
        w.WriteAttributeString("type", xsi, "dcterms:W3CDTF");
        w.WriteString(created.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
        w.WriteEndElement();
        w.WriteEndElement();
    });

    private static string Xml(Action<XmlWriter> write)
    {
        var builder = new StringBuilder();
        using (var writer = XmlWriter.Create(builder, new XmlWriterSettings { OmitXmlDeclaration = false, Indent = false }))
        {
            writer.WriteStartDocument(standalone: true);
            write(writer);
            writer.WriteEndDocument();
        }

        // StringBuilder'a yazınca bildirim utf-16 olur; dosya UTF-8 yazılıyor
        return builder.ToString().Replace("encoding=\"utf-16\"", "encoding=\"UTF-8\"", StringComparison.Ordinal);
    }

    private static void Add(ZipArchive zip, string path, string content)
    {
        ZipArchiveEntry entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        using Stream stream = entry.Open();
        byte[] bytes = new UTF8Encoding(false).GetBytes(content.TrimStart());
        stream.Write(bytes);
    }
}
