namespace Can.Core.Domain.Exceptions;

/// <summary>
/// İstemcinin hatayı mesaja bakmadan ayırt edebilmesi için makinece okunur kod (ör. <c>email_not_confirmed</c>).
/// WebApi katmanı bunu ProblemDetails yanıtına <c>"code"</c> alanı olarak yazar.
/// </summary>
/// <example>
/// <code>
/// throw new ForbiddenException("E-posta adresi doğrulanmadı.") { Code = "email_not_confirmed" };
/// </code>
/// </example>
public interface IHasErrorCode
{
    string? Code { get; }
}
