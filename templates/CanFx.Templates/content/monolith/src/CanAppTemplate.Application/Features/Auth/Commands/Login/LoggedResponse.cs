using CanAppTemplate.Application.Services.AuthService;

namespace CanAppTemplate.Application.Features.Auth.Commands.Login;

/// <summary>
/// Giriş sonucu: ya oturum açıldı (<see cref="Session"/>) ya da ikinci adım isteniyor (<see cref="Challenge"/> +
/// <see cref="Prompt"/>). İkinci adım bir hata değil, girişin olağan bir sonucudur; hatalar exception olarak fırlar.
/// </summary>
public sealed record LoggedResponse(AuthResult? Session, TwoFactorChallenge? Challenge, TwoFactorPrompt? Prompt)
{
    public static LoggedResponse SignedIn(AuthResult session) => new(session, null, null);

    public static LoggedResponse RequiresTwoFactor(TwoFactorChallenge challenge, TwoFactorPrompt prompt) => new(null, challenge, prompt);
}
