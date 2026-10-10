using System.Globalization;

namespace Can.Core.Reporting;

public sealed partial class ReportEngine
{
    private sealed partial class Execution
    {
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

                if (Definition.DateGrouping == DateGrouping.Date)
                    return ReportValue.ToDate(value)?.Date;
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
