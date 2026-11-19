using System.Text.RegularExpressions;

namespace CanFx.FakeData.Tests;

public class SampleProduct
{
    protected SampleProduct()
    {
    }

    public Guid Id { get; protected set; }

    public string Name { get; set; } = string.Empty;

    public string Slug { get; set; } = string.Empty;

    public decimal Price { get; set; }

    public string? Description { get; set; }

    public DateTimeOffset CreatedAt { get; private set; }
}

public class DeterminismTests
{
    [Fact]
    public void Same_seed_gives_same_values()
    {
        Assert.Equal(new Fake(42).Person.FullName(), new Fake(42).Person.FullName());
        Assert.Equal(new Fake(42).For("Product", 3, "Name").Commerce.ProductName(), new Fake(42).For("Product", 3, "Name").Commerce.ProductName());
    }

    [Fact]
    public void Different_scopes_give_independent_values()
    {
        var fake = new Fake(42);
        string[] names = Enumerable.Range(0, 20).Select(i => fake.For("Customer", i, "Name").Person.FullName()).ToArray();
        Assert.True(names.Distinct().Count() > 10);
    }

    [Fact]
    public void Hash_is_stable_across_runs()
    {
        // Sabit değer: algoritma değişirse (ve HasData verisi kayarsa) bu test kırılır.
        Assert.Equal(FakeHash.Of(1, "Product", 0, "Name"), FakeHash.Of(1, "Product", 0, "Name"));
        Assert.NotEqual(FakeHash.Of(1, "ab", "c"), FakeHash.Of(1, "a", "bc"));
        Assert.NotEqual(FakeHash.Of(1, "Product"), FakeHash.Of(2, "Product"));
    }

    [Fact]
    public void Dates_depend_on_reference_date_not_now()
    {
        var reference = new DateTimeOffset(2020, 6, 1, 0, 0, 0, TimeSpan.Zero);
        DateTimeOffset past = new Fake(7, reference).Date.Past(30);
        Assert.InRange(past, reference.AddDays(-30), reference);
        Assert.Equal(past, new Fake(7, reference).Date.Past(30));
    }

    [Fact]
    public void Adding_a_rule_does_not_change_other_fields()
    {
        var fake = new Fake(99);
        IReadOnlyList<FakeRow> before = new FakeBuilder<SampleProduct>(fake)
            .Rule(p => p.Name, f => f.Commerce.ProductName())
            .GenerateRows(10);
        IReadOnlyList<FakeRow> after = new FakeBuilder<SampleProduct>(fake)
            .Rule(p => p.Price, f => f.Commerce.Price(10, 100))   // önce yeni bir kural
            .Rule(p => p.Name, f => f.Commerce.ProductName())
            .GenerateRows(10);

        Assert.Equal(before.Select(r => r["Name"]), after.Select(r => r["Name"]));
    }
}

public class BuilderTests
{
    [Fact]
    public void Builds_objects_with_protected_and_private_setters()
    {
        IReadOnlyList<SampleProduct> products = new FakeBuilder<SampleProduct>(new Fake(1))
            .Rule(p => p.Id, f => f.Identifiers.GuidV7())
            .Rule(p => p.Name, f => f.Commerce.ProductName())
            .Rule(p => p.Slug, (f, row) => FakeFormat.Slug(row.Get<string>("Name")))
            .Rule(p => p.Price, f => f.Commerce.Price(50, 5000))
            .Rule(p => p.Description, f => f.Text.Sentence().OrNull(f, 0.5))
            .Rule(p => p.CreatedAt, f => f.Date.Past())
            .Generate(25);

        Assert.Equal(25, products.Count);
        Assert.All(products, p =>
        {
            Assert.NotEqual(Guid.Empty, p.Id);
            Assert.Equal(FakeFormat.Slug(p.Name), p.Slug);
            Assert.InRange(p.Price, 50m, 5000m);
            Assert.True(p.CreatedAt <= Fake.DefaultReferenceDate);
        });
        Assert.Contains(products, p => p.Description is null);
        Assert.Contains(products, p => p.Description is not null);
        Assert.Equal(25, products.Select(p => p.Id).Distinct().Count());
    }

    [Fact]
    public void Rule_referring_to_a_later_member_explains_the_order()
    {
        var builder = new FakeBuilder<SampleProduct>(new Fake(1))
            .Rule(p => p.Slug, (f, row) => FakeFormat.Slug(row.Get<string>("Name")))
            .Rule(p => p.Name, f => f.Commerce.ProductName());

        Assert.Throws<KeyNotFoundException>(() => builder.Generate(1));
    }

    [Fact]
    public void Start_index_continues_the_same_sequence()
    {
        var builder = new FakeBuilder<SampleProduct>(new Fake(5)).Rule(p => p.Name, f => f.Commerce.ProductName());
        IReadOnlyList<FakeRow> all = builder.GenerateRows(10);
        IReadOnlyList<FakeRow> tail = builder.GenerateRows(5, startIndex: 5);
        Assert.Equal(all.Skip(5).Select(r => r["Name"]), tail.Select(r => r["Name"]));
    }
}

public class DataSetTests
{
    private readonly Fake _fake = new(2026);

    [Fact]
    public void Iban_has_valid_check_digits()
    {
        for (int i = 0; i < 50; i++)
        {
            string iban = _fake.Scope("iban", i).Finance.Iban();
            Assert.Matches("^TR[0-9]{24}$", iban);
            Assert.Equal(1, FinanceFaker.Mod97(iban[4..] + iban[..4]));
        }
    }

    [Fact]
    public void Ean13_has_valid_check_digit()
    {
        string code = _fake.Commerce.Ean13();
        Assert.Equal(13, code.Length);
        int sum = code.Take(12).Select((c, i) => (c - '0') * (i % 2 == 0 ? 1 : 3)).Sum();
        Assert.Equal((10 - sum % 10) % 10, code[12] - '0');
    }

    [Fact]
    public void Guid_v7_has_version_and_reference_time()
    {
        Guid id = _fake.Identifiers.GuidV7();
        Assert.Equal(7, id.Version);
        Assert.StartsWith(Fake.DefaultReferenceDate.ToUnixTimeMilliseconds().ToString("x12")[..8], id.ToString("N"));
    }

    [Fact]
    public void Turkish_values_look_right()
    {
        Assert.Matches(@"^\+90 5\d{2} \d{3} \d{2} \d{2}$", _fake.Phone.Mobile());
        Assert.Matches(@"^[a-z0-9._]+@example\.(com|org|net)$", _fake.Internet.Email("Şükrü", "Öztürk"));
        Assert.Matches("^09[0-9]{3}$", _fake.Address.ZipCode("Aydın"));
        Assert.EndsWith(".", _fake.Text.Sentence());
        decimal price = _fake.Commerce.Price(100, 2000);
        Assert.Contains(price - Math.Floor(price), new[] { 0.90m, 0.99m, 0m });
    }

    [Theory]
    [InlineData("Pamuklu Slim Gömlek", "pamuklu-slim-gomlek")]
    [InlineData("  Işık & Çiçek!  ", "isik-cicek")]
    [InlineData("İstanbul Şubesi", "istanbul-subesi")]
    public void Slug_handles_turkish_characters(string input, string expected) =>
        Assert.Equal(expected, FakeFormat.Slug(input));

    [Fact]
    public void Capitalize_uses_turkish_rules()
    {
        Assert.Equal("İyi", FakeFormat.Capitalize("iyi"));
        Assert.Equal("Işık", FakeFormat.Capitalize("ışık"));
    }
}

public class KindTests
{
    [Theory]
    [InlineData("Email", "string", "Email")]
    [InlineData("FirstName", "string", "FirstName")]
    [InlineData("Slug", "string", "Slug(Name)")]
    [InlineData("BasePrice", "decimal", "Price(10, 5000)")]
    [InlineData("CommissionRate", "decimal", "Decimal(0, 0.3, 2)")]
    [InlineData("IsPublished", "bool", "Bool(0.8)")]
    [InlineData("CreatedAt", "DateTimeOffset", "Past(365)")]
    [InlineData("ValidUntil", "DateTimeOffset", "Future(365)")]
    [InlineData("Rating", "int", "Int(1, 5)")]
    [InlineData("Id", "Guid", "GuidV7")]
    [InlineData("Description", "string?", "Paragraph(2)")]
    [InlineData("PasswordHash", "string", "Null")]
    public void Guesses_kind_from_name_and_type(string member, string type, string expected) =>
        Assert.Equal(expected, FakeKinds.Guess(member, type));

    [Fact]
    public void Enum_values_become_pick() =>
        Assert.Equal("Pick(Paid, Shipped)", FakeKinds.Guess("Status", "OrderStatus", ["Paid", "Shipped"]));

    [Fact]
    public void Parses_arguments_with_quotes()
    {
        (string name, string[] args) = FakeKinds.Parse("Fixed(\"a, b\")");
        Assert.Equal("Fixed", name);
        Assert.Equal(new[] { "a, b" }, args);
        (string price, string[] priceArgs) = FakeKinds.Parse("Price(50, 5000)");
        Assert.Equal("Price", price);
        Assert.Equal(new[] { "50", "5000" }, priceArgs);
    }

    [Fact]
    public void Generates_and_converts_values()
    {
        var fake = new Fake(3);
        var row = new FakeRow(0);
        row.Set("Name", "Pamuklu Gömlek");

        Assert.Equal("pamuklu-gomlek", FakeKinds.Generate("Slug(Name)", fake, row));
        Assert.InRange((decimal)FakeKinds.Generate("Price(50, 60)", fake)!, 50m, 60m);
        Assert.Contains((string)FakeKinds.Generate("Pick(S, M, L)", fake)!, new[] { "S", "M", "L" });
        Assert.Null(FakeKinds.Generate("Null", fake));
        Assert.IsType<DateTime>(FakeKinds.ConvertTo(fake.Date.Past(), "DateTime"));
        Assert.Equal(5L, FakeKinds.ConvertTo(5, "long"));
        Assert.Throws<FormatException>(() => FakeKinds.Generate("NoSuchKind", fake));
        Assert.True(FakeKinds.IsKnown("Int(1, 2)"));
        Assert.False(FakeKinds.IsKnown("Int(1, 2"));
    }

    [Fact]
    public void Every_listed_kind_generates_without_error()
    {
        var fake = new Fake(11);
        var row = new FakeRow(0);
        foreach (FakeKindInfo kind in FakeKinds.All)
        {
            string spec = kind.Name switch
            {
                "Pick" => "Pick(A, B)",
                "Fixed" => "Fixed(x)",
                _ => kind.Name,
            };
            _ = FakeKinds.Generate(spec, fake.Scope(kind.Name), row);
        }
    }
}
