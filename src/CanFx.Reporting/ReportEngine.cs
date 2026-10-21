using System.Globalization;

namespace CanFx.Reporting;

public sealed class ReportEngineOptions
{
    /// <summary>Ay/gün adları, sayı biçimleri ve metin sıralaması için kültür.</summary>
    public CultureInfo Culture { get; set; } = CultureInfo.CurrentCulture;

    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    /// <summary>Sonuçtaki hücre sayısı sınırı (satır × sütun × değer); aşılırsa tanım reddedilir.</summary>
    public int MaxCells { get; set; } = 2_000_000;

    public string NullLabel { get; set; } = "(Boş)";

    public string OthersLabel { get; set; } = "Diğer";

    public string GrandTotalLabel { get; set; } = "Genel toplam";

    public string TrueLabel { get; set; } = "Evet";

    public string FalseLabel { get; set; } = "Hayır";
}

/// <summary>
/// Bellek içi rapor motoru (DevExpress Pivot Grid'in "in-memory" işleyişine benzer):
/// <list type="number">
/// <item>Hesaplanmış alanlar satır başına hesaplanır, filtreler uygulanır.</item>
/// <item>Her satır için satır ve sütun gruplarının yolu bulunur (her düğüme bir kez kimlik verilir).</item>
/// <item>Satır yolunun her öneki × sütun yolunun her öneki için hücre özetine eklenir: böylece ara ve genel toplamlar
/// TEK geçişte hesaplanır ((R+1)×(C+1) güncelleme / satır). Toplamlar "alt satırların toplamı" değil, ham veriden
/// hesaplanır; ortanca, farklı sayısı gibi toplanamayan fonksiyonlar da her düzeyde doğrudur.</item>
/// <item>Gruplama sonrası filtre (having), sıralama ve Top N (+"Diğer": kalanların özetleri birleştirilir).</item>
/// <item>Gösterim dönüşümleri (yüzde, kümülatif, fark, sıra) son tablodan hesaplanır.</item>
/// </list>
/// </summary>
public sealed partial class ReportEngine
{
    private readonly ReportEngineOptions _options;

    public ReportEngine(ReportEngineOptions? options = null) => _options = options ?? new ReportEngineOptions();

    public ReportEngineOptions Options => _options;

    public ReportResult Run(ReportDefinition definition, ReportDataSet data)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(data);
        return new Execution(definition, data, _options).Run();
    }

    /// <summary>
    /// Veritabanında en ince düzeyde (tüm satır + sütun grupları) özetlenmiş gruplardan rapor kurar; ara ve genel
    /// toplamlar bu parçaların birleştirilmesiyle bulunur. Yalnızca birleştirilebilir fonksiyonlar (Sum, Count, Average,
    /// Min, Max, StdDev, Var).
    /// </summary>
    /// <param name="definition">Tanım.</param>
    /// <param name="schema">Alanlar (satırlar kullanılmaz).</param>
    /// <param name="groups">Gruplar: anahtarlar tanımdaki satır/sütun sırasında, gruplama uygulanmış.</param>
    /// <param name="sourceRows">Kaynakta filtreye uyan satır sayısı (bilgi için).</param>
    internal ReportResult RunAggregated(ReportDefinition definition, ReportDataSet schema, IEnumerable<AggregatedGroup> groups, long sourceRows)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(groups);
        return new Execution(definition, schema, _options).RunAggregated(groups, sourceRows);
    }

    /// <summary>Veritabanında hesaplanabilen fonksiyonlar (gruplar birleştirilebilir).</summary>
    public static bool IsDecomposable(ReportAggregate aggregate) =>
        aggregate is ReportAggregate.Sum or ReportAggregate.Count or ReportAggregate.Average or ReportAggregate.Min or ReportAggregate.Max
            or ReportAggregate.StdDev or ReportAggregate.StdDevP or ReportAggregate.Var or ReportAggregate.VarP;

    // ================================================================ çalıştırma

    private sealed partial class Execution
    {
        private static readonly object NullKey = new();

        private readonly ReportDefinition _definition;
        private readonly ReportDataSet _data;
        private readonly ReportEngineOptions _options;
        private readonly List<ReportField> _fields;
        private readonly Dictionary<string, int> _fieldIndex = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<(int Index, ReportExpression Expression)> _calculated = [];
        private readonly List<Func<object?[], bool>> _filters = [];
        private readonly DimensionPlan[] _rowDims;
        private readonly DimensionPlan[] _colDims;
        private readonly ReportMeasure[] _measures;
        private readonly int[] _measureFields;
        private readonly Axis _rows = new();
        private readonly Axis _columns = new();
        private readonly Dictionary<(int Row, int Column), Accumulator[]> _cells = [];
        private readonly Dictionary<(int Row, int Column), object?[]> _results = [];

        public Execution(ReportDefinition definition, ReportDataSet data, ReportEngineOptions options)
        {
            _definition = definition;
            _data = data;
            _options = options;
            _fields = [.. data.Fields];
            for (int i = 0; i < _fields.Count; i++)
                _fieldIndex[_fields[i].Name] = i;

            foreach (ReportCalculatedField calculated in definition.CalculatedFields)
            {
                if (string.IsNullOrWhiteSpace(calculated.Name) || _fieldIndex.ContainsKey(calculated.Name))
                    throw new ReportDefinitionException($"Hesaplanmış alan adı boş ya da zaten var: '{calculated.Name}'.");

                // Önce derle (yalnızca önceki alanlara başvurabilir), sonra ekle.
                ReportExpression expression = ReportExpression.Parse(calculated.Expression, IndexOf);
                _fieldIndex[calculated.Name] = _fields.Count;
                _calculated.Add((_fields.Count, expression));
                _fields.Add(new ReportField(calculated.Name, ReportDataType.Number) { Caption = calculated.Caption, IsCalculated = true });
            }

            foreach (ReportFilter filter in definition.Filters)
                _filters.Add(CompileFilter(filter));

            if (!string.IsNullOrWhiteSpace(definition.FilterExpression))
            {
                ReportExpression expression = ReportExpression.Parse(definition.FilterExpression, IndexOf);
                _filters.Add(row => ReportValue.IsTrue(expression.Evaluate(row, _options.TimeProvider)));
            }

            _measures = definition.Measures.Count > 0 ? [.. definition.Measures] : [new ReportMeasure(null, ReportAggregate.Count) { Name = "count", Caption = "Adet" }];
            if (_measures.Select(m => m.EffectiveName).Distinct(StringComparer.Ordinal).Count() != _measures.Length)
                throw new ReportDefinitionException("Değer adları benzersiz olmalı (aynı alanı iki kez kullanıyorsan Name ver).");

            _measureFields = _measures.Select(m => m.Field is null ? -1 : RequireField(m.Field)).ToArray();
            foreach (ReportMeasure measure in _measures)
            {
                if (measure.Field is null && measure.Aggregate != ReportAggregate.Count)
                    throw new ReportDefinitionException($"'{measure.Aggregate}' için alan gerekli (yalnızca Count alansız olabilir).");
                if (measure.Aggregate == ReportAggregate.Percentile && measure.Percentile is not (>= 0 and <= 1))
                    throw new ReportDefinitionException("Percentile 0 ile 1 arasında olmalı (ör. 0.9).");
            }

            _rowDims = definition.Rows.Select(d => new DimensionPlan(d, this)).ToArray();
            _colDims = definition.Columns.Select(d => new DimensionPlan(d, this)).ToArray();
        }

        public ReportResult Run()
        {
            long source = 0;
            long matched = 0;
            int rowDepth = _rowDims.Length;
            int colDepth = _colDims.Length;
            var rowKeys = new object?[rowDepth];
            var colKeys = new object?[colDepth];
            var values = new object?[_measures.Length];
            int totalFields = _fields.Count;

            foreach (object?[] source0 in _data.Rows)
            {
                source++;
                object?[] row = source0;
                if (_calculated.Count > 0)
                {
                    row = new object?[totalFields];
                    Array.Copy(source0, row, Math.Min(source0.Length, totalFields));
                    foreach ((int index, ReportExpression expression) in _calculated)
                        row[index] = expression.Evaluate(row, _options.TimeProvider);
                }

                if (!Passes(row))
                    continue;
                matched++;

                for (int d = 0; d < rowDepth; d++)
                    rowKeys[d] = _rowDims[d].KeyOf(row);
                for (int d = 0; d < colDepth; d++)
                    colKeys[d] = _colDims[d].KeyOf(row);
                for (int m = 0; m < _measures.Length; m++)
                    values[m] = _measureFields[m] < 0 ? null : ReportValue.Normalize(row[_measureFields[m]]);

                foreach (Accumulator[] accumulators in Cells(rowKeys, colKeys))
                {
                    for (int m = 0; m < accumulators.Length; m++)
                        accumulators[m].Add(values[m]);
                }
            }

            return Finish(source, matched, "memory");
        }

        public ReportResult RunAggregated(IEnumerable<AggregatedGroup> groups, long sourceRows)
        {
            foreach (ReportMeasure measure in _measures)
            {
                if (!IsDecomposable(measure.Aggregate))
                    throw new ReportDefinitionException($"{measure.Aggregate} veritabanında özetlenemez.");
            }

            long matched = 0;
            foreach (AggregatedGroup group in groups)
            {
                matched += group.Rows;
                foreach (Accumulator[] accumulators in Cells(group.RowKeys, group.ColumnKeys))
                {
                    for (int m = 0; m < accumulators.Length; m++)
                    {
                        MeasurePartial part = group.Measures[m];
                        accumulators[m].AddPartial(group.Rows, part.Count, part.Sum, part.SumOfSquares, part.Min, part.Max);
                    }
                }
            }

            return Finish(Math.Max(sourceRows, matched), matched, "database");
        }

        /// <summary>Satır ve sütun yolunun her öneki için hücre (ara ve genel toplamlar dahil).</summary>
        private IEnumerable<Accumulator[]> Cells(object?[] rowKeys, object?[] colKeys)
        {
            int rowDepth = _rowDims.Length;
            int colDepth = _colDims.Length;
            var rowIds = new int[rowDepth + 1];
            var colIds = new int[colDepth + 1];

            AxisNode node = _rows.Root;
            for (int d = 0; d < rowDepth; d++)
            {
                node = _rows.Child(node, rowKeys[d]);
                rowIds[d + 1] = node.Id;
            }

            node = _columns.Root;
            for (int d = 0; d < colDepth; d++)
            {
                node = _columns.Child(node, colKeys[d]);
                colIds[d + 1] = node.Id;
            }

            for (int i = 0; i <= rowDepth; i++)
            {
                for (int j = 0; j <= colDepth; j++)
                    yield return Cell(rowIds[i], colIds[j]);
            }
        }

        private ReportResult Finish(long source, long matched, string mode)
        {
            int rowDepth = _rowDims.Length;
            int colDepth = _colDims.Length;

            ApplyHaving();
            Arrange(_rows, _rows.Root, _rowDims, isRowAxis: true);
            Arrange(_columns, _columns.Root, _colDims, isRowAxis: false);

            List<AxisNode> rowHeaders = FlattenRows();
            List<AxisNode> columnHeaders = FlattenColumns();

            long cells = (long)rowHeaders.Count * columnHeaders.Count * _measures.Length;
            if (cells > _options.MaxCells)
                throw new ReportDefinitionException($"Rapor çok büyük ({cells:N0} hücre, sınır {_options.MaxCells:N0}): filtreyi daralt, Top N ya da daha kaba aralık kullan.");

            var table = new IReadOnlyList<object?>[rowHeaders.Count];
            for (int r = 0; r < rowHeaders.Count; r++)
            {
                var line = new object?[columnHeaders.Count * _measures.Length];
                for (int c = 0; c < columnHeaders.Count; c++)
                {
                    for (int m = 0; m < _measures.Length; m++)
                        line[(c * _measures.Length) + m] = Display(rowHeaders[r], columnHeaders[c], m);
                }

                table[r] = line;
            }

            return new ReportResult(
                _definition.Title,
                _rowDims.Select(d => d.Describe()).ToArray(),
                _colDims.Select(d => d.Describe()).ToArray(),
                _measures.Select(DescribeMeasure).ToArray(),
                rowHeaders.Select(n => Header(n, rowDepth)).ToArray(),
                columnHeaders.Select(n => Header(n, colDepth)).ToArray(),
                table,
                source,
                matched
            )
            {
                Mode = mode,
            };
        }
    }
}
