using System.Globalization;

namespace CanFx.Persistence.Seeding;

/// <summary>
/// Geliştirmede ekranlar boş görünmesin diye örnek (sahte) veri üretir; dış paket gerektirmez. Aynı tohumla (seed) her
/// çalıştırmada aynı değerleri üretir. Daha zengin veri için Bogus gibi bir kütüphane kullanılabilir.
/// </summary>
/// <example>
/// <code>
/// public sealed class SampleCustomerSeeder(AppDbContext db) : IDataSeeder
/// {
///     public bool DevelopmentOnly => true;
///
///     public async Task SeedAsync(DataSeedContext context, CancellationToken ct)
///     {
///         if (await db.Customers.AnyAsync(ct)) return;
///         var sample = new SampleData();
///         for (int i = 0; i &lt; 50; i++)
///             db.Customers.Add(Customer.Create(sample.FullName(), sample.Email(), sample.Phone()));
///         await db.SaveChangesAsync(ct);
///     }
/// }
/// </code>
/// </example>
public sealed class SampleData
{
    private static readonly string[] FirstNames =
        ["Ali", "Ayşe", "Mehmet", "Zeynep", "Mustafa", "Elif", "Ahmet", "Fatma", "Emre", "Selin", "Burak", "Deniz", "Can", "Ece", "Kerem", "Merve"];

    private static readonly string[] LastNames =
        ["Yılmaz", "Kaya", "Demir", "Şahin", "Çelik", "Yıldız", "Aydın", "Öztürk", "Arslan", "Doğan", "Kılıç", "Aslan", "Koç", "Kurt"];

    private static readonly string[] Words =
        ["hızlı", "güvenli", "yeni", "akıllı", "kolay", "modern", "esnek", "pratik", "güçlü", "sade", "verimli", "şık"];

    private static readonly string[] Products =
        ["Kalem", "Defter", "Çanta", "Masa", "Sandalye", "Lamba", "Kupa", "Saat", "Kulaklık", "Klavye", "Monitör", "Telefon"];

    private static readonly string[] Cities =
        ["İstanbul", "Ankara", "İzmir", "Bursa", "Antalya", "Aydın", "Konya", "Eskişehir", "Trabzon", "Muğla"];

    private readonly Random _random;
    private int _sequence;

    /// <param name="seed">Aynı tohum her çalıştırmada aynı veriyi üretir.</param>
    public SampleData(int seed = 42) => _random = new Random(seed);

    public string FirstName() => Pick(FirstNames);

    public string LastName() => Pick(LastNames);

    public string FullName() => $"{FirstName()} {LastName()}";

    /// <summary>Benzersiz e-posta (<c>ayse.kaya7@example.com</c>).</summary>
    public string Email() =>
        $"{Ascii(FirstName())}.{Ascii(LastName())}{++_sequence}@example.com".ToLowerInvariant();

    public string Phone() => $"+905{_random.Next(30, 60)}{_random.Next(1_000_000, 9_999_999)}";

    public string City() => Pick(Cities);

    public string ProductName() => $"{Capitalize(Pick(Words))} {Pick(Products)}";

    /// <summary><paramref name="words"/> kelimelik cümle.</summary>
    public string Sentence(int words = 8) =>
        Capitalize(string.Join(' ', Enumerable.Range(0, words).Select(_ => Pick(Words)))) + ".";

    public int Number(int min, int max) => _random.Next(min, max + 1);

    /// <summary>İki ondalıklı tutar.</summary>
    public decimal Price(decimal min = 10, decimal max = 1000) =>
        Math.Round(min + (decimal)_random.NextDouble() * (max - min), 2);

    public bool Bool(double trueRatio = 0.5) => _random.NextDouble() < trueRatio;

    /// <summary>Şimdiden geriye doğru en çok <paramref name="maxDaysAgo"/> gün önceki bir an.</summary>
    public DateTimeOffset PastDate(int maxDaysAgo = 365) =>
        DateTimeOffset.UtcNow.AddMinutes(-_random.Next(0, maxDaysAgo * 24 * 60));

    public T Pick<T>(IReadOnlyList<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0)
            throw new ArgumentException("Liste boş.", nameof(items));
        return items[_random.Next(items.Count)];
    }

    private static string Capitalize(string text) =>
        text.Length == 0 ? text : char.ToUpper(text[0], CultureInfo.GetCultureInfo("tr-TR")) + text[1..];

    private static string Ascii(string text) =>
        text.Replace('ı', 'i').Replace('İ', 'I').Replace('ş', 's').Replace('Ş', 'S').Replace('ğ', 'g').Replace('Ğ', 'G')
            .Replace('ü', 'u').Replace('Ü', 'U').Replace('ö', 'o').Replace('Ö', 'O').Replace('ç', 'c').Replace('Ç', 'C');
}
