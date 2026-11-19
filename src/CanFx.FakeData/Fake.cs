namespace CanFx.FakeData;

/// <summary>
/// Sahte veri üreticisinin giriş noktası. Aynı tohum ve referans tarihiyle her çalıştırmada aynı değerleri üretir.
/// </summary>
/// <remarks>
/// Bir değeri başka değerlerden bağımsız tutmak için <see cref="For"/> ya da <see cref="Scope"/> ile kendi tohumuna sahip
/// alt üretici al: <c>fake.For("Product", 3, "Name").Commerce.ProductName()</c>. Böylece bir alan eklemek ya da kural
/// sırasını değiştirmek diğer alanların değerlerini kaydırmaz.
/// </remarks>
/// <example>
/// <code>
/// var fake = new Fake(seed: 42);
/// string name = fake.For("Customer", 0, "Name").Person.FullName();   // her zaman aynı
/// decimal price = fake.For("Product", 7, "Price").Commerce.Price(50, 5000);
/// </code>
/// </example>
public sealed class Fake
{
    /// <summary>Varsayılan tohum.</summary>
    public const ulong DefaultSeed = 20260101;

    /// <summary>Varsayılan referans tarihi: tarihler buna göre hesaplanır (şimdiki zaman hiç kullanılmaz).</summary>
    public static readonly DateTimeOffset DefaultReferenceDate = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private PersonFaker? _person;
    private AddressFaker? _address;
    private PhoneFaker? _phone;
    private InternetFaker? _internet;
    private CompanyFaker? _company;
    private CommerceFaker? _commerce;
    private TextFaker? _text;
    private DateFaker? _date;
    private FinanceFaker? _finance;
    private IdentifierFaker? _identifiers;

    public Fake(ulong seed = DefaultSeed, DateTimeOffset? referenceDate = null)
    {
        Seed = seed;
        ReferenceDate = referenceDate ?? DefaultReferenceDate;
        Random = new FakeRandom(FakeHash.Mix(seed));
    }

    public ulong Seed { get; }

    public DateTimeOffset ReferenceDate { get; }

    /// <summary>Bu üreticinin sayı kaynağı (her çağrı bir sonraki değeri verir).</summary>
    public FakeRandom Random { get; }

    public PersonFaker Person => _person ??= new PersonFaker(this);

    public AddressFaker Address => _address ??= new AddressFaker(this);

    public PhoneFaker Phone => _phone ??= new PhoneFaker(this);

    public InternetFaker Internet => _internet ??= new InternetFaker(this);

    public CompanyFaker Company => _company ??= new CompanyFaker(this);

    public CommerceFaker Commerce => _commerce ??= new CommerceFaker(this);

    public TextFaker Text => _text ??= new TextFaker(this);

    public DateFaker Date => _date ??= new DateFaker(this);

    public FinanceFaker Finance => _finance ??= new FinanceFaker(this);

    public IdentifierFaker Identifiers => _identifiers ??= new IdentifierFaker(this);

    /// <summary>Verilen anahtarlardan türetilen tohumla yeni, bağımsız bir üretici (aynı referans tarihi).</summary>
    public Fake Scope(params object?[] keys) => new(FakeHash.Of(Seed, keys), ReferenceDate);

    /// <summary>Bir entity'nin bir kaydının bir alanı için bağımsız üretici.</summary>
    public Fake For(string entity, int index, string field) => Scope(entity, index, field);
}
