namespace CanAppTemplate.Application.Features.Auth.Commands.Register;

/// <summary>Kayıt sonucu: doğrulama kodunun gönderildiği adres ve kodun geçerlilik süresi.</summary>
public sealed record RegisteredResponse(string Email, DateTimeOffset CodeExpiresAt);
