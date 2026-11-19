using System.Globalization;
using System.Text;

namespace CanFx.FakeData;

/// <summary>Bir veri türünün tanımı (Rider diyaloğunda ve canfxgen yardımında gösterilir).</summary>
/// <param name="Name">Tür adı: "Price".</param>
/// <param name="Usage">Parametreleriyle yazımı: "Price(min, max)".</param>
/// <param name="Description">Açıklama.</param>
/// <param name="Example">Örnek çıktı.</param>
public sealed record FakeKindInfo(string Name, string Usage, string Description, string Example);

/// <summary>
/// Metinle tanımlanan veri türleri: <c>"Price(50, 5000)"</c>, <c>"Pick(S, M, L)"</c>, <c>"Slug(Name)"</c>. canfxgen alan başına
/// türü buradan seçer (<see cref="Guess"/>) ve değeri buradan üretir (<see cref="Generate"/>).
/// </summary>
public static class FakeKinds
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    private static readonly Dictionary<string, (FakeKindInfo Info, Func<Fake, FakeRow?, string[], object?> Make)> Kinds =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["FirstName"] = (new("FirstName", "FirstName", "Ad", "Elif"), (f, _, _) => f.Person.FirstName()),
            ["LastName"] = (new("LastName", "LastName", "Soyad", "Yıldız"), (f, _, _) => f.Person.LastName()),
            ["FullName"] = (new("FullName", "FullName", "Ad soyad", "Elif Yıldız"), (f, _, _) => f.Person.FullName()),
            ["Email"] = (new("Email", "Email", "E-posta (example.com)", "elif.yildiz17@example.com"), (f, row, _) => f.Internet.Email(Text(row, "FirstName"), Text(row, "LastName"))),
            ["UserName"] = (new("UserName", "UserName", "Kullanıcı adı", "elif_yildiz17"), (f, row, _) => f.Internet.UserName(Text(row, "FirstName"), Text(row, "LastName"))),
            ["Phone"] = (new("Phone", "Phone", "Cep telefonu", "+90 532 418 27 65"), (f, _, _) => f.Phone.Mobile()),
            ["PhoneE164"] = (new("PhoneE164", "PhoneE164", "Cep telefonu (E.164)", "+905324182765"), (f, _, _) => f.Phone.MobileE164()),
            ["Landline"] = (new("Landline", "Landline", "Sabit hat", "+90 256 213 45 67"), (f, row, _) => f.Phone.Landline(Text(row, "City"))),
            ["City"] = (new("City", "City", "İl", "Aydın"), (f, _, _) => f.Address.City()),
            ["District"] = (new("District", "District", "İlçe (City alanı varsa ona göre)", "Efeler"), (f, row, _) => f.Address.District(Text(row, "City"))),
            ["Neighborhood"] = (new("Neighborhood", "Neighborhood", "Mahalle", "Ata Mahallesi"), (f, _, _) => f.Address.Neighborhood()),
            ["Street"] = (new("Street", "Street", "Sokak / cadde", "Gül Sokak"), (f, _, _) => f.Address.Street()),
            ["StreetAddress"] = (new("StreetAddress", "StreetAddress", "Açık adres", "Ata Mahallesi Gül Sokak No: 12 Daire: 4"), (f, _, _) => f.Address.StreetAddress()),
            ["Address"] = (new("Address", "Address", "Tam adres", "… 09100 Efeler / Aydın"), (f, _, _) => f.Address.FullAddress()),
            ["ZipCode"] = (new("ZipCode", "ZipCode", "Posta kodu", "09100"), (f, row, _) => f.Address.ZipCode(Text(row, "City"))),
            ["Company"] = (new("Company", "Company", "Şirket adı", "Anadolu Tekstil Ltd. Şti."), (f, _, _) => f.Company.Name()),
            ["ProductName"] = (new("ProductName", "ProductName", "Ürün adı", "Pamuklu Slim Gömlek"), (f, _, _) => f.Commerce.ProductName()),
            ["Department"] = (new("Department", "Department", "Reyon / kategori adı", "Ev ve Yaşam"), (f, _, _) => f.Commerce.Department()),
            ["Color"] = (new("Color", "Color", "Renk", "Lacivert"), (f, _, _) => f.Commerce.Color()),
            ["Size"] = (new("Size", "Size", "Beden", "M"), (f, _, _) => f.Commerce.Size()),
            ["Sku"] = (new("Sku", "Sku(prefix)", "Stok kodu", "SKU-48213"), (f, _, a) => f.Commerce.Sku(Arg(a, 0, "SKU"))),
            ["Barcode"] = (new("Barcode", "Barcode", "EAN-13 barkod (geçerli kontrol hanesi)", "8691234567895"), (f, _, _) => f.Commerce.Ean13()),
            ["Word"] = (new("Word", "Word", "Tek kelime", "kalite"), (f, _, _) => f.Text.Word()),
            ["Words"] = (new("Words", "Words(count)", "Kelimeler", "yeni şık ürün"), (f, _, a) => f.Text.Words(Int(a, 0, 3))),
            ["Title"] = (new("Title", "Title", "Başlık (2–4 kelime)", "Yeni Sezon Koleksiyon"), (f, _, _) => f.Text.Title()),
            ["Sentence"] = (new("Sentence", "Sentence", "Cümle", "Rahat ve şık bir ürün."), (f, _, _) => f.Text.Sentence()),
            ["Paragraph"] = (new("Paragraph", "Paragraph(sentences)", "Paragraf", "…"), (f, _, a) => f.Text.Paragraph(Int(a, 0, 3))),
            ["Slug"] = (new("Slug", "Slug(member)", "Başka alandan slug (o alan önce üretilmeli)", "pamuklu-slim-gomlek"), (f, row, a) => FakeFormat.Slug(Text(row, Arg(a, 0, "Name")) ?? f.Text.Words(3))),
            ["Int"] = (new("Int", "Int(min, max)", "Tam sayı", "42"), (f, _, a) => f.Random.Int(Int(a, 0, 0), Int(a, 1, 100))),
            ["Long"] = (new("Long", "Long(min, max)", "Büyük tam sayı", "123456"), (f, _, a) => f.Random.Long(Long(a, 0, 0), Long(a, 1, 1_000_000))),
            ["Decimal"] = (new("Decimal", "Decimal(min, max, decimals)", "Ondalıklı sayı", "12.75"), (f, _, a) => f.Random.Decimal(Dec(a, 0, 0), Dec(a, 1, 1000), Int(a, 2, 2))),
            ["Price"] = (new("Price", "Price(min, max)", "Mağaza fiyatı (,90 / ,99)", "349.90"), (f, _, a) => f.Commerce.Price(Dec(a, 0, 10), Dec(a, 1, 1000))),
            ["Bool"] = (new("Bool", "Bool(probability)", "true olasılığı (0–1)", "true"), (f, _, a) => f.Random.Bool(Dbl(a, 0, 0.5))),
            ["Past"] = (new("Past", "Past(days)", "Referans tarihinden önce", "2025-08-14T10:24:00Z"), (f, _, a) => f.Date.Past(Int(a, 0, 365))),
            ["Future"] = (new("Future", "Future(days)", "Referans tarihinden sonra", "2026-03-02T08:00:00Z"), (f, _, a) => f.Date.Future(Int(a, 0, 365))),
            ["Birthday"] = (new("Birthday", "Birthday(minAge, maxAge)", "Doğum tarihi", "1990-05-17"), (f, _, a) => f.Date.Birthday(Int(a, 0, 18), Int(a, 1, 70))),
            ["Guid"] = (new("Guid", "Guid", "Guid (v4 biçimi)", "…"), (f, _, _) => f.Identifiers.Guid()),
            ["GuidV7"] = (new("GuidV7", "GuidV7", "Guid (v7 biçimi, CanFx anahtarları gibi)", "…"), (f, _, _) => f.Identifiers.GuidV7()),
            ["Iban"] = (new("Iban", "Iban", "TR IBAN (geçerli kontrol haneleri)", "TR330006100519786457841326"), (f, _, _) => f.Finance.Iban()),
            ["Url"] = (new("Url", "Url", "Web adresi (example alanı)", "https://www.atlas.example.com"), (f, _, _) => f.Internet.Url()),
            ["Domain"] = (new("Domain", "Domain", "Alan adı", "atlas.example.com"), (f, _, _) => f.Internet.Domain()),
            ["Pattern"] = (new("Pattern", "Pattern(pattern)", "# rakam, ? harf, * ikisi", "AB-1234"), (f, _, a) => f.Random.Pattern(Arg(a, 0, "????-####"))),
            ["Pick"] = (new("Pick", "Pick(a, b, c)", "Listeden biri", "M"), (f, _, a) => a.Length == 0 ? throw new FormatException("Pick en az bir değer ister: Pick(S, M, L).") : f.Random.Pick(a)),
            ["Fixed"] = (new("Fixed", "Fixed(value)", "Her kayıtta aynı değer", "TRY"), (_, _, a) => Arg(a, 0, string.Empty)),
            ["Null"] = (new("Null", "Null", "Her zaman boş", "null"), (_, _, _) => null),
        };

    /// <summary>Bütün türler (Rider diyaloğu ve yardım için).</summary>
    public static IReadOnlyList<FakeKindInfo> All { get; } = Kinds.Values.Select(k => k.Info).ToList();

    /// <summary>Türün değerini üretir. <paramref name="row"/>: aynı kaydın önceki alanları (Slug, Email için).</summary>
    /// <exception cref="FormatException">Bilinmeyen tür ya da hatalı parametre.</exception>
    public static object? Generate(string spec, Fake fake, FakeRow? row = null)
    {
        ArgumentNullException.ThrowIfNull(fake);
        (string name, string[] args) = Parse(spec);
        return Kinds.TryGetValue(name, out var kind)
            ? kind.Make(fake, row, args)
            : throw new FormatException($"Bilinmeyen veri türü: '{name}'. Türler: {string.Join(", ", Kinds.Keys)}.");
    }

    /// <summary>Tür adı geçerli mi (parametreler dahil yazımı çözülebiliyor mu).</summary>
    public static bool IsKnown(string spec)
    {
        try
        {
            return Kinds.ContainsKey(Parse(spec).Name);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// Alan adı ve tipinden tür tahmini: <c>("Email", "string") → "Email"</c>, <c>("Price", "decimal") → "Price(10, 5000)"</c>.
    /// Enum'larda <paramref name="enumValues"/> verilirse <c>Pick(...)</c>.
    /// </summary>
    public static string Guess(string member, string typeName, IReadOnlyList<string>? enumValues = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(member);
        ArgumentException.ThrowIfNullOrWhiteSpace(typeName);
        string name = member.ToLowerInvariant();
        string type = Normalize(typeName);

        if (enumValues is { Count: > 0 })
            return $"Pick({string.Join(", ", enumValues)})";

        return type switch
        {
            "bool" => name.StartsWith("is", StringComparison.Ordinal) || name.StartsWith("has", StringComparison.Ordinal) ? "Bool(0.8)" : "Bool",
            "Guid" => "GuidV7",
            "DateTime" or "DateTimeOffset" or "DateOnly" => name.Contains("birth", StringComparison.Ordinal) ? "Birthday"
                : Has(name, "expire", "until", "due", "end", "valid", "deadline") ? "Future(365)" : "Past(365)",
            "int" or "long" or "short" => Has(name, "rating", "star") ? "Int(1, 5)"
                : Has(name, "order", "sort", "position", "rank") ? "Int(1, 100)"
                : Has(name, "year") ? "Int(2000, 2026)"
                : Has(name, "age") ? "Int(18, 70)"
                : Has(name, "quantity", "stock", "count", "reserved") ? "Int(0, 100)"
                : Has(name, "weight", "gram") ? "Int(50, 5000)"
                : "Int(1, 1000)",
            "decimal" or "double" or "float" => Has(name, "rate", "percent", "ratio") ? "Decimal(0, 0.3, 2)"
                : Has(name, "price", "amount", "total", "cost", "balance", "fee", "salary") ? "Price(10, 5000)"
                : Has(name, "lat") ? "Decimal(36, 42, 6)"
                : Has(name, "lon", "lng") ? "Decimal(26, 45, 6)"
                : "Decimal(0, 1000, 2)",
            "string" => GuessText(name),
            _ => "Null",
        };
    }

    /// <summary>Üretilen değeri alanın tipine çevirir (canfxgen literal yazmadan önce).</summary>
    public static object? ConvertTo(object? value, string typeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(typeName);
        if (value is null)
            return null;

        return Normalize(typeName) switch
        {
            "string" => value as string ?? Convert.ToString(value, Invariant),
            "int" => Convert.ToInt32(value, Invariant),
            "long" => Convert.ToInt64(value, Invariant),
            "short" => Convert.ToInt16(value, Invariant),
            "decimal" => Convert.ToDecimal(value, Invariant),
            "double" => Convert.ToDouble(value, Invariant),
            "float" => Convert.ToSingle(value, Invariant),
            "bool" => value is string s ? bool.Parse(s) : Convert.ToBoolean(value, Invariant),
            "Guid" => value is Guid g ? g : Guid.Parse(Convert.ToString(value, Invariant)!),
            "DateTimeOffset" => value switch
            {
                DateTimeOffset d => d,
                DateOnly d => new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
                _ => DateTimeOffset.Parse(Convert.ToString(value, Invariant)!, Invariant),
            },
            "DateTime" => value switch
            {
                DateTimeOffset d => d.UtcDateTime,
                DateOnly d => d.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
                _ => DateTime.Parse(Convert.ToString(value, Invariant)!, Invariant),
            },
            "DateOnly" => value switch
            {
                DateOnly d => d,
                DateTimeOffset d => DateOnly.FromDateTime(d.UtcDateTime),
                _ => DateOnly.Parse(Convert.ToString(value, Invariant)!, Invariant),
            },
            _ => value,   // enum ve diğerleri: çağıran yazar (ör. "Paid" → OrderStatus.Paid)
        };
    }

    /// <summary>"Price(50, 5000)" → ("Price", ["50", "5000"]); tırnak içindeki virgül bölmez: Fixed("a, b").</summary>
    public static (string Name, string[] Args) Parse(string spec)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(spec);
        spec = spec.Trim();
        int open = spec.IndexOf('(', StringComparison.Ordinal);
        if (open < 0)
            return (spec, []);
        if (!spec.EndsWith(')'))
            throw new FormatException($"Kapanmayan parantez: '{spec}'.");

        string name = spec[..open].Trim();
        string inner = spec[(open + 1)..^1];
        var args = new List<string>();
        var current = new StringBuilder();
        bool quoted = false;
        foreach (char c in inner)
        {
            if (c == '"')
                quoted = !quoted;
            else if (c == ',' && !quoted)
            {
                args.Add(current.ToString().Trim());
                current.Clear();
            }
            else
                current.Append(c);
        }

        if (quoted)
            throw new FormatException($"Kapanmayan tırnak: '{spec}'.");
        if (current.Length > 0 || args.Count > 0)
            args.Add(current.ToString().Trim());
        return (name, args.ToArray());
    }

    private static string GuessText(string name) =>
        name switch
        {
            _ when name.Contains("email", StringComparison.Ordinal) => "Email",
            _ when name.Contains("firstname", StringComparison.Ordinal) => "FirstName",
            _ when Has(name, "lastname", "surname") => "LastName",
            _ when Has(name, "fullname", "contactname", "customername") => "FullName",
            _ when name.Contains("username", StringComparison.Ordinal) => "UserName",
            _ when Has(name, "phone", "gsm", "mobile") => "Phone",
            _ when name.Contains("city", StringComparison.Ordinal) => "City",
            _ when name.Contains("district", StringComparison.Ordinal) => "District",
            _ when name.Contains("neighborhood", StringComparison.Ordinal) => "Neighborhood",
            _ when name.Contains("street", StringComparison.Ordinal) => "Street",
            _ when Has(name, "zip", "postal") => "ZipCode",
            _ when Has(name, "address", "line") => "StreetAddress",
            _ when Has(name, "company", "legalname") => "Company",
            _ when name.Contains("slug", StringComparison.Ordinal) => "Slug(Name)",
            _ when name.Contains("sku", StringComparison.Ordinal) => "Sku",
            _ when Has(name, "barcode", "ean", "gtin") => "Barcode",
            _ when name.Contains("iban", StringComparison.Ordinal) => "Iban",
            _ when Has(name, "taxnumber", "vkn") => "Pattern(##########)",
            _ when Has(name, "url", "website") => "Url",
            _ when name.Contains("domain", StringComparison.Ordinal) => "Domain",
            _ when name.Contains("color", StringComparison.Ordinal) => "Color",
            _ when name.Contains("size", StringComparison.Ordinal) => "Size",
            _ when name.Contains("department", StringComparison.Ordinal) => "Department",
            _ when name.Contains("currency", StringComparison.Ordinal) => "Fixed(TRY)",
            _ when Has(name, "path", "image", "logo", "photo") => "Pattern(/fake/images/######.webp)",
            _ when Has(name, "description", "content", "body", "summary") => "Paragraph(2)",
            _ when Has(name, "comment", "note", "reason", "message") => "Sentence",
            _ when name.Contains("title", StringComparison.Ordinal) => "Title",
            _ when Has(name, "code", "number", "reference") => "Pattern(??-######)",
            _ when name.Contains("productname", StringComparison.Ordinal) => "ProductName",
            _ when name.EndsWith("name", StringComparison.Ordinal) => "Title",
            _ when Has(name, "password", "hash", "token", "secret", "stamp") => "Null",
            _ => "Words(2)",
        };

    private static bool Has(string name, params string[] parts) => parts.Any(p => name.Contains(p, StringComparison.Ordinal));

    private static string Normalize(string typeName)
    {
        string type = typeName.Trim().TrimEnd('?');
        if (type.StartsWith("System.", StringComparison.Ordinal))
            type = type["System.".Length..];
        return type switch
        {
            "String" => "string",
            "Int32" => "int",
            "Int64" => "long",
            "Int16" => "short",
            "Decimal" => "decimal",
            "Double" => "double",
            "Single" => "float",
            "Boolean" => "bool",
            _ => type,
        };
    }

    private static string? Text(FakeRow? row, string member) =>
        row is not null && row.Values.TryGetValue(member, out object? value) ? value as string : null;

    private static string Arg(string[] args, int index, string fallback) =>
        index < args.Length && args[index].Length > 0 ? args[index].Trim('"') : fallback;

    private static int Int(string[] args, int index, int fallback) =>
        index < args.Length ? int.Parse(args[index], NumberStyles.Integer, Invariant) : fallback;

    private static long Long(string[] args, int index, long fallback) =>
        index < args.Length ? long.Parse(args[index], NumberStyles.Integer, Invariant) : fallback;

    private static decimal Dec(string[] args, int index, decimal fallback) =>
        index < args.Length ? decimal.Parse(args[index], NumberStyles.Number, Invariant) : fallback;

    private static double Dbl(string[] args, int index, double fallback) =>
        index < args.Length ? double.Parse(args[index], NumberStyles.Float, Invariant) : fallback;
}
