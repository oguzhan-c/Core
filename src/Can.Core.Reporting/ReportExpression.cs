using System.Globalization;
using System.Text;

namespace Can.Core.Reporting;

/// <summary>
/// Hesaplanmış alan ve filtre ifadeleri (DevExpress'in "criteria language"ına benzer, sade bir dil). Ayrıştırılır,
/// alan adları bir kez sıra numarasına çevrilir ve her satırda ağaç değerlendirilir.
/// <list type="bullet">
/// <item>Alanlar: <c>[unitPrice]</c>; sabitler: <c>12.5</c>, <c>'metin'</c> (<c>''</c> = tek tırnak), <c>true</c>, <c>false</c>, <c>null</c></item>
/// <item>İşleçler: <c>+ - * / %</c>, <c>= != &lt;&gt; &lt; &lt;= &gt; &gt;=</c>, <c>and or not</c> (<c>&amp;&amp; || !</c>); metin + metin birleştirir</item>
/// <item>Koşul: <c>Iif(koşul, a, b)</c>, <c>Iif(k1, a, k2, b, değilse)</c>, <c>IsNull(x)</c>, <c>IsNull(x, yerine)</c>, <c>Coalesce(a, b, ...)</c></item>
/// <item>Sayı: <c>Abs Round(x[, basamak]) Floor Ceiling Power Sqrt Min(a, b) Max(a, b)</c></item>
/// <item>Tarih: <c>Year Quarter Month Day Hour DayOfWeek</c> (Pazartesi=1) <c>Week</c> (ISO) <c>Date(x)</c> (saatsiz)
/// <c>AddDays AddMonths AddYears DateDiffDay(a, b) DateDiffMonth(a, b) Today() Now()</c></item>
/// <item>Metin: <c>Len Upper Lower Trim Substring(s, başlangıç[, uzunluk]) Concat(...) Contains StartsWith EndsWith ToStr</c></item>
/// </list>
/// Boş (null) değerle aritmetik boş verir; boşla karşılaştırma yanlıştır (<c>IsNull</c> kullan).
/// </summary>
public sealed class ReportExpression
{
    private readonly Node _root;

    private ReportExpression(string text, Node root)
    {
        Text = text;
        _root = root;
    }

    public string Text { get; }

    /// <summary>İfadeyi ayrıştırır; alan adlarını <paramref name="fieldIndex"/> ile çözer (bilinmeyen alan hata).</summary>
    /// <exception cref="ReportDefinitionException">Söz dizimi hatası ya da bilinmeyen alan/fonksiyon.</exception>
    public static ReportExpression Parse(string text, Func<string, int> fieldIndex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ArgumentNullException.ThrowIfNull(fieldIndex);
        var parser = new Parser(text, fieldIndex);
        return new ReportExpression(text, parser.ParseAll());
    }

    /// <summary>Satırda değerlendirir (satır değerleri alan sırasında).</summary>
    public object? Evaluate(object?[] row, TimeProvider? timeProvider = null) =>
        _root.Evaluate(new Context(row, timeProvider ?? TimeProvider.System));

    // ---------------------------------------------------------------- değerlendirme

    private readonly record struct Context(object?[] Row, TimeProvider Time);

    private abstract class Node
    {
        public abstract object? Evaluate(in Context context);
    }

    private sealed class Literal(object? value) : Node
    {
        public override object? Evaluate(in Context context) => value;
    }

    private sealed class Field(int index) : Node
    {
        public override object? Evaluate(in Context context) => ReportValue.Normalize(context.Row[index]);
    }

    private sealed class Unary(char op, Node operand) : Node
    {
        public override object? Evaluate(in Context context)
        {
            object? value = operand.Evaluate(context);
            return op switch
            {
                '-' => ReportValue.ToDecimal(value) is { } d ? (object)(-d) : null,
                _ => value is bool b ? (object)!b : null, // not
            };
        }
    }

    private sealed class Binary(string op, Node left, Node right) : Node
    {
        public override object? Evaluate(in Context context)
        {
            if (op == "and")
                return ReportValue.IsTrue(left.Evaluate(context)) && ReportValue.IsTrue(right.Evaluate(context));
            if (op == "or")
                return ReportValue.IsTrue(left.Evaluate(context)) || ReportValue.IsTrue(right.Evaluate(context));

            object? a = left.Evaluate(context);
            object? b = right.Evaluate(context);

            switch (op)
            {
                case "=":
                    return a is not null && b is not null && ReportValue.Compare(a, b) == 0;
                case "!=":
                    return a is not null && b is not null && ReportValue.Compare(a, b) != 0;
                case "<":
                    return a is not null && b is not null && ReportValue.Compare(a, b) < 0;
                case "<=":
                    return a is not null && b is not null && ReportValue.Compare(a, b) <= 0;
                case ">":
                    return a is not null && b is not null && ReportValue.Compare(a, b) > 0;
                case ">=":
                    return a is not null && b is not null && ReportValue.Compare(a, b) >= 0;
            }

            if (op == "+" && (a is string || b is string))
                return a is null || b is null ? null : string.Concat(ToText(a), ToText(b));

            if (ReportValue.ToDecimal(a) is not { } x || ReportValue.ToDecimal(b) is not { } y)
                return null;

            return op switch
            {
                "+" => x + y,
                "-" => x - y,
                "*" => x * y,
                "/" => y == 0 ? null : x / y,
                "%" => y == 0 ? null : x % y,
                _ => throw new InvalidOperationException(op),
            };
        }
    }

    private sealed class Call(string name, Node[] args) : Node
    {
        public override object? Evaluate(in Context context)
        {
            switch (name)
            {
                // Koşullar kısa devre: yalnızca gereken dal değerlendirilir.
                case "iif":
                    for (int i = 0; i + 1 < args.Length; i += 2)
                    {
                        if (ReportValue.IsTrue(args[i].Evaluate(context)))
                            return args[i + 1].Evaluate(context);
                    }

                    return args.Length % 2 == 1 ? args[^1].Evaluate(context) : null;
                case "isnull":
                    object? first = args[0].Evaluate(context);
                    return args.Length == 1 ? first is null : first ?? args[1].Evaluate(context);
                case "coalesce":
                    foreach (Node arg in args)
                    {
                        if (arg.Evaluate(context) is { } value)
                            return value;
                    }

                    return null;
                case "today":
                    return context.Time.GetLocalNow().DateTime.Date;
                case "now":
                    return context.Time.GetLocalNow().DateTime;
            }

            var values = new object?[args.Length];
            for (int i = 0; i < args.Length; i++)
                values[i] = args[i].Evaluate(context);

            return Functions.Invoke(name, values);
        }
    }

    private static string ToText(object? value) => ReportValue.ToText(value, CultureInfo.InvariantCulture) ?? string.Empty;

    private static class Functions
    {
        /// <summary>Ad → (en az, en çok argüman). En çok -1: sınırsız.</summary>
        public static readonly Dictionary<string, (int Min, int Max)> Arity = new(StringComparer.Ordinal)
        {
            ["iif"] = (3, -1),
            ["isnull"] = (1, 2),
            ["coalesce"] = (1, -1),
            ["today"] = (0, 0),
            ["now"] = (0, 0),
            ["abs"] = (1, 1),
            ["round"] = (1, 2),
            ["floor"] = (1, 1),
            ["ceiling"] = (1, 1),
            ["power"] = (2, 2),
            ["sqrt"] = (1, 1),
            ["min"] = (2, 2),
            ["max"] = (2, 2),
            ["year"] = (1, 1),
            ["quarter"] = (1, 1),
            ["month"] = (1, 1),
            ["day"] = (1, 1),
            ["hour"] = (1, 1),
            ["dayofweek"] = (1, 1),
            ["week"] = (1, 1),
            ["date"] = (1, 1),
            ["adddays"] = (2, 2),
            ["addmonths"] = (2, 2),
            ["addyears"] = (2, 2),
            ["datediffday"] = (2, 2),
            ["datediffmonth"] = (2, 2),
            ["len"] = (1, 1),
            ["upper"] = (1, 1),
            ["lower"] = (1, 1),
            ["trim"] = (1, 1),
            ["substring"] = (2, 3),
            ["concat"] = (1, -1),
            ["contains"] = (2, 2),
            ["startswith"] = (2, 2),
            ["endswith"] = (2, 2),
            ["tostr"] = (1, 1),
        };

        public static object? Invoke(string name, object?[] a)
        {
            switch (name)
            {
                case "concat":
                    return string.Concat(a.Select(ToText));
                case "tostr":
                    return a[0] is null ? null : ToText(a[0]);
                case "len":
                    return a[0] is null ? null : (decimal)ToText(a[0]).Length;
                case "upper":
                    return (a[0] as string)?.ToUpperInvariant();
                case "lower":
                    return (a[0] as string)?.ToLowerInvariant();
                case "trim":
                    return (a[0] as string)?.Trim();
                case "substring":
                    return Substring(a);
                case "contains":
                    return a[0] is string s1 && a[1] is string p1 && s1.Contains(p1, StringComparison.OrdinalIgnoreCase);
                case "startswith":
                    return a[0] is string s2 && a[1] is string p2 && s2.StartsWith(p2, StringComparison.OrdinalIgnoreCase);
                case "endswith":
                    return a[0] is string s3 && a[1] is string p3 && s3.EndsWith(p3, StringComparison.OrdinalIgnoreCase);
            }

            if (name is "year" or "quarter" or "month" or "day" or "hour" or "dayofweek" or "week" or "date")
            {
                if (ReportValue.ToDate(a[0]) is not { } d)
                    return null;
                return name switch
                {
                    "year" => d.Year,
                    "quarter" => (d.Month + 2) / 3,
                    "month" => d.Month,
                    "day" => d.Day,
                    "hour" => d.Hour,
                    "dayofweek" => d.DayOfWeek == DayOfWeek.Sunday ? 7m : (decimal)(int)d.DayOfWeek,
                    "week" => ISOWeek.GetWeekOfYear(d),
                    _ => (object)d.Date,
                } switch
                {
                    int i => (decimal)i,
                    var other => other,
                };
            }

            if (name is "adddays" or "addmonths" or "addyears")
            {
                if (ReportValue.ToDate(a[0]) is not { } d || ReportValue.ToDecimal(a[1]) is not { } n)
                    return null;
                return name switch
                {
                    "adddays" => d.AddDays((double)n),
                    "addmonths" => d.AddMonths((int)n),
                    _ => d.AddYears((int)n),
                };
            }

            if (name is "datediffday" or "datediffmonth")
            {
                if (ReportValue.ToDate(a[0]) is not { } from || ReportValue.ToDate(a[1]) is not { } to)
                    return null;
                return name == "datediffday"
                    ? (decimal)(to.Date - from.Date).TotalDays
                    : (decimal)(((to.Year - from.Year) * 12) + to.Month - from.Month);
            }

            // sayısal
            decimal?[] n2 = a.Select(ReportValue.ToDecimal).ToArray();
            if (n2.Any(x => x is null))
                return null;

            return name switch
            {
                "abs" => Math.Abs(n2[0]!.Value),
                "round" => Math.Round(n2[0]!.Value, a.Length > 1 ? (int)n2[1]!.Value : 0, MidpointRounding.AwayFromZero),
                "floor" => Math.Floor(n2[0]!.Value),
                "ceiling" => Math.Ceiling(n2[0]!.Value),
                "power" => ToDecimalOrNull(Math.Pow((double)n2[0]!.Value, (double)n2[1]!.Value)),
                "sqrt" => n2[0] < 0 ? null : ToDecimalOrNull(Math.Sqrt((double)n2[0]!.Value)),
                "min" => Math.Min(n2[0]!.Value, n2[1]!.Value),
                "max" => Math.Max(n2[0]!.Value, n2[1]!.Value),
                _ => throw new InvalidOperationException(name),
            };
        }

        private static object? Substring(object?[] a)
        {
            if (a[0] is not string s || ReportValue.ToDecimal(a[1]) is not { } start)
                return null;
            int from = Math.Clamp((int)start, 0, s.Length);
            int length = a.Length > 2 && ReportValue.ToDecimal(a[2]) is { } l ? Math.Clamp((int)l, 0, s.Length - from) : s.Length - from;
            return s.Substring(from, length);
        }

        private static decimal? ToDecimalOrNull(double value) => ReportValue.ToDecimal(value);
    }

    // ---------------------------------------------------------------- ayrıştırma

    private sealed class Parser(string text, Func<string, int> fieldIndex)
    {
        private int _position;

        public Node ParseAll()
        {
            Node node = ParseOr();
            SkipWhitespace();
            if (_position < text.Length)
                throw Error($"beklenmeyen '{text[_position]}'");
            return node;
        }

        private Node ParseOr()
        {
            Node left = ParseAnd();
            while (MatchWord("or") || Match("||"))
                left = new Binary("or", left, ParseAnd());
            return left;
        }

        private Node ParseAnd()
        {
            Node left = ParseNot();
            while (MatchWord("and") || Match("&&"))
                left = new Binary("and", left, ParseNot());
            return left;
        }

        private Node ParseNot()
        {
            if (MatchWord("not") || (Peek('!') && !PeekAt(1, '=') && Match("!")))
                return new Unary('!', ParseNot());
            return ParseComparison();
        }

        private Node ParseComparison()
        {
            Node left = ParseAdditive();
            SkipWhitespace();
            string? op = Match("<=") ? "<=" : Match(">=") ? ">=" : Match("<>") || Match("!=") ? "!=" : Match("==") || Match("=") ? "=" : Match("<") ? "<" : Match(">") ? ">" : null;
            return op is null ? left : new Binary(op, left, ParseAdditive());
        }

        private Node ParseAdditive()
        {
            Node left = ParseMultiplicative();
            while (true)
            {
                if (Match("+"))
                    left = new Binary("+", left, ParseMultiplicative());
                else if (Match("-"))
                    left = new Binary("-", left, ParseMultiplicative());
                else
                    return left;
            }
        }

        private Node ParseMultiplicative()
        {
            Node left = ParseUnary();
            while (true)
            {
                if (Match("*"))
                    left = new Binary("*", left, ParseUnary());
                else if (Match("/"))
                    left = new Binary("/", left, ParseUnary());
                else if (Match("%"))
                    left = new Binary("%", left, ParseUnary());
                else
                    return left;
            }
        }

        private Node ParseUnary()
        {
            if (Match("-"))
                return new Unary('-', ParseUnary());
            if (Match("+"))
                return ParseUnary();
            return ParsePrimary();
        }

        private Node ParsePrimary()
        {
            SkipWhitespace();
            if (_position >= text.Length)
                throw Error("ifade eksik");

            char c = text[_position];

            if (c == '(')
            {
                _position++;
                Node inner = ParseOr();
                Expect(')');
                return inner;
            }

            if (c == '[')
            {
                int end = text.IndexOf(']', _position + 1);
                if (end < 0)
                    throw Error("']' eksik");
                string name = text[(_position + 1)..end].Trim();
                _position = end + 1;
                int index = fieldIndex(name);
                if (index < 0)
                    throw Error($"'{name}' alanı yok");
                return new Field(index);
            }

            if (c == '\'')
                return new Literal(ReadString());

            if (char.IsAsciiDigit(c) || (c == '.' && _position + 1 < text.Length && char.IsAsciiDigit(text[_position + 1])))
                return new Literal(ReadNumber());

            if (char.IsLetter(c) || c == '_')
            {
                string word = ReadWord();
                string lower = word.ToLowerInvariant();
                switch (lower)
                {
                    case "true":
                        return new Literal(true);
                    case "false":
                        return new Literal(false);
                    case "null":
                        return new Literal(null);
                }

                if (!Functions.Arity.TryGetValue(lower, out (int Min, int Max) arity))
                    throw Error($"'{word}' fonksiyonu yok");

                Expect('(');
                var args = new List<Node>();
                SkipWhitespace();
                if (!Match(")"))
                {
                    do
                    {
                        args.Add(ParseOr());
                    }
                    while (Match(","));
                    Expect(')');
                }

                if (args.Count < arity.Min || (arity.Max >= 0 && args.Count > arity.Max))
                    throw Error($"'{word}' {FormatArity(arity)} argüman alır, {args.Count} verildi");

                return new Call(lower, args.ToArray());
            }

            throw Error($"beklenmeyen '{c}'");
        }

        private static string FormatArity((int Min, int Max) arity) =>
            arity.Max < 0 ? $"en az {arity.Min}" : arity.Min == arity.Max ? $"{arity.Min}" : $"{arity.Min}-{arity.Max}";

        private string ReadString()
        {
            var builder = new StringBuilder();
            _position++; // '
            while (_position < text.Length)
            {
                char c = text[_position++];
                if (c == '\'')
                {
                    if (_position < text.Length && text[_position] == '\'')
                    {
                        builder.Append('\'');
                        _position++;
                        continue;
                    }

                    return builder.ToString();
                }

                builder.Append(c);
            }

            throw Error("metin kapanmamış (')");
        }

        private decimal ReadNumber()
        {
            int start = _position;
            while (_position < text.Length && (char.IsAsciiDigit(text[_position]) || text[_position] == '.'))
                _position++;
            string number = text[start.._position];
            return decimal.TryParse(number, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal value)
                ? value
                : throw Error($"geçersiz sayı '{number}'");
        }

        private string ReadWord()
        {
            int start = _position;
            while (_position < text.Length && (char.IsLetterOrDigit(text[_position]) || text[_position] == '_'))
                _position++;
            return text[start.._position];
        }

        private bool MatchWord(string word)
        {
            SkipWhitespace();
            if (_position + word.Length > text.Length
                || !text.AsSpan(_position, word.Length).Equals(word, StringComparison.OrdinalIgnoreCase))
                return false;
            int after = _position + word.Length;
            if (after < text.Length && (char.IsLetterOrDigit(text[after]) || text[after] == '_'))
                return false; // "order" "or" değildir
            _position = after;
            return true;
        }

        private bool Match(string token)
        {
            SkipWhitespace();
            if (!text.AsSpan(_position).StartsWith(token, StringComparison.Ordinal))
                return false;
            _position += token.Length;
            return true;
        }

        private bool Peek(char c)
        {
            SkipWhitespace();
            return _position < text.Length && text[_position] == c;
        }

        private bool PeekAt(int offset, char c) => _position + offset < text.Length && text[_position + offset] == c;

        private void Expect(char c)
        {
            if (!Match(c.ToString()))
                throw Error($"'{c}' bekleniyordu");
        }

        private void SkipWhitespace()
        {
            while (_position < text.Length && char.IsWhiteSpace(text[_position]))
                _position++;
        }

        private ReportDefinitionException Error(string message) =>
            new($"İfade hatalı ({message}, konum {_position + 1}): {text}");
    }
}

/// <summary>Rapor tanımı geçersiz (bilinmeyen alan, hatalı ifade, desteklenmeyen birleşim ...).</summary>
public sealed class ReportDefinitionException : Exception
{
    public ReportDefinitionException() { }

    public ReportDefinitionException(string message)
        : base(message) { }

    public ReportDefinitionException(string message, Exception innerException)
        : base(message, innerException) { }
}
