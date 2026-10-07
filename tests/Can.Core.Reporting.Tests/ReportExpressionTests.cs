namespace Can.Core.Reporting.Tests;

public class ReportExpressionTests
{
    private static readonly string[] Names = ["a", "n", "d", "s"];

    private static object? Eval(string text, object? a = null, object? n = null, object? d = null, object? s = null) =>
        ReportExpression.Parse(text, name => Array.IndexOf(Names, name.ToLowerInvariant())).Evaluate([a, n, d, s]);

    [Theory]
    [InlineData("1 + 2 * 3", 7)]
    [InlineData("(1 + 2) * 3", 9)]
    [InlineData("10 / 4", 2.5)]
    [InlineData("10 % 4", 2)]
    [InlineData("-[a] + 1", -4)]
    [InlineData("Round(2.345, 2)", 2.35)]
    [InlineData("Abs(-3) + Floor(2.7) + Ceiling(2.1)", 8)]
    [InlineData("Max(3, 7) - Min(3, 7)", 4)]
    [InlineData("Power(2, 10)", 1024)]
    public void Arithmetic(string text, double expected) => Assert.Equal((decimal)expected, Eval(text, a: 5));

    [Fact]
    public void Division_by_zero_and_null_arithmetic_give_null()
    {
        Assert.Null(Eval("10 / 0"));
        Assert.Null(Eval("[n] + 1"));
    }

    [Fact]
    public void Conditions_and_logic()
    {
        Assert.Equal("büyük", Eval("Iif([a] > 3, 'büyük', 'küçük')", a: 5));
        Assert.Equal("orta", Eval("Iif([a] > 10, 'büyük', [a] > 3, 'orta', 'küçük')", a: 5));
        Assert.Equal(1m, Eval("IsNull([n], 0) + 1"));
        Assert.Equal(true, Eval("IsNull([n])"));
        Assert.Equal(true, Eval("[a] > 3 and not ([a] = 4)", a: 5));
        Assert.Equal(true, Eval("[a] < 3 || [a] >= 5", a: 5));
        Assert.Equal(false, Eval("![a] = 5", a: 5)); // ! karşılaştırmanın tamamına uygulanır
        Assert.Equal(false, Eval("[n] = 1")); // boşla karşılaştırma yanlış
        Assert.Equal(7m, Eval("Coalesce([n], [a], 3)", a: 7));
    }

    [Fact]
    public void Text_functions()
    {
        Assert.Equal("a'bc", Eval("'a''b' + 'c'"));
        Assert.Equal("Mer", Eval("Substring([s], 0, 3)", s: "Merhaba"));
        Assert.Equal("MERHABA", Eval("Upper([s])", s: "Merhaba"));
        Assert.Equal(7m, Eval("Len([s])", s: "Merhaba"));
        Assert.Equal(true, Eval("Contains([s], 'HAB') and StartsWith([s], 'mer') and EndsWith([s], 'ba')", s: "Merhaba"));
        Assert.Equal("x-5", Eval("Concat('x', '-', [a])", a: 5));
    }

    [Fact]
    public void Date_functions()
    {
        var date = new DateTime(2024, 5, 17, 14, 30, 0); // cuma
        Assert.Equal(2024m, Eval("Year([d])", d: date));
        Assert.Equal(2m, Eval("Quarter([d])", d: date));
        Assert.Equal(5m, Eval("DayOfWeek([d])", d: date));
        Assert.Equal(14m, Eval("Hour([d])", d: date));
        Assert.Equal(20m, Eval("Week([d])", d: date));
        Assert.Equal(new DateTime(2024, 5, 17), Eval("Date([d])", d: date));
        Assert.Equal(new DateTime(2024, 7, 17, 14, 30, 0), Eval("AddMonths([d], 2)", d: date));
        Assert.Equal(14m, Eval("DateDiffDay([d], AddDays([d], 14))", d: date));
        Assert.Equal(true, Eval("[d] > '2024-01-01'", d: date)); // metin tarih olarak karşılaştırılır
    }

    [Theory]
    [InlineData("[x] + 1", "'x' alanı yok")]
    [InlineData("Foo(1)", "'Foo' fonksiyonu yok")]
    [InlineData("Round()", "argüman alır")]
    [InlineData("1 +", "ifade eksik")]
    [InlineData("'abc", "kapanmamış")]
    [InlineData("(1 + 2", "')' bekleniyordu")]
    [InlineData("1 2", "beklenmeyen")]
    public void Errors_are_explained(string text, string message)
    {
        ReportDefinitionException ex = Assert.Throws<ReportDefinitionException>(() => Eval(text));
        Assert.Contains(message, ex.Message, StringComparison.Ordinal);
    }
}
