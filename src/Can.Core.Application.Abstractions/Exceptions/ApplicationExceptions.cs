using Can.Core.Domain.Exceptions;

namespace Can.Core.Application.Exceptions;

// Uygulama katmanının fırlattığı hata tipleri. WebApi katmanındaki exception middleware bunları
// HTTP durum kodlarına ve ProblemDetails yanıtlarına çevirir:
//
//   ValidationException    → 400 (alan bazlı hatalarla)
//   UnauthorizedException  → 401
//   ForbiddenException     → 403
//   NotFoundException      → 404
//   ConflictException      → 409
//   BusinessException      → 400  (Can.Core.Domain.Exceptions)

/// <summary>İstenen kayıt bulunamadı.</summary>
public class NotFoundException : Exception, IHasErrorCode
{
    /// <inheritdoc />
    public string? Code { get; init; }

    public NotFoundException(string message)
        : base(message) { }

    /// <summary><c>NotFoundException.For&lt;Product&gt;(id)</c> → "'Product' (42) bulunamadı."</summary>
    public static NotFoundException For<TEntity>(object id) => new($"'{typeof(TEntity).Name}' ({id}) bulunamadı.");
}

/// <summary>İstek mevcut durumla çakışıyor (ör. aynı isimde kayıt var, eşzamanlı güncelleme).</summary>
public class ConflictException : Exception, IHasErrorCode
{
    /// <inheritdoc />
    public string? Code { get; init; }

    public ConflictException(string message)
        : base(message) { }
}

/// <summary>Kullanıcı kimliği doğrulanmamış (giriş yapılmamış).</summary>
public class UnauthorizedException : Exception, IHasErrorCode
{
    /// <inheritdoc />
    public string? Code { get; init; }

    public UnauthorizedException(string message = "Bu işlem için giriş yapmalısın.")
        : base(message) { }
}

/// <summary>Kullanıcı giriş yapmış ama bu işlem için yetkisi yok.</summary>
public class ForbiddenException : Exception, IHasErrorCode
{
    /// <inheritdoc />
    public string? Code { get; init; }

    public ForbiddenException(string message = "Bu işlem için yetkin yok.")
        : base(message) { }
}

/// <summary>İstek doğrulanamadı. <see cref="Errors"/>: alan adı → hata mesajları.</summary>
public class ValidationException : Exception, IHasErrorCode
{
    /// <inheritdoc />
    public string? Code { get; init; }

    public ValidationException(IReadOnlyDictionary<string, string[]> errors)
        : base(BuildMessage(errors))
    {
        Errors = errors;
    }

    public ValidationException(string propertyName, string error)
        : this(new Dictionary<string, string[]> { [propertyName] = [error] }) { }

    public IReadOnlyDictionary<string, string[]> Errors { get; }

    private static string BuildMessage(IReadOnlyDictionary<string, string[]> errors) =>
        "Doğrulama hataları: " + string.Join("; ", errors.Select(e => $"{e.Key}: {string.Join(", ", e.Value)}"));
}
