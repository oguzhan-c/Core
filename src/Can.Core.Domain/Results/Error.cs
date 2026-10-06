namespace Can.Core.Domain.Results;

/// <summary>Hatanın türü. WebApi bunu HTTP durum koduna çevirir (parantez içinde).</summary>
public enum ErrorType
{
    /// <summary>İş kuralı ihlali (400).</summary>
    Failure = 0,

    /// <summary>Girdi doğrulama hatası (400); <see cref="Error.Field"/> hangi alan olduğunu söyler.</summary>
    Validation,

    /// <summary>Kayıt bulunamadı (404).</summary>
    NotFound,

    /// <summary>Kaydın mevcut durumuyla çakışma, ör. aynı kod zaten var (409).</summary>
    Conflict,

    /// <summary>Giriş gerekli ya da kimlik doğrulanamadı (401).</summary>
    Unauthorized,

    /// <summary>Giriş yapılmış ama yetki yok (403).</summary>
    Forbidden,

    /// <summary>Beklenmeyen durum (500). Gerçek hatalar (bug, veritabanı çöktü) exception olarak kalmalı.</summary>
    Unexpected,
}

/// <summary>
/// Beklenen bir hatayı tarif eder: istemcinin ayırt edebileceği sabit bir <see cref="Code"/>, kullanıcıya gösterilecek
/// <see cref="Description"/> ve HTTP karşılığını belirleyen <see cref="Type"/>.
/// </summary>
/// <remarks>
/// Hataları her aggregate için bir katalogda topla; böylece kodlar tek yerde durur ve testlerde karşılaştırılabilir:
/// <code>
/// public static class ProductErrors
/// {
///     public static Error NotFound(Guid id) =&gt; Error.NotFound("product.not_found", $"'{id}' ürünü bulunamadı.");
///     public static readonly Error Discontinued = Error.Failure("product.discontinued", "Ürün satıştan kaldırılmış.");
/// }
/// </code>
/// </remarks>
public sealed record Error
{
    private static readonly IReadOnlyDictionary<string, object?> NoMetadata = new Dictionary<string, object?>();

    private Error(string code, string description, ErrorType type, string? field, IReadOnlyDictionary<string, object?>? metadata)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentNullException.ThrowIfNull(description);

        Code = code;
        Description = description;
        Type = type;
        Field = field;
        Metadata = metadata ?? NoMetadata;
    }

    /// <summary>Sabit, makinece okunur kod (ör. <c>"product.not_found"</c>). ProblemDetails'te <c>code</c> olarak döner.</summary>
    public string Code { get; }

    /// <summary>Kullanıcıya gösterilebilecek açıklama.</summary>
    public string Description { get; }

    public ErrorType Type { get; }

    /// <summary>Doğrulama hatalarında ilgili alan (ör. <c>"Name"</c>); ProblemDetails <c>errors</c> sözlüğünün anahtarı.</summary>
    public string? Field { get; }

    /// <summary>Ek bilgi (ör. kalan stok). İstemciye gönderilmez; loglama ve iş mantığı içindir.</summary>
    public IReadOnlyDictionary<string, object?> Metadata { get; }

    public static Error Failure(string code, string description) => new(code, description, ErrorType.Failure, null, null);

    public static Error Validation(string code, string description, string? field = null) =>
        new(code, description, ErrorType.Validation, field, null);

    public static Error NotFound(string code, string description) => new(code, description, ErrorType.NotFound, null, null);

    public static Error Conflict(string code, string description) => new(code, description, ErrorType.Conflict, null, null);

    public static Error Unauthorized(string code = "unauthorized", string description = "Bu işlem için giriş yapmalısın.") =>
        new(code, description, ErrorType.Unauthorized, null, null);

    public static Error Forbidden(string code = "forbidden", string description = "Bu işlem için yetkin yok.") =>
        new(code, description, ErrorType.Forbidden, null, null);

    public static Error Unexpected(string code = "unexpected", string description = "Beklenmeyen bir hata oluştu.") =>
        new(code, description, ErrorType.Unexpected, null, null);

    /// <summary>Aynı hata, ek bilgiyle (orijinal değişmez).</summary>
    public Error WithMetadata(string key, object? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var metadata = new Dictionary<string, object?>(Metadata, StringComparer.Ordinal) { [key] = value };
        return new Error(Code, Description, Type, Field, metadata);
    }

    /// <summary>Aynı hata, başka bir alana bağlı olarak (ör. iç içe nesnede <c>"Address.City"</c>).</summary>
    public Error ForField(string? field) => new(Code, Description, Type, field, Metadata);

    /// <summary>Kod, tür, alan ve açıklama eşitse aynı hatadır (metadata karşılaştırılmaz).</summary>
    public bool Equals(Error? other) =>
        other is not null
        && Code == other.Code
        && Type == other.Type
        && Field == other.Field
        && Description == other.Description;

    public override int GetHashCode() => HashCode.Combine(Code, Type, Field, Description);

    public override string ToString() => Field is null ? $"{Code}: {Description}" : $"{Code} ({Field}): {Description}";
}
