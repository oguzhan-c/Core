using System.Collections;
using System.ComponentModel;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;

namespace Can.Core.Persistence.Dynamic;

/// <summary>
/// <see cref="DynamicQuery"/>'yi expression tree'ye çevirip sorguya ekler.
/// </summary>
/// <remarks>
/// nArchitecture'daki System.Linq.Dynamic.Core + string birleştirme yaklaşımının yerine doğrudan
/// expression tree kurulur:
/// <list type="bullet">
/// <item>Ek paket bağımlılığı yoktur.</item>
/// <item>Alan adları gerçek property'lere karşı doğrulanır; istemci sorguya rastgele ifade enjekte edemez.</item>
/// <item>Değerler property tipine çevrilip parametre olarak gönderilir (EF Core SQL parametresi üretir).</item>
/// </list>
/// </remarks>
public static class DynamicQueryableExtensions
{
    private static readonly MethodInfo StringContains = typeof(string).GetMethod(nameof(string.Contains), new[] { typeof(string) })!;
    private static readonly MethodInfo StringStartsWith = typeof(string).GetMethod(nameof(string.StartsWith), new[] { typeof(string) })!;
    private static readonly MethodInfo StringEndsWith = typeof(string).GetMethod(nameof(string.EndsWith), new[] { typeof(string) })!;
    private static readonly MethodInfo StringToLower = typeof(string).GetMethod(nameof(string.ToLower), Type.EmptyTypes)!;
    private static readonly MethodInfo StringCompare = typeof(string).GetMethod(nameof(string.Compare), new[] { typeof(string), typeof(string) })!;

    private static readonly MethodInfo EnumerableContains = typeof(Enumerable)
        .GetMethods(BindingFlags.Public | BindingFlags.Static)
        .Single(m => m.Name == nameof(Enumerable.Contains) && m.GetParameters().Length == 2);

    /// <summary>Filtre ve sıralamayı uygular.</summary>
    public static IQueryable<T> ToDynamic<T>(this IQueryable<T> query, DynamicQuery dynamicQuery)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(dynamicQuery);

        if (dynamicQuery.Filter is not null)
            query = query.ApplyFilter(dynamicQuery.Filter);

        if (dynamicQuery.Sort is not null)
            query = query.ApplySort(dynamicQuery.Sort);

        return query;
    }

    public static IQueryable<T> ApplyFilter<T>(this IQueryable<T> query, Filter filter)
    {
        ParameterExpression parameter = Expression.Parameter(typeof(T), "x");
        Expression? body = BuildFilter(parameter, filter);

        return body is null ? query : query.Where(Expression.Lambda<Func<T, bool>>(body, parameter));
    }

    public static IQueryable<T> ApplySort<T>(this IQueryable<T> query, IEnumerable<Sort> sorts)
    {
        bool first = true;

        foreach (Sort sort in sorts)
        {
            bool descending = (sort.Dir ?? "asc").ToLowerInvariant() switch
            {
                "asc" or "" => false,
                "desc" => true,
                _ => throw new ArgumentException($"Geçersiz sıralama yönü: '{sort.Dir}'. 'asc' ya da 'desc' olmalı."),
            };

            ParameterExpression parameter = Expression.Parameter(typeof(T), "x");
            Expression member = BuildMemberPath(parameter, sort.Field, out _);
            LambdaExpression keySelector = Expression.Lambda(member, parameter);

            string methodName = (first, descending) switch
            {
                (true, false) => nameof(Queryable.OrderBy),
                (true, true) => nameof(Queryable.OrderByDescending),
                (false, false) => nameof(Queryable.ThenBy),
                (false, true) => nameof(Queryable.ThenByDescending),
            };

            MethodCallExpression call = Expression.Call(
                typeof(Queryable),
                methodName,
                new[] { typeof(T), member.Type },
                query.Expression,
                Expression.Quote(keySelector)
            );

            query = query.Provider.CreateQuery<T>(call);
            first = false;
        }

        return query;
    }

    // ---------------------------------------------------------------- Filtre ağacı

    private static Expression? BuildFilter(ParameterExpression parameter, Filter filter)
    {
        Expression? own = string.IsNullOrWhiteSpace(filter.Field) ? null : BuildCondition(parameter, filter);

        Expression[] children = (filter.Filters ?? [])
            .Select(child => BuildFilter(parameter, child))
            .OfType<Expression>()
            .ToArray();

        if (children.Length == 0)
            return own;

        bool or = (filter.Logic ?? "and").ToLowerInvariant() switch
        {
            "and" => false,
            "or" => true,
            _ => throw new ArgumentException($"Geçersiz logic: '{filter.Logic}'. 'and' ya da 'or' olmalı."),
        };

        Func<Expression, Expression, Expression> combine = or
            ? (left, right) => Expression.OrElse(left, right)
            : (left, right) => Expression.AndAlso(left, right);
        Expression grouped = children.Aggregate(combine);

        return own is null ? grouped : combine(own, grouped);
    }

    private static Expression BuildCondition(ParameterExpression parameter, Filter filter)
    {
        string @operator = (filter.Operator ?? string.Empty).ToLowerInvariant();
        Expression member = BuildMemberPath(parameter, filter.Field!, out IReadOnlyList<Expression> nullChecks);
        Type type = member.Type;

        Expression condition = @operator switch
        {
            // null olamayan değer tiplerinde (int, Guid ...) isnull her zaman false'tur
            FilterOperators.IsNull => ReflectionCanBeNull(type)
                ? (Expression)Expression.Equal(member, Expression.Constant(null, type))
                : Expression.Constant(false),
            FilterOperators.IsNotNull => ReflectionCanBeNull(type)
                ? (Expression)Expression.NotEqual(member, Expression.Constant(null, type))
                : Expression.Constant(true),

            FilterOperators.Equal => Expression.Equal(member, Value(filter, type)),
            FilterOperators.NotEqual => Expression.NotEqual(member, Value(filter, type)),

            FilterOperators.LessThan => Compare(member, Value(filter, type), ExpressionType.LessThan),
            FilterOperators.LessThanOrEqual => Compare(member, Value(filter, type), ExpressionType.LessThanOrEqual),
            FilterOperators.GreaterThan => Compare(member, Value(filter, type), ExpressionType.GreaterThan),
            FilterOperators.GreaterThanOrEqual => Compare(member, Value(filter, type), ExpressionType.GreaterThanOrEqual),

            FilterOperators.Between => Between(member, filter),
            FilterOperators.In => In(member, filter),

            FilterOperators.Contains => Text(member, filter, StringContains),
            FilterOperators.DoesNotContain => Expression.Not(Text(member, filter, StringContains)),
            FilterOperators.StartsWith => Text(member, filter, StringStartsWith),
            FilterOperators.EndsWith => Text(member, filter, StringEndsWith),

            _ => throw new ArgumentException($"Geçersiz operatör: '{filter.Operator}'."),
        };

        // isnull dışındaki koşullarda ara navigation'lar null olmamalı: x.Category != null && x.Category.Name == ...
        if (@operator != FilterOperators.IsNull)
        {
            for (int i = nullChecks.Count - 1; i >= 0; i--)
                condition = Expression.AndAlso(Expression.NotEqual(nullChecks[i], Expression.Constant(null, nullChecks[i].Type)), condition);
        }

        return condition;
    }

    /// <summary><c>x.Category.Name</c> — her parça gerçek bir public property olmalı.</summary>
    private static Expression BuildMemberPath(ParameterExpression parameter, string path, out IReadOnlyList<Expression> nullChecks)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Alan adı boş olamaz.");

        var checks = new List<Expression>();
        Expression current = parameter;

        string[] segments = path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (int i = 0; i < segments.Length; i++)
        {
            if (i > 0 && !current.Type.IsValueType)
                checks.Add(current);

            PropertyInfo property =
                current.Type.GetProperty(segments[i], BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)
                ?? throw new ArgumentException($"Geçersiz alan: '{path}'. '{current.Type.Name}' tipinde '{segments[i]}' yok.");

            current = Expression.Property(current, property);
        }

        nullChecks = checks;
        return current;
    }

    // ---------------------------------------------------------------- Operatörler

    private static Expression Compare(Expression member, Expression value, ExpressionType comparison)
    {
        // string için <, > tanımlı değil: string.Compare(a, b) > 0
        if (member.Type == typeof(string))
        {
            Expression compare = Expression.Call(StringCompare, member, value);
            return Expression.MakeBinary(comparison, compare, Expression.Constant(0));
        }

        // enum'larda <, > tanımlı değil: alttaki sayısal tipe çevrilerek karşılaştırılır
        Type? underlyingNullable = Nullable.GetUnderlyingType(member.Type);
        Type plain = underlyingNullable ?? member.Type;
        if (plain.IsEnum)
        {
            Type numeric = Enum.GetUnderlyingType(plain);
            Type target = underlyingNullable is null ? numeric : typeof(Nullable<>).MakeGenericType(numeric);
            return Expression.MakeBinary(comparison, Expression.Convert(member, target), Expression.Convert(value, target));
        }

        return Expression.MakeBinary(comparison, member, value);
    }

    private static Expression Between(Expression member, Filter filter)
    {
        string[] bounds = SplitValues(filter);
        if (bounds.Length != 2)
            throw new ArgumentException($"'between' için değer 'alt,üst' biçiminde olmalı. Verilen: '{filter.Value}'.");

        return Expression.AndAlso(
            Compare(member, Parameterize(ConvertValue(bounds[0], member.Type, filter), member.Type), ExpressionType.GreaterThanOrEqual),
            Compare(member, Parameterize(ConvertValue(bounds[1], member.Type, filter), member.Type), ExpressionType.LessThanOrEqual)
        );
    }

    private static Expression In(Expression member, Filter filter)
    {
        bool lower = member.Type == typeof(string) && !filter.CaseSensitive;

        IList values = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(member.Type))!;
        foreach (string raw in SplitValues(filter))
        {
            object? value = ConvertValue(raw, member.Type, filter);
            values.Add(lower ? ((string?)value)?.ToLowerInvariant() : value);
        }

        Expression target = lower ? Expression.Call(member, StringToLower) : member;
        Type listType = typeof(IEnumerable<>).MakeGenericType(member.Type);

        return Expression.Call(
            EnumerableContains.MakeGenericMethod(member.Type),
            Parameterize(values, listType),
            target
        );
    }

    private static Expression Text(Expression member, Filter filter, MethodInfo method)
    {
        if (member.Type != typeof(string))
            throw new ArgumentException($"'{filter.Operator}' yalnızca metin alanlarında kullanılabilir ('{filter.Field}' {member.Type.Name}).");

        string value = filter.Value ?? string.Empty;

        if (filter.CaseSensitive)
            return Expression.Call(member, method, Parameterize(value, typeof(string)));

        return Expression.Call(
            Expression.Call(member, StringToLower),
            method,
            Parameterize(value.ToLowerInvariant(), typeof(string))
        );
    }

    // ---------------------------------------------------------------- Değerler

    private static bool ReflectionCanBeNull(Type type) => !type.IsValueType || Nullable.GetUnderlyingType(type) is not null;

    private static Expression Value(Filter filter, Type type) =>
        Parameterize(ConvertValue(filter.Value, type, filter), type);

    private static string[] SplitValues(Filter filter) =>
        (filter.Value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>"42", "2026-10-05", "Active", "true" gibi metinleri property tipine çevirir.</summary>
    private static object? ConvertValue(string? raw, Type type, Filter filter)
    {
        if (raw is null)
            return null;

        if (type == typeof(string))
            return raw;

        try
        {
            return TypeDescriptor.GetConverter(type).ConvertFromString(null, CultureInfo.InvariantCulture, raw);
        }
        catch (Exception ex) when (ex is FormatException or NotSupportedException or ArgumentException or InvalidCastException)
        {
            throw new ArgumentException($"'{raw}' değeri '{filter.Field}' alanının tipine ({type.Name}) çevrilemedi.", ex);
        }
    }

    /// <summary>
    /// Değeri sabit (constant) yerine bir nesnenin property'si olarak verir; EF Core bunu SQL
    /// parametresine çevirir (sorgu planı cache'i kirlenmez).
    /// </summary>
    private static Expression Parameterize(object? value, Type type)
    {
        object holder = Activator.CreateInstance(typeof(ValueHolder<>).MakeGenericType(type), value)!;
        return Expression.Property(Expression.Constant(holder), nameof(ValueHolder<object>.Value));
    }

    private sealed class ValueHolder<T>(T value)
    {
        public T Value { get; } = value;
    }
}
