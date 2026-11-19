using CanFx.FakeData.Data;

namespace CanFx.FakeData;

public enum FakeGender
{
    Female,
    Male,
}

/// <summary>Veri kümelerinin ortak tabanı.</summary>
public abstract class FakerBase
{
    private protected FakerBase(Fake fake) => Fake = fake;

    protected Fake Fake { get; }

    protected FakeRandom Random => Fake.Random;
}

/// <summary>Kişi: ad, soyad.</summary>
public sealed class PersonFaker : FakerBase
{
    internal PersonFaker(Fake fake) : base(fake)
    {
    }

    public FakeGender Gender() => Random.Bool() ? FakeGender.Female : FakeGender.Male;

    public string FirstName(FakeGender? gender = null) =>
        Random.Pick((gender ?? Gender()) == FakeGender.Female ? TurkishData.FemaleFirstNames : TurkishData.MaleFirstNames);

    public string LastName() => Random.Pick(TurkishData.LastNames);

    public string FullName(FakeGender? gender = null) => $"{FirstName(gender)} {LastName()}";
}

/// <summary>Adres: il, ilçe, mahalle, sokak, posta kodu.</summary>
public sealed class AddressFaker : FakerBase
{
    internal AddressFaker(Fake fake) : base(fake)
    {
    }

    public string City() => Random.Pick(TurkishData.Cities);

    /// <summary>İlçe; il verilmezse büyük illerden biri seçilir.</summary>
    public string District(string? city = null)
    {
        city ??= Random.Pick(TurkishData.Districts.Keys.ToList());
        return Random.Pick(TurkishData.Districts.TryGetValue(city, out string[]? districts) ? districts : TurkishData.GenericDistricts);
    }

    public string Neighborhood() => $"{Random.Pick(TurkishData.NeighborhoodRoots)} Mahallesi";

    public string Street() => $"{Random.Pick(TurkishData.StreetRoots)} {Random.Pick(TurkishData.StreetTypes)}";

    public string BuildingNumber() => Random.Int(1, 180).ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>5 haneli posta kodu; il verilirse ilk iki hane plaka kodudur.</summary>
    public string ZipCode(string? city = null)
    {
        int plate = city is null ? 0 : TurkishData.PlateCode(city);
        if (plate <= 0)
            plate = Random.Int(1, 81);
        return $"{plate:00}{Random.Int(0, 999):000}";
    }

    /// <summary>"Ata Mahallesi Gül Sokak No: 12 Daire: 4".</summary>
    public string StreetAddress() =>
        $"{Neighborhood()} {Street()} No: {BuildingNumber()} Daire: {Random.Int(1, 24)}";

    /// <summary>"Ata Mahallesi Gül Sokak No: 12 Daire: 4, 09100 Efeler / Aydın".</summary>
    public string FullAddress()
    {
        string city = Random.Pick(TurkishData.Districts.Keys.ToList());
        return $"{StreetAddress()}, {ZipCode(city)} {District(city)} / {city}";
    }
}

/// <summary>Telefon: cep (+90 5xx) ve sabit hat.</summary>
public sealed class PhoneFaker : FakerBase
{
    internal PhoneFaker(Fake fake) : base(fake)
    {
    }

    /// <summary>"+90 532 418 27 65".</summary>
    public string Mobile()
    {
        string digits = Random.Digits(7);
        return $"+90 {Random.Pick(TurkishData.MobilePrefixes)} {digits[..3]} {digits[3..5]} {digits[5..]}";
    }

    /// <summary>"+905324182765".</summary>
    public string MobileE164() => $"+90{Random.Pick(TurkishData.MobilePrefixes)}{Random.Digits(7)}";

    /// <summary>"+90 256 213 45 67" (il verilirse onun alan kodu).</summary>
    public string Landline(string? city = null)
    {
        string area = city is not null && TurkishData.AreaCodes.TryGetValue(city, out string? code)
            ? code
            : Random.Pick(TurkishData.AreaCodes.Values.ToList());
        string digits = $"{Random.Int(2, 4)}{Random.Digits(6)}";
        return $"+90 {area} {digits[..3]} {digits[3..5]} {digits[5..]}";
    }
}

/// <summary>İnternet: e-posta, kullanıcı adı, adres. Alan adları ayrılmış örnek alanlardır (example.com ...).</summary>
public sealed class InternetFaker : FakerBase
{
    private static readonly string[] Domains = ["example.com", "example.org", "example.net"];

    internal InternetFaker(Fake fake) : base(fake)
    {
    }

    /// <summary>"elif.yildiz17@example.com"; ad verilmezse üretilir.</summary>
    public string Email(string? firstName = null, string? lastName = null) =>
        $"{UserName(firstName, lastName)}@{Random.Pick(Domains)}";

    /// <summary>"elif.yildiz17".</summary>
    public string UserName(string? firstName = null, string? lastName = null)
    {
        firstName ??= Fake.Person.FirstName();
        lastName ??= Fake.Person.LastName();
        string separator = Random.Pick([".", "_", ""]);
        return $"{FakeFormat.Ascii(firstName).ToLowerInvariant()}{separator}{FakeFormat.Ascii(lastName).ToLowerInvariant()}{Random.Int(1, 99)}"
            .Replace(" ", string.Empty, StringComparison.Ordinal);
    }

    public string Domain() => $"{FakeFormat.Slug(Fake.Company.Word())}.{Random.Pick(Domains)}";

    public string Url() => $"https://www.{Domain()}";
}

/// <summary>Şirket: "Anadolu Tekstil San. ve Tic. Ltd. Şti.".</summary>
public sealed class CompanyFaker : FakerBase
{
    internal CompanyFaker(Fake fake) : base(fake)
    {
    }

    public string Name() => Random.Bool(0.5)
        ? $"{Fake.Person.LastName()} {Sector()} {Suffix()}"
        : $"{Word()} {Sector()} {Suffix()}";

    public string Word() => Random.Pick(TurkishData.CompanyWords);

    public string Sector() => Random.Pick(TurkishData.CompanySectors);

    public string Suffix() => Random.Pick(TurkishData.CompanySuffixes);
}

/// <summary>Ticaret: ürün adı, reyon, renk, beden, fiyat, SKU, barkod.</summary>
public sealed class CommerceFaker : FakerBase
{
    internal CommerceFaker(Fake fake) : base(fake)
    {
    }

    /// <summary>"Pamuklu Slim Gömlek".</summary>
    public string ProductName()
    {
        string noun = Random.Pick(TurkishData.ProductNouns);
        return Random.Int(0, 2) switch
        {
            0 => $"{Random.Pick(TurkishData.ProductAdjectives)} {noun}",
            1 => $"{Random.Pick(TurkishData.ProductMaterials)} {noun}",
            _ => $"{Random.Pick(TurkishData.ProductMaterials)} {Random.Pick(TurkishData.ProductAdjectives)} {noun}",
        };
    }

    public string Department() => Random.Pick(TurkishData.Departments);

    public string Color() => Random.Pick(TurkishData.Colors);

    public string Size() => Random.Pick(TurkishData.ClothingSizes);

    /// <summary>Mağaza fiyatı: 349,90 / 1.299,99 gibi ,90 ya da ,99 ile biter.</summary>
    public decimal Price(decimal min = 10, decimal max = 1000)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(min, max);
        decimal whole = Math.Floor(Random.Decimal(min, max, 0));
        decimal price = whole + Random.Pick([0.90m, 0.99m, 0.00m]);
        return Math.Clamp(price, min, max);
    }

    /// <summary>"SKU-48213".</summary>
    public string Sku(string prefix = "SKU") => $"{prefix}-{Random.Digits(5)}";

    /// <summary>Kontrol hanesi geçerli EAN-13 barkod (869 ile başlar: Türkiye öneki).</summary>
    public string Ean13()
    {
        string body = "869" + Random.Digits(9);
        int sum = 0;
        for (int i = 0; i < 12; i++)
            sum += (body[i] - '0') * (i % 2 == 0 ? 1 : 3);
        return body + ((10 - sum % 10) % 10).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}

/// <summary>Türkçe kelimelerden metin (lorem ipsum yerine).</summary>
public sealed class TextFaker : FakerBase
{
    internal TextFaker(Fake fake) : base(fake)
    {
    }

    public string Word() => Random.Pick(TurkishData.Words);

    public string Words(int count) => string.Join(' ', Enumerable.Range(0, count).Select(_ => Word()));

    /// <summary>Büyük harfle başlayan, noktayla biten cümle.</summary>
    public string Sentence(int minWords = 5, int maxWords = 12) =>
        FakeFormat.Capitalize(Words(Random.Int(minWords, maxWords))) + ".";

    public string Paragraph(int sentences = 3) =>
        string.Join(' ', Enumerable.Range(0, sentences).Select(_ => Sentence()));

    /// <summary>Her kelimesi büyük harfle başlayan 2–4 kelimelik başlık.</summary>
    public string Title() =>
        string.Join(' ', Enumerable.Range(0, Random.Int(2, 4)).Select(_ => FakeFormat.Capitalize(Word())));
}

/// <summary>Tarih: hepsi referans tarihine göre, saniyeye yuvarlanmış (şimdiki zaman kullanılmaz).</summary>
public sealed class DateFaker : FakerBase
{
    internal DateFaker(Fake fake) : base(fake)
    {
    }

    private DateTimeOffset Reference => Fake.ReferenceDate;

    /// <summary>Referanstan en çok <paramref name="days"/> gün önce.</summary>
    public DateTimeOffset Past(int days = 365) => Between(Reference.AddDays(-days), Reference);

    /// <summary>Referanstan en çok <paramref name="days"/> gün sonra.</summary>
    public DateTimeOffset Future(int days = 365) => Between(Reference, Reference.AddDays(days));

    public DateTimeOffset Recent(int days = 7) => Past(days);

    public DateTimeOffset Soon(int days = 7) => Future(days);

    public DateTimeOffset Between(DateTimeOffset from, DateTimeOffset to)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(from, to);
        long seconds = (long)(to - from).TotalSeconds;
        return from.AddSeconds(Random.Long(0, seconds));
    }

    /// <summary>Doğum tarihi: referansa göre <paramref name="minAge"/>–<paramref name="maxAge"/> yaş.</summary>
    public DateOnly Birthday(int minAge = 18, int maxAge = 70)
    {
        DateTimeOffset date = Between(Reference.AddYears(-maxAge), Reference.AddYears(-minAge));
        return DateOnly.FromDateTime(date.UtcDateTime);
    }
}

/// <summary>Finans: tutar, IBAN.</summary>
public sealed class FinanceFaker : FakerBase
{
    internal FinanceFaker(Fake fake) : base(fake)
    {
    }

    public decimal Amount(decimal min = 0, decimal max = 10000, int decimals = 2) => Random.Decimal(min, max, decimals);

    /// <summary>Kontrol haneleri geçerli, sahte TR IBAN (26 karakter, boşluksuz).</summary>
    public string Iban()
    {
        string bban = Random.Pick(TurkishData.BankCodes) + "0" + Random.Digits(16);
        int check = 98 - Mod97(bban + "TR00");
        return $"TR{check:00}{bban}";
    }

    /// <summary>Harfleri sayıya çevirerek (A=10 ... Z=35) mod 97.</summary>
    internal static int Mod97(string value)
    {
        int remainder = 0;
        foreach (char c in value)
        {
            string digits = char.IsLetter(c) ? (char.ToUpperInvariant(c) - 'A' + 10).ToString(System.Globalization.CultureInfo.InvariantCulture) : c.ToString();
            foreach (char d in digits)
                remainder = (remainder * 10 + (d - '0')) % 97;
        }

        return remainder;
    }
}

/// <summary>Kimlikler: sabit (tohumdan türetilen) Guid'ler.</summary>
public sealed class IdentifierFaker : FakerBase
{
    internal IdentifierFaker(Fake fake) : base(fake)
    {
    }

    /// <summary>Sürüm 4 biçiminde, tohumdan türetilen Guid.</summary>
    public Guid Guid()
    {
        byte[] bytes = Random.Bytes(16);
        bytes[7] = (byte)((bytes[7] & 0x0F) | 0x40);   // sürüm 4 (Guid bayt düzeninde 7. bayt)
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);   // RFC 4122 varyantı
        return new System.Guid(bytes);
    }

    /// <summary>
    /// Sürüm 7 biçiminde Guid: zaman kısmı referans tarihi, kalanı tohumdan. CanFx'in Guid anahtarlarıyla (v7) aynı biçim.
    /// </summary>
    public Guid GuidV7()
    {
        long milliseconds = Fake.ReferenceDate.ToUnixTimeMilliseconds();
        byte[] random = Random.Bytes(10);
        Span<byte> bytes = stackalloc byte[16];
        for (int i = 0; i < 6; i++)
            bytes[i] = (byte)(milliseconds >> (8 * (5 - i)));
        bytes[6] = (byte)(0x70 | (random[0] & 0x0F));
        bytes[7] = random[1];
        bytes[8] = (byte)(0x80 | (random[2] & 0x3F));
        random.AsSpan(3, 7).CopyTo(bytes[9..]);
        return new System.Guid(bytes, bigEndian: true);
    }
}
