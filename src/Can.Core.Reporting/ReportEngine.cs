using System.Globalization;

namespace Can.Core.Reporting;

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
public sealed class ReportEngine
{
    private readonly ReportEngineOptions _options;

    public ReportEngine(ReportEngineOptions? options = null) => _options = options ?? new ReportEngineOptions();

    public ReportResult Run(ReportDefinition definition, ReportDataSet data)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(data);
        return new Execution(definition, data, _options).Run();
    }

    // ================================================================ çalıştırma

    private sealed class Execution
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
            var rowIds = new int[rowDepth + 1];
            var colIds = new int[colDepth + 1];
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

                AxisNode node = _rows.Root;
                for (int d = 0; d < rowDepth; d++)
                {
                    node = _rows.Child(node, _rowDims[d].KeyOf(row));
                    rowIds[d + 1] = node.Id;
                }

                node = _columns.Root;
                for (int d = 0; d < colDepth; d++)
                {
                    node = _columns.Child(node, _colDims[d].KeyOf(row));
                    colIds[d + 1] = node.Id;
                }

                for (int m = 0; m < _measures.Length; m++)
                    values[m] = _measureFields[m] < 0 ? null : ReportValue.Normalize(row[_measureFields[m]]);

                for (int i = 0; i <= rowDepth; i++)
                {
                    for (int j = 0; j <= colDepth; j++)
                    {
                        Accumulator[] accumulators = Cell(rowIds[i], colIds[j]);
                        for (int m = 0; m < accumulators.Length; m++)
                            accumulators[m].Add(values[m]);
                    }
                }
            }

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
            );
        }

        // ------------------------------------------------------------ alanlar ve filtreler

        private int IndexOf(string name) => _fieldIndex.GetValueOrDefault(name, -1);

        private int RequireField(string name) =>
            IndexOf(name) is var index and >= 0 ? index : throw new ReportDefinitionException($"'{name}' alanı yok.");

        private ReportField FieldAt(int index) => _fields[index];

        private bool Passes(object?[] row)
        {
            foreach (Func<object?[], bool> filter in _filters)
            {
                if (!filter(row))
                    return false;
            }

            return true;
        }

        private Func<object?[], bool> CompileFilter(ReportFilter filter)
        {
            int index = RequireField(filter.Field);
            object?[] values = filter.Values?.Select(ReportValue.Normalize).ToArray() ?? [];

            int Required(int count) =>
                values.Length >= count ? count : throw new ReportDefinitionException($"'{filter.Field}' filtresi ({filter.Operator}) {count} değer ister.");

            switch (filter.Operator)
            {
                case FilterOperator.IsNull:
                    return row => ReportValue.Normalize(row[index]) is null;
                case FilterOperator.IsNotNull:
                    return row => ReportValue.Normalize(row[index]) is not null;
                case FilterOperator.In:
                case FilterOperator.NotIn:
                    bool negate = filter.Operator == FilterOperator.NotIn;
                    return row =>
                    {
                        object? value = ReportValue.Normalize(row[index]);
                        bool found = values.Any(v => ReportValue.AreEqual(value, v));
                        return negate ? !found : found;
                    };
                case FilterOperator.Between:
                    Required(2);
                    return row =>
                    {
                        object? value = ReportValue.Normalize(row[index]);
                        return value is not null && ReportValue.Compare(value, values[0]) >= 0 && ReportValue.Compare(value, values[1]) <= 0;
                    };
                case FilterOperator.Contains:
                case FilterOperator.StartsWith:
                    Required(1);
                    string text = values[0]?.ToString() ?? string.Empty;
                    bool contains = filter.Operator == FilterOperator.Contains;
                    return row => row[index] is string s
                        && (contains ? s.Contains(text, StringComparison.OrdinalIgnoreCase) : s.StartsWith(text, StringComparison.OrdinalIgnoreCase));
            }

            Required(1);
            object? expected = values[0];
            FilterOperator op = filter.Operator;
            return row => Matches(ReportValue.Normalize(row[index]), op, expected);
        }

        private static bool Matches(object? value, FilterOperator op, object? expected)
        {
            if (op == FilterOperator.Equal && expected is null)
                return value is null;
            if (op == FilterOperator.NotEqual && expected is null)
                return value is not null;
            if (value is null || expected is null)
                return op == FilterOperator.NotEqual;

            int compare = ReportValue.Compare(value, expected);
            return op switch
            {
                FilterOperator.Equal => compare == 0,
                FilterOperator.NotEqual => compare != 0,
                FilterOperator.LessThan => compare < 0,
                FilterOperator.LessThanOrEqual => compare <= 0,
                FilterOperator.GreaterThan => compare > 0,
                FilterOperator.GreaterThanOrEqual => compare >= 0,
                _ => throw new ReportDefinitionException($"{op} bu filtrede kullanılamaz."),
            };
        }

        // ------------------------------------------------------------ hücreler

        private Accumulator[] Cell(int row, int column)
        {
            if (!_cells.TryGetValue((row, column), out Accumulator[]? accumulators))
            {
                accumulators = new Accumulator[_measures.Length];
                for (int m = 0; m < accumulators.Length; m++)
                    accumulators[m] = new Accumulator(_measures[m].Aggregate, _measureFields[m] < 0);
                _cells[(row, column)] = accumulators;
            }

            return accumulators;
        }

        /// <summary>Ham (gösterim dönüşümü uygulanmamış) değer.</summary>
        private object? Raw(AxisNode row, AxisNode column, int measure)
        {
            if (!_results.TryGetValue((row.Id, column.Id), out object?[]? results))
            {
                results = new object?[_measures.Length];
                if (_cells.TryGetValue((row.Id, column.Id), out Accumulator[]? accumulators))
                {
                    for (int m = 0; m < results.Length; m++)
                        results[m] = accumulators[m].Result(_measures[m].Percentile);
                }

                _results[(row.Id, column.Id)] = results;
            }

            return results[measure];
        }

        private decimal? RawNumber(AxisNode row, AxisNode column, int measure) => ReportValue.ToDecimal(Raw(row, column, measure));

        // ------------------------------------------------------------ having, sıralama, Top N

        private void ApplyHaving()
        {
            if (_definition.Having.Count == 0 || _rowDims.Length == 0)
                return;

            var conditions = _definition.Having
                .Select(h =>
                {
                    int measure = Array.FindIndex(_measures, m => m.EffectiveName == h.Measure);
                    if (measure < 0)
                        throw new ReportDefinitionException($"Having: '{h.Measure}' değeri yok.");
                    object?[] values = h.Values?.Select(ReportValue.Normalize).ToArray() ?? [];
                    return (Measure: measure, h.Operator, Values: values);
                })
                .ToArray();

            int depth = _rowDims.Length;
            foreach (AxisNode leaf in _rows.All.Where(n => n.Level == depth))
            {
                foreach ((int measure, FilterOperator op, object?[] values) in conditions)
                {
                    object? value = Raw(leaf, _columns.Root, measure);
                    bool ok = op == FilterOperator.Between
                        ? values.Length == 2 && value is not null && ReportValue.Compare(value, values[0]) >= 0 && ReportValue.Compare(value, values[1]) <= 0
                        : Matches(value, op, values.FirstOrDefault());
                    if (!ok)
                    {
                        leaf.Excluded = true;
                        break;
                    }
                }
            }

            // Tüm alt satırları elenen grup da gösterilmez.
            for (int level = depth - 1; level >= 1; level--)
            {
                foreach (AxisNode group in _rows.All.Where(n => n.Level == level))
                    group.Excluded = group.Children.Count > 0 && group.Children.All(c => c.Excluded);
            }
        }

        private void Arrange(Axis axis, AxisNode node, DimensionPlan[] dims, bool isRowAxis)
        {
            if (node.Level >= dims.Length || node.IsOthers)
                return;

            DimensionPlan dim = dims[node.Level];
            AxisNode otherRoot = isRowAxis ? _columns.Root : _rows.Root;
            int sortMeasure = dim.SortMeasureIndex;

            List<AxisNode> children = node.Children.Where(c => !c.Excluded).ToList();
            CompareInfo compare = _options.Culture.CompareInfo;

            Comparison<AxisNode> comparison = dim.Definition.Sort switch
            {
                DimensionSort.KeyDescending => (a, b) => ReportValue.Compare(b.Key, a.Key, compare),
                DimensionSort.MeasureAscending => (a, b) => CompareMeasure(a, b, isRowAxis, otherRoot, sortMeasure, compare),
                DimensionSort.MeasureDescending => (a, b) => CompareMeasure(b, a, isRowAxis, otherRoot, sortMeasure, compare),
                _ => (a, b) => ReportValue.Compare(a.Key, b.Key, compare),
            };
            children.Sort(comparison);

            if (dim.Definition.Top is int top && top >= 0 && children.Count > top)
            {
                List<AxisNode> rest = children.GetRange(top, children.Count - top);
                children.RemoveRange(top, children.Count - top);
                if (dim.Definition.ShowOthers)
                    children.Add(CreateOthers(axis, node, rest, isRowAxis));
            }

            node.Children = children;
            foreach (AxisNode child in children)
                Arrange(axis, child, dims, isRowAxis);
        }

        private int CompareMeasure(AxisNode a, AxisNode b, bool isRowAxis, AxisNode otherRoot, int measure, CompareInfo compare)
        {
            object? va = isRowAxis ? Raw(a, otherRoot, measure) : Raw(otherRoot, a, measure);
            object? vb = isRowAxis ? Raw(b, otherRoot, measure) : Raw(otherRoot, b, measure);
            int result = ReportValue.Compare(va, vb, compare);
            return result != 0 ? result : ReportValue.Compare(a.Key, b.Key, compare);
        }

        /// <summary>"Diğer": kalan kardeşlerin her hücredeki özetleri birleştirilir (ham veriye dönmeden).</summary>
        private AxisNode CreateOthers(Axis axis, AxisNode parent, List<AxisNode> rest, bool isRowAxis)
        {
            AxisNode others = axis.Create(parent, null);
            others.IsOthers = true;

            Axis otherAxis = isRowAxis ? _columns : _rows;
            foreach (AxisNode cross in otherAxis.All)
            {
                Accumulator[]? merged = null;
                foreach (AxisNode removed in rest)
                {
                    (int Row, int Column) key = isRowAxis ? (removed.Id, cross.Id) : (cross.Id, removed.Id);
                    if (!_cells.TryGetValue(key, out Accumulator[]? source))
                        continue;

                    merged ??= isRowAxis ? Cell(others.Id, cross.Id) : Cell(cross.Id, others.Id);
                    for (int m = 0; m < merged.Length; m++)
                        merged[m].Merge(source[m]);
                }
            }

            return others;
        }

        // ------------------------------------------------------------ düzleştirme

        private List<AxisNode> FlattenRows()
        {
            var result = new List<AxisNode>();
            if (_rowDims.Length == 0)
            {
                result.Add(_rows.Root);
                return result;
            }

            void Visit(AxisNode node)
            {
                foreach (AxisNode child in node.Children)
                {
                    bool isGroup = child.Level < _rowDims.Length && !child.IsOthers;
                    if (!isGroup)
                    {
                        result.Add(child);
                        continue;
                    }

                    if (_definition.ShowSubtotals)
                        result.Add(child); // grup başlığı alt satırlardan önce
                    Visit(child);
                }
            }

            Visit(_rows.Root);
            if (_definition.ShowRowGrandTotal)
                result.Add(_rows.Root);
            return result;
        }

        private List<AxisNode> FlattenColumns()
        {
            var result = new List<AxisNode>();
            if (_colDims.Length == 0)
            {
                result.Add(_columns.Root);
                return result;
            }

            void Visit(AxisNode node)
            {
                foreach (AxisNode child in node.Children)
                {
                    bool isGroup = child.Level < _colDims.Length && !child.IsOthers;
                    if (!isGroup)
                    {
                        result.Add(child);
                        continue;
                    }

                    Visit(child);
                    if (_definition.ShowSubtotals)
                        result.Add(child); // ara toplam sütunu alt sütunlardan sonra
                }
            }

            Visit(_columns.Root);
            if (_definition.ShowColumnGrandTotal)
                result.Add(_columns.Root);
            return result;
        }

        private ReportHeader Header(AxisNode node, int depth)
        {
            var keys = new List<object?>();
            var labels = new List<string>();
            DimensionPlan[] dims = node == _rows.Root || _rows.Contains(node) ? _rowDims : _colDims;

            for (AxisNode? current = node; current is { Parent: not null }; current = current.Parent)
            {
                keys.Insert(0, current.IsOthers ? null : current.Key);
                labels.Insert(0, current.IsOthers ? _options.OthersLabel : dims[current.Level - 1].Label(current.Key));
            }

            ReportHeaderKind kind = node.Parent is null ? ReportHeaderKind.GrandTotal
                : node.IsOthers ? ReportHeaderKind.Others
                : node.Level < depth ? ReportHeaderKind.Group
                : ReportHeaderKind.Item;

            if (kind == ReportHeaderKind.GrandTotal)
                labels.Add(_options.GrandTotalLabel);

            return new ReportHeader(node.Level, kind, keys, labels);
        }

        // ------------------------------------------------------------ gösterim

        private object? Display(AxisNode row, AxisNode column, int measure)
        {
            ReportMeasure definition = _measures[measure];
            if (definition.Display == MeasureDisplay.Value)
                return Raw(row, column, measure);

            decimal? value = RawNumber(row, column, measure);
            bool alongRows = definition.Axis == ReportAxis.Rows;

            switch (definition.Display)
            {
                case MeasureDisplay.PercentOfGrandTotal:
                    return Ratio(value, RawNumber(_rows.Root, _columns.Root, measure));
                case MeasureDisplay.PercentOfRowTotal:
                    return Ratio(value, RawNumber(row, _columns.Root, measure));
                case MeasureDisplay.PercentOfColumnTotal:
                    return Ratio(value, RawNumber(_rows.Root, column, measure));
                case MeasureDisplay.PercentOfParentRow:
                    return Ratio(value, RawNumber(row.Parent ?? row, column, measure));
                case MeasureDisplay.PercentOfParentColumn:
                    return Ratio(value, RawNumber(row, column.Parent ?? column, measure));
            }

            // Kardeşler boyunca (ör. aylar): kümülatif, önceki ile fark, sıra.
            AxisNode moving = alongRows ? row : column;
            if (moving.Parent is null)
                return definition.Display == MeasureDisplay.RunningTotal ? value : null;

            List<AxisNode> siblings = moving.Parent.Children;
            int position = siblings.IndexOf(moving);
            decimal? At(AxisNode sibling) => alongRows ? RawNumber(sibling, column, measure) : RawNumber(row, sibling, measure);

            switch (definition.Display)
            {
                case MeasureDisplay.RunningTotal:
                    decimal? total = null;
                    for (int i = 0; i <= position; i++)
                    {
                        if (At(siblings[i]) is { } v)
                            total = (total ?? 0) + v;
                    }

                    return total;
                case MeasureDisplay.DifferenceFromPrevious:
                    return position == 0 || value is null || At(siblings[position - 1]) is not { } previous ? null : value - previous;
                case MeasureDisplay.PercentDifferenceFromPrevious:
                    return position == 0 || value is null || At(siblings[position - 1]) is not { } before || before == 0
                        ? null
                        : (value - before) / Math.Abs(before);
                case MeasureDisplay.Rank:
                    if (value is null)
                        return null;
                    int greater = siblings.Count(s => At(s) is { } other && other > value);
                    return (decimal)(greater + 1);
                default:
                    return value;
            }
        }

        private static decimal? Ratio(decimal? value, decimal? total) => value is null || total is null || total == 0 ? null : value / total;

        private ReportResultMeasure DescribeMeasure(ReportMeasure measure)
        {
            string caption = measure.Caption
                ?? (measure.Field is null ? "Adet" : $"{measure.Aggregate} ({FieldCaption(measure.Field)})");
            string? format = measure.Format ?? measure.Display switch
            {
                MeasureDisplay.PercentOfColumnTotal or MeasureDisplay.PercentOfRowTotal or MeasureDisplay.PercentOfGrandTotal
                    or MeasureDisplay.PercentOfParentRow or MeasureDisplay.PercentOfParentColumn
                    or MeasureDisplay.PercentDifferenceFromPrevious => "P1",
                MeasureDisplay.Rank => "N0",
                _ => measure.Field is not null && IndexOf(measure.Field) is >= 0 and var i ? FieldAt(i).Format : null,
            };
            return new ReportResultMeasure(measure.EffectiveName, caption, measure.Aggregate, measure.Display, format);
        }

        private string FieldCaption(string field) => IndexOf(field) is >= 0 and var i ? FieldAt(i).Caption ?? FieldAt(i).Name : field;

        // ------------------------------------------------------------ gruplama düzeyi

        private sealed class DimensionPlan
        {
            private readonly Execution _owner;
            private readonly int _index;

            public DimensionPlan(ReportDimension definition, Execution owner)
            {
                Definition = definition;
                _owner = owner;
                _index = owner.RequireField(definition.Field);
                ReportField field = owner.FieldAt(_index);

                if (definition.DateGrouping != DateGrouping.None && field.Type != ReportDataType.Date && !field.IsCalculated)
                    throw new ReportDefinitionException($"'{field.Name}' tarih değil; tarih aralığı kullanılamaz.");
                if (definition.RangeSize is { } size && (size <= 0 || (field.Type != ReportDataType.Number && !field.IsCalculated)))
                    throw new ReportDefinitionException($"'{field.Name}' için aralık genişliği pozitif bir sayı alanında kullanılabilir.");

                SortMeasureIndex = definition.SortByMeasure is null
                    ? 0
                    : Array.FindIndex(owner._measures, m => m.EffectiveName == definition.SortByMeasure) is var found and >= 0
                        ? found
                        : throw new ReportDefinitionException($"Sıralama değeri '{definition.SortByMeasure}' yok.");
                Caption = definition.Caption ?? field.Caption ?? field.Name;
            }

            public ReportDimension Definition { get; }

            public int SortMeasureIndex { get; }

            public string Caption { get; }

            public ReportResultDimension Describe() => new(Definition.Field, Caption, Definition.DateGrouping);

            public object? KeyOf(object?[] row)
            {
                object? value = ReportValue.Normalize(row[_index]);
                if (value is null)
                    return null;

                if (Definition.DateGrouping != DateGrouping.None)
                    return ReportValue.ToDate(value) is { } date ? DateKey(date, Definition.DateGrouping) : null;

                if (Definition.RangeSize is { } size)
                    return ReportValue.ToDecimal(value) is { } number ? Math.Floor(number / size) * size : null;

                if (Definition.FirstLetter)
                    return value is string { Length: > 0 } text ? text[..1].ToUpper(_owner._options.Culture) : null;

                return value;
            }

            public string Label(object? key)
            {
                ReportEngineOptions options = _owner._options;
                CultureInfo culture = options.Culture;
                if (key is null)
                    return options.NullLabel;

                string quarter = culture.TwoLetterISOLanguageName == "tr" ? "Ç" : "Q";
                string week = culture.TwoLetterISOLanguageName == "tr" ? "H" : "W";

                switch (Definition.DateGrouping)
                {
                    case DateGrouping.Year or DateGrouping.DayOfMonth:
                        return Convert.ToString(key, culture)!;
                    case DateGrouping.YearQuarter when key is int yq:
                        return $"{yq / 10} {quarter}{yq % 10}";
                    case DateGrouping.YearMonth when key is int ym:
                        return new DateTime(ym / 100, ym % 100, 1).ToString("MMMM yyyy", culture);
                    case DateGrouping.YearWeek when key is int yw:
                        return $"{yw / 100} {week}{yw % 100:00}";
                    case DateGrouping.Quarter when key is int q:
                        return $"{quarter}{q}";
                    case DateGrouping.Month when key is int m:
                        return culture.DateTimeFormat.GetMonthName(m);
                    case DateGrouping.DayOfWeek when key is int dow:
                        return culture.DateTimeFormat.GetDayName((DayOfWeek)(dow % 7));
                    case DateGrouping.Hour when key is int h:
                        return $"{h:00}:00";
                    case DateGrouping.Date when key is DateTime d:
                        return d.ToString("d", culture);
                }

                if (Definition.RangeSize is { } size && key is decimal lower)
                    return $"{lower.ToString("#,0.##", culture)} - {(lower + size).ToString("#,0.##", culture)}";

                return key switch
                {
                    bool b => b ? options.TrueLabel : options.FalseLabel,
                    _ => ReportValue.ToText(key, culture) ?? options.NullLabel,
                };
            }

            private static int? DateKey(DateTime date, DateGrouping grouping) => grouping switch
            {
                DateGrouping.Year => date.Year,
                DateGrouping.YearQuarter => (date.Year * 10) + ((date.Month + 2) / 3),
                DateGrouping.YearMonth => (date.Year * 100) + date.Month,
                DateGrouping.YearWeek => (ISOWeek.GetYear(date) * 100) + ISOWeek.GetWeekOfYear(date),
                DateGrouping.Quarter => (date.Month + 2) / 3,
                DateGrouping.Month => date.Month,
                DateGrouping.DayOfWeek => date.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)date.DayOfWeek,
                DateGrouping.DayOfMonth => date.Day,
                DateGrouping.Hour => date.Hour,
                _ => null,
            };
        }

        // ------------------------------------------------------------ eksen ağacı

        private sealed class AxisNode(int id, AxisNode? parent, object? key)
        {
            public int Id { get; } = id;

            public AxisNode? Parent { get; } = parent;

            public object? Key { get; } = key;

            public int Level { get; } = parent is null ? 0 : parent.Level + 1;

            public List<AxisNode> Children { get; set; } = [];

            public Dictionary<object, AxisNode>? Index { get; set; }

            public bool IsOthers { get; set; }

            public bool Excluded { get; set; }
        }

        private sealed class Axis
        {
            private readonly HashSet<AxisNode> _members = [];

            public Axis()
            {
                Root = new AxisNode(0, null, null);
                All.Add(Root);
                _members.Add(Root);
            }

            public AxisNode Root { get; }

            public List<AxisNode> All { get; } = [];

            public bool Contains(AxisNode node) => _members.Contains(node);

            public AxisNode Child(AxisNode parent, object? key)
            {
                parent.Index ??= [];
                object lookup = key ?? NullKey;
                if (!parent.Index.TryGetValue(lookup, out AxisNode? child))
                {
                    child = Create(parent, key);
                    parent.Index[lookup] = child;
                    parent.Children.Add(child);
                }

                return child;
            }

            public AxisNode Create(AxisNode parent, object? key)
            {
                var node = new AxisNode(All.Count, parent, key);
                All.Add(node);
                _members.Add(node);
                return node;
            }
        }
    }
}
