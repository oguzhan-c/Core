using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace Can.Core.Reporting.Export;

/// <summary>
/// PDF yazıcı (PDF 1.7, dış paket yok). Tablo sayfaya sığdırılır: önce yazı küçültülür (en az 6 pt), yine sığmazsa
/// değer sütunları sayfalara bölünür ve satır başlıkları her sayfada tekrarlanır. Satırlar sayfalara bölünürken sütun
/// başlıkları her sayfada tekrar çizilir; altta sayfa numarası.
/// </summary>
internal static class PdfReportWriter
{
    private const double Margin = 36;
    private const double Padding = 3;
    private const double MaxColumnWidth = 220;
    private const double MinFontSize = 6;

    private sealed record Chunk(int[] Columns);

    public static byte[] Write(ReportGrid grid, ReportResult result, CultureInfo culture, ReportExportOptions options)
    {
        (double pageWidth, double pageHeight) = options.PdfPageSize switch
        {
            PdfPageSize.A4Portrait => (595.28, 841.89),
            PdfPageSize.A3Landscape => (1190.55, 841.89),
            _ => (841.89, 595.28),
        };

        string title = PdfFont.Prepare(options.Title ?? result.Title ?? "Rapor");
        string? subtitle = string.IsNullOrWhiteSpace(options.Subtitle) ? null : PdfFont.Prepare(options.Subtitle!);
        DateTimeOffset generated = options.GeneratedAt ?? DateTimeOffset.Now;
        string stamp = generated.ToString("g", culture);

        double available = pageWidth - (2 * Margin);
        double size = options.PdfFontSize;
        double[] widths = ColumnWidths(grid, size);
        while (Sum(widths) > available && size > MinFontSize)
        {
            size = Math.Max(MinFontSize, size - 0.5);
            widths = ColumnWidths(grid, size);
        }

        List<Chunk> chunks = Chunks(grid, widths, available);
        double rowHeight = size * 1.7;
        double top = pageHeight - Margin;
        double headerBlock = 18 + (subtitle is null ? 0 : 12) + 8; // başlık + alt başlık + boşluk
        double bodyTop = top - headerBlock - (grid.HeaderRows * rowHeight);
        double bottom = Margin + 14; // sayfa altı
        int rowsPerPage = Math.Max(1, (int)Math.Floor((bodyTop - bottom) / rowHeight));
        int bodyRows = grid.Rows.Count - grid.HeaderRows;
        int verticalPages = Math.Max(1, (int)Math.Ceiling(bodyRows / (double)rowsPerPage));
        int totalPages = verticalPages * chunks.Count;

        var pages = new List<string>();
        foreach (Chunk chunk in chunks)
        {
            for (int v = 0; v < verticalPages; v++)
            {
                var page = new PageBuilder();
                double y = top;

                page.Text(Margin, y - 14, title, bold: true, 13);
                page.TextRight(pageWidth - Margin, y - 14, stamp, bold: false, 8);
                y -= 18;
                if (subtitle is not null)
                {
                    page.Text(Margin, y - 10, subtitle, bold: false, 9);
                    y -= 12;
                }

                y -= 8;

                // sütun başlıkları
                for (int h = 0; h < grid.HeaderRows; h++)
                {
                    DrawRow(page, grid, h, chunk, widths, y, rowHeight, size);
                    y -= rowHeight;
                }

                int first = grid.HeaderRows + (v * rowsPerPage);
                int last = Math.Min(grid.Rows.Count, first + rowsPerPage);
                for (int r = first; r < last; r++)
                {
                    DrawRow(page, grid, r, chunk, widths, y, rowHeight, size);
                    y -= rowHeight;
                }

                int number = pages.Count + 1;
                page.TextRight(pageWidth - Margin, Margin, string.Format(culture, options.PageLabel, number, totalPages), bold: false, 7);
                pages.Add(page.ToString());
            }
        }

        return Assemble(pages, pageWidth, pageHeight, title, generated);
    }

    // ---------------------------------------------------------------- yerleşim

    private static double[] ColumnWidths(ReportGrid grid, double size)
    {
        var widths = new double[grid.ColumnCount];
        var merged = new HashSet<(int Row, int Column)>();
        foreach (GridMerge merge in grid.Merges)
        {
            for (int c = merge.FirstColumn; c <= merge.LastColumn; c++)
                merged.Add((merge.Row, c));
        }

        for (int r = 0; r < grid.Rows.Count; r++)
        {
            for (int c = 0; c < grid.ColumnCount; c++)
            {
                if (merged.Contains((r, c)))
                    continue; // birleşik başlık genişliği sütunlara yayılır
                GridCell cell = grid.Rows[r][c];
                bool bold = cell.Style != GridStyle.Normal;
                double width = PdfFont.Width(PdfFont.Prepare(cell.Text), bold, size) + (2 * Padding);
                widths[c] = Math.Max(widths[c], Math.Min(width, MaxColumnWidth));
            }
        }

        for (int c = 0; c < widths.Length; c++)
            widths[c] = Math.Max(widths[c], size * 3);
        return widths;
    }

    /// <summary>Değer sütunlarını sayfa genişliğine göre gruplara böler; satır başlığı sütunları her grupta.</summary>
    private static List<Chunk> Chunks(ReportGrid grid, double[] widths, double available)
    {
        int[] headerColumns = Enumerable.Range(0, grid.RowHeaderColumns).ToArray();
        double headerWidth = headerColumns.Sum(c => widths[c]);
        var chunks = new List<Chunk>();
        var current = new List<int>(headerColumns);
        double used = headerWidth;

        for (int c = grid.RowHeaderColumns; c < grid.ColumnCount; c++)
        {
            if (used + widths[c] > available && current.Count > headerColumns.Length)
            {
                chunks.Add(new Chunk(current.ToArray()));
                current = new List<int>(headerColumns);
                used = headerWidth;
            }

            current.Add(c);
            used += widths[c];
        }

        chunks.Add(new Chunk(current.ToArray()));
        return chunks;
    }

    private static double Sum(double[] values) => values.Sum();

    private static void DrawRow(PageBuilder page, ReportGrid grid, int r, Chunk chunk, double[] widths, double y, double height, double size)
    {
        GridCell[] row = grid.Rows[r];
        GridStyle style = r < grid.HeaderRows ? GridStyle.Header : row.Length > 0 ? row[^1].Style : GridStyle.Normal;
        double x = Margin;
        double rowWidth = chunk.Columns.Sum(c => widths[c]);

        (double R, double G, double B)? fill = style switch
        {
            GridStyle.Header => (0.85, 0.88, 0.95),
            GridStyle.Group => (0.95, 0.95, 0.95),
            GridStyle.Total => (0.87, 0.92, 0.97),
            _ => null,
        };
        if (fill is { } f)
            page.Fill(x, y - height, rowWidth, height, f.R, f.G, f.B);

        double baseline = y - (height * 0.68);
        int i = 0;
        while (i < chunk.Columns.Length)
        {
            int column = chunk.Columns[i];
            double width = widths[column];

            // birleşik başlık: bu parçadaki bitişik sütunlara yayılır
            GridMerge? merge = r < grid.HeaderRows ? grid.Merges.FirstOrDefault(m => m.Row == r && column >= m.FirstColumn && column <= m.LastColumn) : null;
            int span = 1;
            if (merge is not null)
            {
                while (i + span < chunk.Columns.Length && chunk.Columns[i + span] <= merge.LastColumn && chunk.Columns[i + span] == chunk.Columns[i + span - 1] + 1)
                {
                    width += widths[chunk.Columns[i + span]];
                    span++;
                }
            }

            GridCell cell = merge is not null ? row[merge.FirstColumn] : row[column];
            bool drawLabel = merge is null || column == merge.FirstColumn || i == 0 || chunk.Columns[i - 1] < merge.FirstColumn || chunk.Columns[i - 1] != column - 1;
            string text = drawLabel ? Fit(PdfFont.Prepare(cell.Text), cell.Style != GridStyle.Normal, size, width - (2 * Padding)) : string.Empty;
            bool bold = cell.Style != GridStyle.Normal;

            if (text.Length > 0)
            {
                if (cell.Numeric)
                    page.TextRight(x + width - Padding, baseline, text, bold, size);
                else if (merge is not null)
                    page.TextCenter(x + (width / 2), baseline, text, bold, size);
                else
                    page.Text(x + Padding, baseline, text, bold, size);
            }

            page.Line(x + width, y, x + width, y - height, 0.8, 0.25); // dikey ayırıcı
            x += width;
            i += span;
        }

        page.Line(Margin, y - height, Margin + rowWidth, y - height, style == GridStyle.Total ? 0.3 : 0.75, style == GridStyle.Total ? 0.8 : 0.3);
        page.Line(Margin, y, Margin, y - height, 0.8, 0.25);
        if (r == 0)
            page.Line(Margin, y, Margin + rowWidth, y, 0.6, 0.5);
    }

    /// <summary>Sığmayan metin üç noktayla kısaltılır.</summary>
    private static string Fit(string text, bool bold, double size, double width)
    {
        if (PdfFont.Width(text, bold, size) <= width)
            return text;
        for (int length = text.Length - 1; length > 0; length--)
        {
            string candidate = text[..length].TrimEnd() + "…";
            if (PdfFont.Width(candidate, bold, size) <= width)
                return candidate;
        }

        return string.Empty;
    }

    // ---------------------------------------------------------------- içerik akışı

    private sealed class PageBuilder
    {
        private readonly StringBuilder _content = new();

        public void Text(double x, double y, string text, bool bold, double size)
        {
            _content.Append("BT /").Append(bold ? "F2 " : "F1 ").Append(N(size)).Append(" Tf 0 g ")
                .Append(N(x)).Append(' ').Append(N(y)).Append(" Td ").Append(PdfString(text)).Append(" Tj ET\n");
        }

        public void TextRight(double right, double y, string text, bool bold, double size) => Text(right - PdfFont.Width(text, bold, size), y, text, bold, size);

        public void TextCenter(double center, double y, string text, bool bold, double size) => Text(center - (PdfFont.Width(text, bold, size) / 2), y, text, bold, size);

        public void Fill(double x, double y, double width, double height, double r, double g, double b)
        {
            _content.Append(N(r)).Append(' ').Append(N(g)).Append(' ').Append(N(b)).Append(" rg ")
                .Append(N(x)).Append(' ').Append(N(y)).Append(' ').Append(N(width)).Append(' ').Append(N(height)).Append(" re f\n");
        }

        public void Line(double x1, double y1, double x2, double y2, double gray, double width)
        {
            _content.Append(N(gray)).Append(" G ").Append(N(width)).Append(" w ")
                .Append(N(x1)).Append(' ').Append(N(y1)).Append(" m ").Append(N(x2)).Append(' ').Append(N(y2)).Append(" l S\n");
        }

        public override string ToString() => _content.ToString();

        private static string N(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

        /// <summary>PDF metin dizesi: ( ) \ kaçışlı, ASCII dışı baytlar sekizli (\ooo).</summary>
        private static string PdfString(string text)
        {
            var builder = new StringBuilder("(");
            foreach (byte b in PdfFont.Encode(text))
            {
                if (b is (byte)'(' or (byte)')' or (byte)'\\')
                    builder.Append('\\').Append((char)b);
                else if (b is < 32 or > 126)
                    builder.Append('\\').Append(Convert.ToString(b, 8).PadLeft(3, '0'));
                else
                    builder.Append((char)b);
            }

            return builder.Append(')').ToString();
        }
    }

    // ---------------------------------------------------------------- dosya

    /// <summary>Nesneler, çapraz başvuru tablosu (bayt konumları) ve trailer.</summary>
    private static byte[] Assemble(List<string> pages, double width, double height, string title, DateTimeOffset created)
    {
        using var output = new MemoryStream();
        var offsets = new List<long> { 0 }; // nesne 0 kullanılmaz

        void Write(string text)
        {
            byte[] bytes = Encoding.Latin1.GetBytes(text);
            output.Write(bytes);
        }

        void Object(int number, string body)
        {
            while (offsets.Count <= number)
                offsets.Add(0);
            offsets[number] = output.Position;
            Write($"{number} 0 obj\n{body}\nendobj\n");
        }

        void Stream(int number, byte[] data)
        {
            while (offsets.Count <= number)
                offsets.Add(0);
            offsets[number] = output.Position;
            Write($"{number} 0 obj\n<< /Length {data.Length} /Filter /FlateDecode >>\nstream\n");
            output.Write(data);
            Write("\nendstream\nendobj\n");
        }

        Write("%PDF-1.7\n%âãÏÓ\n"); // ikili dosya işareti

        const int catalog = 1, pagesObject = 2, regular = 3, bold = 4, info = 5;
        int firstPage = 6; // her sayfa: sayfa + içerik
        string kids = string.Join(' ', Enumerable.Range(0, pages.Count).Select(i => $"{firstPage + (i * 2)} 0 R"));

        Object(catalog, $"<< /Type /Catalog /Pages {pagesObject} 0 R >>");
        Object(pagesObject, $"<< /Type /Pages /Kids [{kids}] /Count {pages.Count} >>");
        Object(regular, $"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding << /Type /Encoding /BaseEncoding /WinAnsiEncoding /Differences {PdfFont.Differences} >> >>");
        Object(bold, $"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding << /Type /Encoding /BaseEncoding /WinAnsiEncoding /Differences {PdfFont.Differences} >> >>");
        Object(info, $"<< /Title {TextString(title)} /Producer (Can.Core.Reporting) /CreationDate (D:{created.UtcDateTime:yyyyMMddHHmmss}Z) >>");

        string mediaBox = $"[0 0 {width.ToString("0.##", CultureInfo.InvariantCulture)} {height.ToString("0.##", CultureInfo.InvariantCulture)}]";
        for (int i = 0; i < pages.Count; i++)
        {
            int page = firstPage + (i * 2);
            Object(page, $"<< /Type /Page /Parent {pagesObject} 0 R /MediaBox {mediaBox} /Resources << /Font << /F1 {regular} 0 R /F2 {bold} 0 R >> >> /Contents {page + 1} 0 R >>");
            Stream(page + 1, Compress(Encoding.Latin1.GetBytes(pages[i])));
        }

        long xref = output.Position;
        var table = new StringBuilder();
        table.Append(CultureInfo.InvariantCulture, $"xref\n0 {offsets.Count}\n");
        table.Append("0000000000 65535 f \n");
        for (int i = 1; i < offsets.Count; i++)
            table.Append(offsets[i].ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        table.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {offsets.Count} /Root {catalog} 0 R /Info {info} 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        Write(table.ToString());

        return output.ToArray();
    }

    private static byte[] Compress(byte[] data)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(data);
        return output.ToArray();
    }

    /// <summary>Belge bilgisi metni: UTF-16BE (Türkçe başlık için).</summary>
    private static string TextString(string text)
    {
        var hex = new StringBuilder("<FEFF");
        foreach (byte b in Encoding.BigEndianUnicode.GetBytes(text))
            hex.Append(b.ToString("X2", CultureInfo.InvariantCulture));
        return hex.Append('>').ToString();
    }
}
