namespace Can.Core.Reporting;

public sealed partial class ReportDefinition
{
    /// <summary>
    /// Tanımı verilen alanlara göre denetler ve TÜM hataları döndürür (tasarımcıda hepsini birden göstermek için).
    /// Boş liste: tanım geçerli. Motorun çalışırken yaptığı denetimlerin aynısıdır; çalıştırmadan önce erken hata verir.
    /// </summary>
    /// <param name="fields">Veri kaynağının alanları (<see cref="IReportDataSource.Fields"/>).</param>
    public IReadOnlyList<string> Validate(IReadOnlyList<ReportField> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        var errors = new List<string>();
        var known = new Dictionary<string, ReportField>(StringComparer.OrdinalIgnoreCase);
        foreach (ReportField field in fields)
            known.TryAdd(field.Name, field);

        var order = known.Keys.ToList();
        int IndexOf(string name) => order.FindIndex(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));

        // Hesaplanmış alanlar: sırayla, yalnızca öncekilere başvurabilir.
        foreach (ReportCalculatedField calculated in CalculatedFields)
        {
            if (string.IsNullOrWhiteSpace(calculated.Name) || known.ContainsKey(calculated.Name))
            {
                errors.Add($"Hesaplanmış alan adı boş ya da zaten var: '{calculated.Name}'.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(calculated.Expression))
                errors.Add($"'{calculated.Name}' hesaplanmış alanının ifadesi boş.");
            else
                Parse($"'{calculated.Name}' hesaplanmış alanı", calculated.Expression);

            known[calculated.Name] = new ReportField(calculated.Name, ReportDataType.Number) { IsCalculated = true };
            order.Add(calculated.Name);
        }

        // Filtreler.
        foreach (ReportFilter filter in Filters)
        {
            if (!known.ContainsKey(filter.Field))
            {
                errors.Add($"Filtre: '{filter.Field}' alanı yok.");
                continue;
            }

            int count = filter.Values?.Count ?? 0;
            int required = filter.Operator switch
            {
                FilterOperator.IsNull or FilterOperator.IsNotNull or FilterOperator.In or FilterOperator.NotIn => 0,
                FilterOperator.Between => 2,
                _ => 1,
            };
            if (count < required)
                errors.Add($"'{filter.Field}' filtresi ({filter.Operator}) {required} değer ister.");
        }

        if (!string.IsNullOrWhiteSpace(FilterExpression))
            Parse("Filtre ifadesi", FilterExpression);

        // Değerler.
        ReportMeasure[] measures = Measures.Count > 0 ? [.. Measures] : [new ReportMeasure(null, ReportAggregate.Count) { Name = "count" }];
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (ReportMeasure measure in measures)
        {
            if (!names.Add(measure.EffectiveName))
                errors.Add($"'{measure.EffectiveName}' değer adı birden fazla kez kullanılmış (aynı alanı iki kez kullanıyorsan Name ver).");
            if (measure.Field is null && measure.Aggregate != ReportAggregate.Count)
                errors.Add($"'{measure.Aggregate}' için alan gerekli (yalnızca Count alansız olabilir).");
            if (measure.Field is not null && !known.ContainsKey(measure.Field))
                errors.Add($"Değer: '{measure.Field}' alanı yok.");
            if (measure.Aggregate == ReportAggregate.Percentile && measure.Percentile is not (>= 0 and <= 1))
                errors.Add($"'{measure.EffectiveName}': Percentile 0 ile 1 arasında olmalı (ör. 0.9).");
        }

        // Gruplama düzeyleri.
        foreach (ReportDimension dimension in Rows.Concat(Columns))
        {
            if (!known.TryGetValue(dimension.Field, out ReportField? field))
            {
                errors.Add($"Gruplama: '{dimension.Field}' alanı yok.");
                continue;
            }

            if (dimension.DateGrouping != DateGrouping.None && field.Type != ReportDataType.Date && !field.IsCalculated)
                errors.Add($"'{field.Name}' tarih değil; tarih aralığı kullanılamaz.");
            if (dimension.RangeSize is { } size && (size <= 0 || (field.Type != ReportDataType.Number && !field.IsCalculated)))
                errors.Add($"'{field.Name}' için aralık genişliği pozitif bir sayı alanında kullanılabilir.");
            if (dimension.SortByMeasure is { } sortBy && !names.Contains(sortBy))
                errors.Add($"Sıralama değeri '{sortBy}' yok.");
            if (dimension.Top is <= 0)
                errors.Add($"'{field.Name}': Top pozitif olmalı.");
        }

        // Gruplama sonrası filtre.
        if (Having.Count > 0 && Rows.Count == 0)
            errors.Add("Having yalnızca satır gruplaması varken kullanılabilir (en alt satırlara uygulanır).");
        foreach (ReportHaving having in Having)
        {
            if (!names.Contains(having.Measure))
                errors.Add($"Having: '{having.Measure}' değeri yok.");
            if (having.Operator is not (FilterOperator.Equal or FilterOperator.NotEqual or FilterOperator.LessThan or FilterOperator.LessThanOrEqual
                or FilterOperator.GreaterThan or FilterOperator.GreaterThanOrEqual or FilterOperator.Between))
                errors.Add($"Having: {having.Operator} kullanılamaz (yalnızca karşılaştırma ve Between).");
            else if (having.Operator == FilterOperator.Between ? (having.Values?.Count ?? 0) < 2 : (having.Values?.Count ?? 0) < 1)
                errors.Add($"Having: '{having.Measure}' ({having.Operator}) için değer eksik.");
        }

        return errors;

        void Parse(string owner, string text)
        {
            try
            {
                ReportExpression.Parse(text, IndexOf);
            }
            catch (ReportDefinitionException ex)
            {
                errors.Add($"{owner}: {ex.Message}");
            }
        }
    }

    /// <summary><see cref="Validate"/> hata bulursa hepsini tek mesajda fırlatır.</summary>
    /// <exception cref="ReportDefinitionException">Tanım geçersiz.</exception>
    public void EnsureValid(IReadOnlyList<ReportField> fields)
    {
        IReadOnlyList<string> errors = Validate(fields);
        if (errors.Count > 0)
            throw new ReportDefinitionException(string.Join(Environment.NewLine, errors)) { Errors = errors };
    }
}
