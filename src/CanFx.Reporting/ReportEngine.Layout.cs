using System.Globalization;

namespace CanFx.Reporting;

public sealed partial class ReportEngine
{
    private sealed partial class Execution
    {
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
    }
}
