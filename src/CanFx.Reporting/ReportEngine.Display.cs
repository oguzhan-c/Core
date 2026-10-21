namespace CanFx.Reporting;

public sealed partial class ReportEngine
{
    private sealed partial class Execution
    {
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
    }
}
