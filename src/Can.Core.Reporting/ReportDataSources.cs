using System.Linq.Expressions;
using System.Reflection;

namespace Can.Core.Reporting;

/// <summary>Raporların çalıştığı veri kaynağı (siparişler, satış satırları ...).</summary>
public interface IReportDataSource
{
    /// <summary>Tanımlarda kullanılan ad (<c>ReportDefinition.DataSource</c>).</summary>
    string Name { get; }

    string Caption { get; }

    /// <summary>Kullanılabilir alanlar (tasarımcı listeler).</summary>
    IReadOnlyList<ReportField> Fields { get; }

    /// <summary>Gerekli yetki (operation claim); boşsa giriş yapmış herkes. Uç nokta katmanı denetler.</summary>
    string? Permission { get; }

    Task<ReportResult> RunAsync(ReportDefinition definition, ReportEngine engine, IServiceProvider services, CancellationToken cancellationToken = default);
}

/// <summary>Veri kaynağı ayarları.</summary>
public sealed class ReportSourceOptions
{
    private readonly Dictionary<string, (string? Caption, string? Format)> _fields = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _hidden = new(StringComparer.OrdinalIgnoreCase);

    public string? Caption { get; set; }

    public string? Permission { get; set; }

    /// <summary>
    /// Mümkünse veritabanında grupla (GROUP BY; yalnızca özetler çekilir). Çevrilemeyen tanımlar (ortanca, farklı sayısı,
    /// ISO hafta ...) kendiliğinden bellekte hesaplanır. Varsayılan açık.
    /// </summary>
    public bool DatabaseMode { get; set; } = true;

    /// <summary>Bellek modunda okunacak en fazla satır (aşılırsa rapor reddedilir: filtreyi daralt).</summary>
    public int MaxRows { get; set; } = 1_000_000;

    /// <summary>Alanın başlığı ve biçimi.</summary>
    public ReportSourceOptions Field(string name, string? caption, string? format = null)
    {
        _fields[name] = (caption, format);
        return this;
    }

    /// <summary>Alanı raporlarda kullanılamaz yapar (ör. Id, TenantId, şifre hash'i).</summary>
    public ReportSourceOptions Hide(params string[] names)
    {
        foreach (string name in names)
            _hidden.Add(name);
        return this;
    }

    internal bool IsHidden(string name) => _hidden.Contains(name);

    internal ReportField Describe(ReportField field) =>
        _fields.TryGetValue(field.Name, out (string? Caption, string? Format) info) ? field with { Caption = info.Caption ?? field.Caption, Format = info.Format ?? field.Format } : field;
}

/// <summary>
/// <see cref="IQueryable{T}"/> (EF Core sorgusu) üzerinde rapor. Önce veritabanında gruplamayı dener: filtreler WHERE,
/// gruplar GROUP BY, özetler SUM/COUNT/MIN/MAX olur ve yalnızca grup başına bir satır gelir. Olmazsa satırlar (filtreler
/// yine veritabanında uygulanarak) okunup bellekte hesaplanır. Sonuç iki yolda da aynıdır.
/// </summary>
public sealed class QueryableReportSource<T> : IReportDataSource
    where T : class
{
    private static readonly PropertyInfo[] Properties = typeof(T)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
        .ToArray();

    private readonly Func<IServiceProvider, IQueryable<T>> _query;
    private readonly ReportSourceOptions _options;
    private readonly PropertyInfo[] _visible;

    public QueryableReportSource(string name, Func<IServiceProvider, IQueryable<T>> query, ReportSourceOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(query);
        Name = name;
        _query = query;
        _options = options ?? new ReportSourceOptions();
        _visible = Properties.Where(p => !_options.IsHidden(p.Name) && !_options.IsHidden(ReportDataSet.CamelCase(p.Name))).ToArray();
        Fields = _visible
            .Select(p => _options.Describe(new ReportField(ReportDataSet.CamelCase(p.Name), ReportField.TypeOf(p.PropertyType))))
            .ToArray();
        Caption = _options.Caption ?? name;
    }

    public string Name { get; }

    public string Caption { get; }

    public IReadOnlyList<ReportField> Fields { get; }

    public string? Permission => _options.Permission;

    public async Task<ReportResult> RunAsync(ReportDefinition definition, ReportEngine engine, IServiceProvider services, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(engine);

        IQueryable<T> query = _query(services);
        var schema = new ReportDataSet(Fields, []);

        if (_options.DatabaseMode)
        {
            try
            {
                if (DatabasePlan.TryCreate(definition, _visible, Fields) is { } plan)
                {
                    (List<AggregatedGroup> groups, long rows) = await plan.ExecuteAsync(query, cancellationToken).ConfigureAwait(false);
                    return engine.RunAggregated(definition, schema, groups, rows);
                }
            }
            catch (Exception ex) when (IsTranslationFailure(ex))
            {
                // Sağlayıcı çeviremedi (ör. SQLite'ta decimal toplamı): bellekte hesapla.
            }
        }

        IQueryable<T> filtered = query;
        try
        {
            filtered = DatabasePlan.ApplyFilters(query, definition, _visible, Fields);
        }
        catch (Exception ex) when (IsTranslationFailure(ex))
        {
            // filtreler bellekte uygulanır
        }
        var rows2 = new List<object?[]>();
        Func<T, object?[]> read = Reader();
        await foreach (T item in Enumerate(filtered, cancellationToken).ConfigureAwait(false))
        {
            if (rows2.Count >= _options.MaxRows)
                throw new ReportDefinitionException($"Rapor {_options.MaxRows:N0} satırdan fazlasını okuyor: filtreyi daralt.");
            rows2.Add(read(item));
        }

        return engine.Run(definition, new ReportDataSet(Fields, rows2));
    }

    private static bool IsTranslationFailure(Exception ex) =>
        ex is InvalidOperationException or NotSupportedException or ArgumentException && ex is not ReportDefinitionException;

    private Func<T, object?[]> Reader()
    {
        ParameterExpression item = Expression.Parameter(typeof(T), "item");
        NewArrayExpression values = Expression.NewArrayInit(
            typeof(object),
            _visible.Select(p => Expression.Convert(Expression.Property(item, p), typeof(object))));
        return Expression.Lambda<Func<T, object?[]>>(values, item).Compile();
    }

    internal static async IAsyncEnumerable<TItem> Enumerate<TItem>(IQueryable<TItem> query, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (query is IAsyncEnumerable<TItem> asyncQuery)
        {
            await foreach (TItem item in asyncQuery.WithCancellation(cancellationToken).ConfigureAwait(false))
                yield return item;
        }
        else
        {
            foreach (TItem item in query)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return item;
            }
        }
    }

    // ================================================================ veritabanı planı

    /// <summary>Tanımı tek bir GROUP BY sorgusuna çevirir.</summary>
    private sealed class DatabasePlan
    {
        private readonly Expression<Func<T, bool>>? _where;
        private readonly LambdaExpression _keySelector;
        private readonly LambdaExpression _resultSelector;
        private readonly Type _keyType;
        private readonly Type _resultType;
        private readonly int _keyCount;
        private readonly int _rowDims;
        private readonly bool[] _intKeys;
        private readonly int _rowsSlot;
        private readonly MeasureSlots[] _measures;

        private DatabasePlan(
            Expression<Func<T, bool>>? where,
            LambdaExpression keySelector,
            LambdaExpression resultSelector,
            int keyCount,
            int rowDims,
            bool[] intKeys,
            int rowsSlot,
            MeasureSlots[] measures)
        {
            _where = where;
            _keySelector = keySelector;
            _resultSelector = resultSelector;
            _keyType = keySelector.ReturnType;
            _resultType = resultSelector.ReturnType;
            _keyCount = keyCount;
            _rowDims = rowDims;
            _intKeys = intKeys;
            _rowsSlot = rowsSlot;
            _measures = measures;
        }

        private sealed record MeasureSlots(int Count, int Sum, int SumOfSquares, int Min, int Max);

        /// <summary>Alan ve hesaplanmış alan ifadeleri (çevrilemeyen hesaplanmış alan: null).</summary>
        private static (ParameterExpression Item, List<Expression?> Fields, List<ReportField> Schema)? Compile(
            ReportDefinition definition,
            PropertyInfo[] properties,
            IReadOnlyList<ReportField> fields)
        {
            ParameterExpression item = Expression.Parameter(typeof(T), "x");
            var expressions = properties.Select(p => (Expression?)Expression.Property(item, p)).ToList();
            var schema = fields.ToList();
            var translator = new LinqTranslator(i => i < expressions.Count ? expressions[i] : null);

            foreach (ReportCalculatedField calculated in definition.CalculatedFields)
            {
                ReportExpression expression;
                try
                {
                    expression = ReportExpression.Parse(calculated.Expression, name => schema.FindIndex(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)));
                }
                catch (ReportDefinitionException)
                {
                    return null; // hatayı bellek modu bildirecek
                }

                expressions.Add(expression.Translate(translator));
                schema.Add(new ReportField(calculated.Name, ReportDataType.Number) { IsCalculated = true });
            }

            return (item, expressions, schema);
        }

        private static Expression? FieldOf(string name, List<Expression?> expressions, List<ReportField> schema)
        {
            int index = schema.FindIndex(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
            return index < 0 ? null : expressions[index];
        }

        private static Expression? Where(ReportDefinition definition, List<Expression?> expressions, List<ReportField> schema, bool requireAll)
        {
            var translator = new LinqTranslator(i => i < expressions.Count ? expressions[i] : null);
            Expression? where = null;

            foreach (ReportFilter filter in definition.Filters)
            {
                Expression? condition = FieldOf(filter.Field, expressions, schema) is { } field
                    ? translator.Filter(field, filter.Operator, filter.Values ?? [])
                    : null;
                if (condition is null)
                {
                    if (requireAll)
                        return Fail;
                    continue;
                }

                where = where is null ? condition : Expression.AndAlso(where, condition);
            }

            if (!string.IsNullOrWhiteSpace(definition.FilterExpression))
            {
                Expression? condition = null;
                try
                {
                    ReportExpression expression = ReportExpression.Parse(
                        definition.FilterExpression,
                        name => schema.FindIndex(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)));
                    condition = expression.Translate(translator) is { } e && e.Type == typeof(bool) ? e : null;
                }
                catch (ReportDefinitionException)
                {
                    condition = null;
                }

                if (condition is null)
                {
                    if (requireAll)
                        return Fail;
                }
                else
                {
                    where = where is null ? condition : Expression.AndAlso(where, condition);
                }
            }

            return where;
        }

        private static readonly Expression Fail = Expression.Constant("fail");

        /// <summary>Bellek modu için: çevrilebilen filtreler yine veritabanında uygulanır (okunan satır azalır).</summary>
        public static IQueryable<T> ApplyFilters(IQueryable<T> query, ReportDefinition definition, PropertyInfo[] properties, IReadOnlyList<ReportField> fields)
        {
            var compiled = Compile(definition, properties, fields);
            if (compiled is null)
                return query;
            (ParameterExpression item, List<Expression?> expressions, List<ReportField> schema) = compiled.Value;
            Expression? where = Where(definition, expressions, schema, requireAll: false);
            return where is null ? query : query.Where(Expression.Lambda<Func<T, bool>>(where, item));
        }

        public static DatabasePlan? TryCreate(ReportDefinition definition, PropertyInfo[] properties, IReadOnlyList<ReportField> fields)
        {
            var compiled = Compile(definition, properties, fields);
            if (compiled is null)
                return null;
            (ParameterExpression item, List<Expression?> expressions, List<ReportField> schema) = compiled.Value;

            ReportMeasure[] measures = definition.Measures.Count > 0 ? [.. definition.Measures] : [new ReportMeasure(null, ReportAggregate.Count)];
            if (measures.Any(m => !ReportEngine.IsDecomposable(m.Aggregate)))
                return null;

            Expression? where = Where(definition, expressions, schema, requireAll: true);
            if (where == Fail)
                return null;

            var translator = new LinqTranslator(i => i < expressions.Count ? expressions[i] : null);
            var keys = new List<Expression>();
            var intKeys = new List<bool>();
            foreach (ReportDimension dimension in definition.Rows.Concat(definition.Columns))
            {
                if (FieldOf(dimension.Field, expressions, schema) is not { } field)
                    return null;

                Expression? key = dimension.DateGrouping != DateGrouping.None ? translator.DateKey(field, dimension.DateGrouping)
                    : dimension.RangeSize is { } size ? translator.RangeKey(field, size)
                    : dimension.FirstLetter ? LinqTranslator.FirstLetter(field)
                    : field;
                if (key is null)
                    return null;
                keys.Add(key);
                intKeys.Add(dimension.DateGrouping is not (DateGrouping.None or DateGrouping.Date));
            }

            // Özet ifadeleri: g => g.Count(), g.Sum(x => v) ...
            var aggregates = new List<Func<ParameterExpression, Expression>>();
            var slots = new List<MeasureSlots>();
            int next = keys.Count + 1; // keys, sonra satır sayısı

            foreach (ReportMeasure measure in measures)
            {
                Expression? value = measure.Field is null ? null : FieldOf(measure.Field, expressions, schema);
                if (measure.Field is not null && value is null)
                    return null;

                int count = -1, sum = -1, squares = -1, min = -1, max = -1;
                switch (measure.Aggregate)
                {
                    case ReportAggregate.Count:
                        if (value is not null)
                        {
                            count = next++;
                            aggregates.Add(g => CountNotNull(g, item, value));
                        }

                        break;
                    case ReportAggregate.Sum or ReportAggregate.Average:
                        if (value is null || !LinqTranslator.IsNumber(value))
                            return null;
                        count = next++;
                        aggregates.Add(g => CountNotNull(g, item, value));
                        sum = next++;
                        aggregates.Add(g => Sum(g, item, value));
                        break;
                    case ReportAggregate.Min or ReportAggregate.Max:
                        if (value is null)
                            return null;
                        int slot = next++;
                        if (measure.Aggregate == ReportAggregate.Min)
                            min = slot;
                        else
                            max = slot;
                        string method = measure.Aggregate == ReportAggregate.Min ? nameof(Enumerable.Min) : nameof(Enumerable.Max);
                        aggregates.Add(g => MinMax(g, item, value, method));
                        break;
                    default: // varyans ailesi: n, Σx, Σx²
                        if (value is null || !LinqTranslator.IsNumber(value))
                            return null;
                        count = next++;
                        aggregates.Add(g => CountNotNull(g, item, value));
                        sum = next++;
                        aggregates.Add(g => Sum(g, item, Expression.Convert(value, typeof(double?))));
                        squares = next++;
                        aggregates.Add(g => Sum(g, item, Expression.Multiply(Expression.Convert(value, typeof(double?)), Expression.Convert(value, typeof(double?)))));
                        break;
                }

                slots.Add(new MeasureSlots(count, sum, squares, min, max));
            }

            if (next > ReportSlots.Capacity)
                return null;

            // Anahtar tipi: ReportSlots<k1, k2, ..., int, int ...>
            Type[] keyTypes = Enumerable.Range(0, ReportSlots.Capacity).Select(i => i < keys.Count ? keys[i].Type : typeof(int)).ToArray();
            Type keyType = typeof(ReportSlots<,,,,,,,,,,,,,,,>).MakeGenericType(keyTypes);
            LambdaExpression keySelector = Expression.Lambda(
                Expression.MemberInit(Expression.New(keyType), keys.Select((k, i) => Expression.Bind(keyType.GetProperty($"S{i}")!, k))),
                item);

            ParameterExpression g = Expression.Parameter(typeof(IGrouping<,>).MakeGenericType(keyType, typeof(T)), "g");
            var resultValues = new List<Expression>();
            for (int i = 0; i < keys.Count; i++)
                resultValues.Add(Expression.Property(Expression.Property(g, "Key"), $"S{i}"));
            resultValues.Add(Expression.Call(typeof(Enumerable), nameof(Enumerable.Count), [typeof(T)], g));
            resultValues.AddRange(aggregates.Select(a => a(g)));

            Type[] resultTypes = Enumerable.Range(0, ReportSlots.Capacity).Select(i => i < resultValues.Count ? resultValues[i].Type : typeof(int)).ToArray();
            Type resultType = typeof(ReportSlots<,,,,,,,,,,,,,,,>).MakeGenericType(resultTypes);
            LambdaExpression resultSelector = Expression.Lambda(
                Expression.MemberInit(Expression.New(resultType), resultValues.Select((v, i) => Expression.Bind(resultType.GetProperty($"S{i}")!, v))),
                g);

            return new DatabasePlan(
                where is null ? null : Expression.Lambda<Func<T, bool>>(where, item),
                keySelector,
                resultSelector,
                keys.Count,
                definition.Rows.Count,
                intKeys.ToArray(),
                keys.Count,
                slots.ToArray());
        }

        private static Expression CountNotNull(ParameterExpression g, ParameterExpression item, Expression value)
        {
            if (value.Type.IsValueType && Nullable.GetUnderlyingType(value.Type) is null)
                return Expression.Call(typeof(Enumerable), nameof(Enumerable.Count), [typeof(T)], g);

            LambdaExpression predicate = Expression.Lambda(Expression.NotEqual(value, Expression.Constant(null, value.Type)), item);
            return Expression.Call(typeof(Enumerable), nameof(Enumerable.Count), [typeof(T)], g, predicate);
        }

        private static Expression Sum(ParameterExpression g, ParameterExpression item, Expression value)
        {
            Type underlying = Nullable.GetUnderlyingType(value.Type) ?? value.Type;
            Type target = underlying == typeof(decimal) ? typeof(decimal?)
                : underlying == typeof(double) || underlying == typeof(float) ? typeof(double?)
                : typeof(long?);
            Expression converted = value.Type == target ? value : Expression.Convert(value, target);
            LambdaExpression selector = Expression.Lambda(converted, item);
            return Expression.Call(typeof(Enumerable), nameof(Enumerable.Sum), [typeof(T)], g, selector);
        }

        private static Expression MinMax(ParameterExpression g, ParameterExpression item, Expression value, string method)
        {
            Expression nullable = LinqTranslator.Nullable(value);
            LambdaExpression selector = Expression.Lambda(nullable, item);
            return Expression.Call(typeof(Enumerable), method, [typeof(T), nullable.Type], g, selector);
        }

        public async Task<(List<AggregatedGroup> Groups, long Rows)> ExecuteAsync(IQueryable<T> query, CancellationToken cancellationToken)
        {
            IQueryable<T> source = _where is null ? query : query.Where(_where);

            Expression grouped = Expression.Call(typeof(Queryable), nameof(Queryable.GroupBy), [typeof(T), _keyType], source.Expression, Expression.Quote(_keySelector));
            Expression selected = Expression.Call(
                typeof(Queryable),
                nameof(Queryable.Select),
                [typeof(IGrouping<,>).MakeGenericType(_keyType, typeof(T)), _resultType],
                grouped,
                Expression.Quote(_resultSelector));

            IQueryable results = source.Provider.CreateQuery(selected);
            List<ReportSlots> rows = await (Task<List<ReportSlots>>)typeof(DatabasePlan)
                .GetMethod(nameof(ReadAsync), BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(_resultType)
                .Invoke(null, [results, cancellationToken])!;

            var groups = new List<AggregatedGroup>(rows.Count);
            long total = 0;
            foreach (ReportSlots row in rows)
            {
                var rowKeys = new object?[_rowDims];
                var columnKeys = new object?[_keyCount - _rowDims];
                for (int i = 0; i < _keyCount; i++)
                {
                    object? raw = row.Get(i);
                    object? key = raw is null ? null : _intKeys[i] ? Convert.ToInt32(raw, System.Globalization.CultureInfo.InvariantCulture) : ReportValue.Normalize(raw);
                    if (i < _rowDims)
                        rowKeys[i] = key;
                    else
                        columnKeys[i - _rowDims] = key;
                }

                long count = Convert.ToInt64(row.Get(_rowsSlot), System.Globalization.CultureInfo.InvariantCulture);
                total += count;

                var partials = new MeasurePartial[_measures.Length];
                for (int m = 0; m < _measures.Length; m++)
                {
                    MeasureSlots s = _measures[m];
                    partials[m] = new MeasurePartial(
                        s.Count < 0 ? count : Convert.ToInt64(row.Get(s.Count), System.Globalization.CultureInfo.InvariantCulture),
                        s.Sum < 0 ? null : ReportValue.ToDecimal(row.Get(s.Sum)),
                        s.SumOfSquares < 0 ? null : ReportValue.ToDecimal(row.Get(s.SumOfSquares)),
                        s.Min < 0 ? null : ReportValue.Normalize(row.Get(s.Min)),
                        s.Max < 0 ? null : ReportValue.Normalize(row.Get(s.Max)));
                }

                groups.Add(new AggregatedGroup(rowKeys, columnKeys, count, partials));
            }

            return (groups, total);
        }

        private static async Task<List<ReportSlots>> ReadAsync<TRow>(IQueryable<TRow> query, CancellationToken cancellationToken)
            where TRow : ReportSlots
        {
            var list = new List<ReportSlots>();
            await foreach (TRow row in Enumerate(query, cancellationToken).ConfigureAwait(false))
                list.Add(row);
            return list;
        }
    }
}
