using System.Linq.Expressions;
using System.Reflection;

namespace Can.Core.Reporting;

/// <summary>
/// Rapor ifadelerini, filtrelerini ve gruplama anahtarlarını LINQ ifade ağacına çevirir; EF Core bunları SQL'e çevirir.
/// Çevrilemeyen bir şey olursa <see langword="null"/> döner ve rapor bellekte hesaplanır.
/// Sayılar kendi tiplerinde kalır (veritabanı toplamı kendi tipinde yapsın); farklı tipler genişletilir:
/// tamsayı &lt; double &lt; decimal. Bölme her zaman ondalıklıdır ve sıfıra bölme boş verir (bellekteki gibi).
/// </summary>
internal sealed class LinqTranslator(Func<int, Expression?> field)
{
    private enum Kind
    {
        Number,
        Text,
        Date,
        Bool,
        Null,
        Other,
    }

    // ---------------------------------------------------------------- ifade düğümleri

    public Expression? Literal(object? value) =>
        value switch
        {
            null => Expression.Constant(null),
            decimal d => Expression.Constant(d, typeof(decimal?)),
            string s => Expression.Constant(s, typeof(string)),
            bool b => Expression.Constant(b, typeof(bool?)),
            DateTime t => Expression.Constant(t, typeof(DateTime?)),
            _ => null,
        };

    public Expression? Field(int index) => field(index);

    public Expression? Unary(char op, Expression operand) =>
        op switch
        {
            '-' when KindOf(operand) == Kind.Number => Expression.Negate(Nullable(operand)),
            '!' when ToBool(operand) is { } b => Expression.Not(b),
            _ => null,
        };

    public Expression? Binary(string op, Expression a, Expression b)
    {
        switch (op)
        {
            case "and":
                return ToBool(a) is { } la && ToBool(b) is { } lb ? Expression.AndAlso(la, lb) : null;
            case "or":
                return ToBool(a) is { } oa && ToBool(b) is { } ob ? Expression.OrElse(oa, ob) : null;
            case "=" or "!=" or "<" or "<=" or ">" or ">=":
                return Compare(op, a, b);
        }

        if (op == "+" && KindOf(a) == Kind.Text && KindOf(b) == Kind.Text)
            return Expression.Call(typeof(string).GetMethod(nameof(string.Concat), [typeof(string), typeof(string)])!, a, b);

        if (Promote(a, b, division: op == "/") is not { } promoted)
            return null;
        (Expression x, Expression y) = promoted;

        return op switch
        {
            "+" => Expression.Add(x, y),
            "-" => Expression.Subtract(x, y),
            "*" => Expression.Multiply(x, y),
            "/" => GuardZero(y, Expression.Divide(x, y)),
            "%" => GuardZero(y, Expression.Modulo(x, y)),
            _ => null,
        };
    }

    public Expression? Call(string name, Expression[] args)
    {
        switch (name)
        {
            case "iif":
                return Iif(args);
            case "isnull" when args.Length == 1:
                return IsNullCheck(args[0]);
            case "isnull" or "coalesce":
                Expression? result = args[^1];
                for (int i = args.Length - 2; i >= 0 && result is not null; i--)
                    result = Coalesce(args[i], result);
                return result;
            case "year" or "month" or "day" or "hour":
                return DatePart(args[0], char.ToUpperInvariant(name[0]) + name[1..]);
            case "quarter":
                return DateKey(args[0], DateGrouping.Quarter);
            case "dayofweek":
                return DateKey(args[0], DateGrouping.DayOfWeek);
            case "date":
                return DateOnly(args[0]);
            case "abs" or "floor" or "ceiling":
                return MathCall(name == "abs" ? nameof(Math.Abs) : name == "floor" ? nameof(Math.Floor) : nameof(Math.Ceiling), args[0]);
            case "round":
                return Round(args);
            case "upper" or "lower" or "trim":
                return KindOf(args[0]) == Kind.Text
                    ? Expression.Call(args[0], typeof(string).GetMethod(name == "upper" ? nameof(string.ToUpper) : name == "lower" ? nameof(string.ToLower) : nameof(string.Trim), Type.EmptyTypes)!)
                    : null;
            case "len":
                return KindOf(args[0]) == Kind.Text
                    ? Expression.Condition(
                        Expression.Equal(args[0], Expression.Constant(null, typeof(string))),
                        Expression.Constant(null, typeof(int?)),
                        Expression.Convert(Expression.Property(args[0], nameof(string.Length)), typeof(int?)))
                    : null;
            case "contains" or "startswith" or "endswith":
                return TextMatch(name, args[0], args[1]);
            default:
                return null; // ör. Week, DateDiff, Power: bellekte
        }
    }

    // ---------------------------------------------------------------- filtreler ve gruplama anahtarları

    /// <summary>Yapısal filtre; değerler alanın tipine çevrilir.</summary>
    public Expression? Filter(Expression fieldExpression, FilterOperator op, IReadOnlyList<object?> values)
    {
        Expression? Constant(object? value) => ConstantLike(fieldExpression, value);

        switch (op)
        {
            case FilterOperator.IsNull:
                return IsNullCheck(fieldExpression);
            case FilterOperator.IsNotNull:
                return IsNullCheck(fieldExpression) is { } isNull ? Expression.Not(isNull) : null;
            case FilterOperator.Contains or FilterOperator.StartsWith:
                return values.Count > 0 && Constant(values[0]) is { } text ? TextMatch(op == FilterOperator.Contains ? "contains" : "startswith", fieldExpression, text) : null;
            case FilterOperator.Between:
                if (values.Count < 2 || Constant(values[0]) is not { } from || Constant(values[1]) is not { } to)
                    return null;
                return Compare(">=", fieldExpression, from) is { } lower && Compare("<=", fieldExpression, to) is { } upper ? Expression.AndAlso(lower, upper) : null;
            case FilterOperator.In or FilterOperator.NotIn:
                Expression? any = null;
                foreach (object? value in values)
                {
                    Expression? equal = value is null ? IsNullCheck(fieldExpression) : Constant(value) is { } c ? Compare("=", fieldExpression, c) : null;
                    if (equal is null)
                        return null;
                    any = any is null ? equal : Expression.OrElse(any, equal);
                }

                any ??= Expression.Constant(false);
                return op == FilterOperator.In ? any : Expression.Not(any);
        }

        object? expected = values.Count > 0 ? values[0] : null;
        if (expected is null)
        {
            return op switch
            {
                FilterOperator.Equal => IsNullCheck(fieldExpression),
                FilterOperator.NotEqual => IsNullCheck(fieldExpression) is { } n ? Expression.Not(n) : null,
                _ => null,
            };
        }

        if (Constant(expected) is not { } constant)
            return null;

        string? comparison = op switch
        {
            FilterOperator.Equal => "=",
            FilterOperator.NotEqual => "!=",
            FilterOperator.LessThan => "<",
            FilterOperator.LessThanOrEqual => "<=",
            FilterOperator.GreaterThan => ">",
            FilterOperator.GreaterThanOrEqual => ">=",
            _ => null,
        };

        // Bellekteki gibi: boş değer "eşit değil" sayılır.
        if (comparison == "!=")
            return IsNullCheck(fieldExpression) is { } isNull && Compare("!=", fieldExpression, constant) is { } notEqual ? Expression.OrElse(isNull, notEqual) : null;

        return comparison is null ? null : Compare(comparison, fieldExpression, constant);
    }

    /// <summary>Tarih aralığı anahtarı (bellekteki <c>DateKey</c> ile aynı tamsayılar).</summary>
    public Expression? DateKey(Expression e, DateGrouping grouping)
    {
        if (ToDate(e) is not { } date)
            return null;

        ParameterlessDate d = new(date);
        Expression year = d.Part("Year");
        Expression month = d.Part("Month");
        Expression quarter = Expression.Divide(Expression.Add(month, Expression.Constant(2)), Expression.Constant(3));
        Expression dayOfWeek = Expression.Convert(d.Part("DayOfWeek"), typeof(int));
        Expression isoDayOfWeek = Expression.Condition(Expression.Equal(dayOfWeek, Expression.Constant(0)), Expression.Constant(7), dayOfWeek);

        Expression? key = grouping switch
        {
            DateGrouping.Year => year,
            DateGrouping.YearQuarter => Expression.Add(Expression.Multiply(year, Expression.Constant(10)), quarter),
            DateGrouping.YearMonth => Expression.Add(Expression.Multiply(year, Expression.Constant(100)), month),
            DateGrouping.Quarter => quarter,
            DateGrouping.Month => month,
            DateGrouping.DayOfWeek => isoDayOfWeek,
            DateGrouping.DayOfMonth => d.Part("Day"),
            DateGrouping.Hour => d.Part("Hour"),
            _ => null, // YearWeek (ISO hafta) ve Date ayrı
        };

        if (grouping == DateGrouping.Date)
            return DateOnly(e);

        return key is null ? null : d.NullGuard(key, typeof(int?));
    }

    /// <summary><c>Floor(x / size) * size</c>.</summary>
    public Expression? RangeKey(Expression e, decimal size)
    {
        if (KindOf(e) != Kind.Number)
            return null;

        // decimal alanlar decimal'de, diğerleri double'da (her sağlayıcı decimal aritmetiği yapamaz); anahtar istemcide decimal olur.
        Type type = Rank(e) == 3 ? typeof(decimal) : typeof(double);
        Type nullable = typeof(Nullable<>).MakeGenericType(type);
        Expression value = Expression.Convert(e, nullable);
        Expression width = Expression.Constant(Convert.ChangeType(size, type, System.Globalization.CultureInfo.InvariantCulture), type);
        Expression floor = Expression.Call(typeof(Math).GetMethod(nameof(Math.Floor), [type])!, Expression.Divide(Expression.Property(value, "Value"), width));
        return Expression.Condition(
            Expression.Equal(value, Expression.Constant(null, nullable)),
            Expression.Constant(null, nullable),
            Expression.Convert(Expression.Multiply(floor, width), nullable));
    }

    public static Expression? FirstLetter(Expression e)
    {
        if (e.Type != typeof(string))
            return null;

        Expression empty = Expression.OrElse(
            Expression.Equal(e, Expression.Constant(null, typeof(string))),
            Expression.Equal(e, Expression.Constant(string.Empty)));
        Expression first = Expression.Call(
            Expression.Call(e, typeof(string).GetMethod(nameof(string.Substring), [typeof(int), typeof(int)])!, Expression.Constant(0), Expression.Constant(1)),
            typeof(string).GetMethod(nameof(string.ToUpper), Type.EmptyTypes)!);
        return Expression.Condition(empty, Expression.Constant(null, typeof(string)), first);
    }

    /// <summary>İfadenin sayısal olup olmadığı (toplanabilir mi).</summary>
    public static bool IsNumber(Expression e) => KindOf(e) == Kind.Number;

    /// <summary>Nullable hâli (değer tipleri için).</summary>
    public static Expression Nullable(Expression e) =>
        e.Type.IsValueType && System.Nullable.GetUnderlyingType(e.Type) is null ? Expression.Convert(e, typeof(Nullable<>).MakeGenericType(e.Type)) : e;

    // ---------------------------------------------------------------- yardımcılar

    private static Kind KindOf(Expression e)
    {
        if (e is ConstantExpression { Value: null } && e.Type == typeof(object))
            return Kind.Null;

        Type t = System.Nullable.GetUnderlyingType(e.Type) ?? e.Type;
        if (t == typeof(string))
            return Kind.Text;
        if (t == typeof(DateTime))
            return Kind.Date;
        if (t == typeof(bool))
            return Kind.Bool;
        if (t == typeof(decimal) || t == typeof(double) || t == typeof(float) || t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte))
            return Kind.Number;
        return Kind.Other;
    }

    /// <summary>0: esnek (sabit), 1: tamsayı, 2: double, 3: decimal.</summary>
    private static int Rank(Expression e)
    {
        if (e is ConstantExpression { Value: decimal })
            return 0;
        Type t = System.Nullable.GetUnderlyingType(e.Type) ?? e.Type;
        return t == typeof(decimal) ? 3 : t == typeof(double) || t == typeof(float) ? 2 : 1;
    }

    private static (Expression, Expression)? Promote(Expression a, Expression b, bool division)
    {
        if (KindOf(a) != Kind.Number || KindOf(b) != Kind.Number)
            return null;

        int rank = Math.Max(Rank(a), Rank(b));
        if (rank == 0)
            rank = 3; // iki sabit
        bool fraction = (a is ConstantExpression { Value: decimal da } && da % 1 != 0) || (b is ConstantExpression { Value: decimal db } && db % 1 != 0);
        if (rank == 1 && (division || fraction))
            rank = 2;

        Type target = rank switch
        {
            3 => typeof(decimal?),
            2 => typeof(double?),
            _ => typeof(long?),
        };
        return (To(a, target), To(b, target));
    }

    private static Expression To(Expression e, Type target)
    {
        if (e.Type == target)
            return e;
        if (e is ConstantExpression { Value: decimal d })
            return Expression.Constant(Convert.ChangeType(d, System.Nullable.GetUnderlyingType(target)!, System.Globalization.CultureInfo.InvariantCulture), target);
        return Expression.Convert(e, target);
    }

    private static Expression GuardZero(Expression divisor, Expression operation)
    {
        Type type = operation.Type;
        Expression zero = Expression.Constant(Convert.ChangeType(0, System.Nullable.GetUnderlyingType(type) ?? type, System.Globalization.CultureInfo.InvariantCulture), type);
        return Expression.Condition(Expression.Equal(divisor, zero), Expression.Constant(null, type), operation);
    }

    private static Expression? Compare(string op, Expression a, Expression b)
    {
        Kind ka = KindOf(a);
        Kind kb = KindOf(b);
        Expression x;
        Expression y;

        if (ka == Kind.Number && kb == Kind.Number)
        {
            if (Promote(a, b, division: false) is not { } promoted)
                return null;
            (x, y) = promoted;
        }
        else if (ka == Kind.Date && (kb == Kind.Date || IsTextConstant(b)) || kb == Kind.Date && IsTextConstant(a))
        {
            if (ToDate(a) is not { } da || ToDate(b) is not { } db)
                return null;
            (x, y) = (da, db);
        }
        else if (ka == Kind.Text && kb == Kind.Text)
        {
            if (op is "=" or "!=")
            {
                (x, y) = (a, b);
            }
            else
            {
                // string.Compare(a, b) op 0
                Expression comparison = Expression.Call(typeof(string).GetMethod(nameof(string.Compare), [typeof(string), typeof(string)])!, a, b);
                return Guard(a, b, Operator(op, comparison, Expression.Constant(0)));
            }
        }
        else if (ka == Kind.Bool && kb == Kind.Bool)
        {
            (x, y) = (Nullable(a), Nullable(b));
        }
        else
        {
            return null;
        }

        return Guard(x, y, Operator(op, x, y));
    }

    private static Expression Operator(string op, Expression x, Expression y) =>
        op switch
        {
            "=" => Expression.Equal(x, y),
            "!=" => Expression.NotEqual(x, y),
            "<" => Expression.LessThan(x, y),
            "<=" => Expression.LessThanOrEqual(x, y),
            ">" => Expression.GreaterThan(x, y),
            _ => Expression.GreaterThanOrEqual(x, y),
        };

    /// <summary>Boşla karşılaştırma yanlış (bellekteki gibi).</summary>
    private static Expression Guard(Expression a, Expression b, Expression comparison)
    {
        Expression result = comparison;
        foreach (Expression e in new[] { b, a })
        {
            if (!e.Type.IsValueType || System.Nullable.GetUnderlyingType(e.Type) is not null)
            {
                if (e is ConstantExpression { Value: not null })
                    continue;
                result = Expression.AndAlso(Expression.NotEqual(e, Expression.Constant(null, e.Type)), result);
            }
        }

        return result;
    }

    private static bool IsTextConstant(Expression e) => e is ConstantExpression { Value: string };

    private static Expression? ToDate(Expression e)
    {
        if (e is ConstantExpression { Value: string s })
            return ReportValue.ToDate(s) is { } parsed ? Expression.Constant(parsed, typeof(DateTime?)) : null;
        return KindOf(e) == Kind.Date ? Nullable(e) : null;
    }

    private static Expression? ToBool(Expression e) =>
        e.Type == typeof(bool) ? e
        : e.Type == typeof(bool?) ? Expression.Equal(e, Expression.Constant(true, typeof(bool?)))
        : null;

    private static Expression? IsNullCheck(Expression e)
    {
        if (KindOf(e) == Kind.Null)
            return Expression.Constant(true);
        if (e.Type.IsValueType && System.Nullable.GetUnderlyingType(e.Type) is null)
            return Expression.Constant(false);
        return Expression.Equal(e, Expression.Constant(null, e.Type));
    }

    private static Expression? Coalesce(Expression a, Expression b)
    {
        if (KindOf(b) == Kind.Null)
            return a;
        if (KindOf(a) == Kind.Number && KindOf(b) == Kind.Number && Promote(a, b, false) is { } promoted)
            return Expression.Coalesce(promoted.Item1, promoted.Item2);
        if (KindOf(a) != KindOf(b))
            return null;
        Expression left = Nullable(a);
        Expression right = left.Type == b.Type ? b : Expression.Convert(b, left.Type);
        return Expression.Coalesce(left, right);
    }

    private static Expression? Iif(Expression[] args)
    {
        // dallar: tek, else: son (varsa)
        var branches = new List<Expression>();
        for (int i = 1; i < args.Length; i += 2)
            branches.Add(args[i]);
        if (args.Length % 2 == 1)
            branches.Add(args[^1]);

        IEnumerable<Expression> typed = branches.Where(b => KindOf(b) != Kind.Null);
        Kind kind = typed.Select(KindOf).Distinct().SingleOrDefault();
        if (typed.Select(KindOf).Distinct().Count() != 1 || kind == Kind.Other)
            return null;

        Type target = kind switch
        {
            Kind.Number => typed.Max(Rank) switch { 3 or 0 => typeof(decimal?), 2 => typeof(double?), _ => typeof(long?) },
            Kind.Text => typeof(string),
            Kind.Date => typeof(DateTime?),
            _ => typeof(bool?),
        };

        Expression Branch(Expression b) => KindOf(b) == Kind.Null ? Expression.Constant(null, target) : To(b, target);

        Expression result = args.Length % 2 == 1 ? Branch(args[^1]) : Expression.Constant(null, target);
        for (int i = (args.Length % 2 == 1 ? args.Length - 1 : args.Length) - 2; i >= 0; i -= 2)
        {
            if (ToBool(args[i]) is not { } condition)
                return null;
            result = Expression.Condition(condition, Branch(args[i + 1]), result);
        }

        return result;
    }

    private static Expression? DatePart(Expression e, string part)
    {
        if (ToDate(e) is not { } date)
            return null;
        var d = new ParameterlessDate(date);
        return d.NullGuard(d.Part(part), typeof(int?));
    }

    private static Expression? DateOnly(Expression e)
    {
        if (ToDate(e) is not { } date)
            return null;
        var d = new ParameterlessDate(date);
        return d.NullGuard(d.Part("Date"), typeof(DateTime?));
    }

    private static Expression? MathCall(string method, Expression e)
    {
        if (KindOf(e) != Kind.Number)
            return null;
        Type type = Rank(e) == 2 ? typeof(double) : typeof(decimal);
        Expression value = To(e, typeof(Nullable<>).MakeGenericType(type));
        MethodInfo info = typeof(Math).GetMethod(method, [type])!;
        return Expression.Condition(
            Expression.Equal(value, Expression.Constant(null, value.Type)),
            Expression.Constant(null, value.Type),
            Expression.Convert(Expression.Call(info, Expression.Property(value, "Value")), value.Type));
    }

    private static Expression? Round(Expression[] args)
    {
        if (KindOf(args[0]) != Kind.Number)
            return null;
        int digits = 0;
        if (args.Length > 1)
        {
            if (args[1] is not ConstantExpression { Value: decimal d })
                return null;
            digits = (int)d;
        }

        Type type = Rank(args[0]) == 2 ? typeof(double) : typeof(decimal);
        Expression value = To(args[0], typeof(Nullable<>).MakeGenericType(type));
        MethodInfo info = typeof(Math).GetMethod(nameof(Math.Round), [type, typeof(int), typeof(MidpointRounding)])!;
        return Expression.Condition(
            Expression.Equal(value, Expression.Constant(null, value.Type)),
            Expression.Constant(null, value.Type),
            Expression.Convert(Expression.Call(info, Expression.Property(value, "Value"), Expression.Constant(digits), Expression.Constant(MidpointRounding.AwayFromZero)), value.Type));
    }

    /// <summary>Büyük/küçük harf duyarsız (bellekteki gibi): ikisi de küçük harfe çevrilir.</summary>
    private static Expression? TextMatch(string name, Expression text, Expression pattern)
    {
        if (KindOf(text) != Kind.Text || KindOf(pattern) != Kind.Text)
            return null;

        MethodInfo lower = typeof(string).GetMethod(nameof(string.ToLower), Type.EmptyTypes)!;
        MethodInfo method = typeof(string).GetMethod(
            name == "contains" ? nameof(string.Contains) : name == "startswith" ? nameof(string.StartsWith) : nameof(string.EndsWith),
            [typeof(string)])!;
        Expression call = Expression.Call(Expression.Call(text, lower), method, Expression.Call(pattern, lower));
        return Expression.AndAlso(Expression.NotEqual(text, Expression.Constant(null, typeof(string))), call);
    }

    private static Expression? ConstantLike(Expression fieldExpression, object? value)
    {
        value = ReportValue.Normalize(value);
        return KindOf(fieldExpression) switch
        {
            Kind.Number => ReportValue.ToDecimal(value) is { } d ? Expression.Constant(d, typeof(decimal?)) as Expression : null,
            Kind.Date => ReportValue.ToDate(value) is { } t ? Expression.Constant(t, typeof(DateTime?)) : null,
            Kind.Text => value is null ? null : Expression.Constant(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture), typeof(string)),
            Kind.Bool => value is bool b ? Expression.Constant(b, typeof(bool?)) : null,
            _ => null,
        };
    }

    /// <summary>Boş olabilen tarih: parçalarını <c>.Value</c> üzerinden okur ve boşsa boş döner.</summary>
    private readonly struct ParameterlessDate
    {
        private readonly Expression _date;
        private readonly Expression _value;

        public ParameterlessDate(Expression date)
        {
            _date = date;
            _value = Expression.Property(date, "Value");
        }

        public Expression Part(string name) => Expression.Property(_value, name);

        public Expression NullGuard(Expression body, Type type) =>
            Expression.Condition(
                Expression.Equal(_date, Expression.Constant(null, typeof(DateTime?))),
                Expression.Constant(null, type),
                Expression.Convert(body, type));
    }
}
