namespace CanFx.Reporting;

public sealed partial class ReportEngine
{
    private sealed partial class Execution
    {
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
    }
}
