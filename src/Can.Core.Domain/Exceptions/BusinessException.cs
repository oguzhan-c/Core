namespace Can.Core.Domain.Exceptions;

/// <summary>
/// Bir iş kuralı ihlal edildiğinde fırlatılır (ör. "Onaylanmış sipariş iptal edilemez").
/// Aggregate'ler, business rule sınıfları ve handler'lar kullanabilir. WebApi katmanı 400 Bad Request'e çevirir.
/// </summary>
public class BusinessException : Exception
{
    public BusinessException(string message)
        : base(message) { }

    public BusinessException(string message, Exception innerException)
        : base(message, innerException) { }
}
