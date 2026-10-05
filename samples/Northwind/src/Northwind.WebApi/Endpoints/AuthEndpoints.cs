using Can.Core.Application.Exceptions;
using Can.Core.Mediator;
using Can.Core.WebApi.DependencyInjection;
using Northwind.Application.Features.Auth;

namespace Northwind.WebApi.Endpoints;

/// <summary>
/// Giriş/çıkış. Token'lar yanıt gövdesinde DÖNMEZ; HttpOnly + Secure + SameSite=Strict cookie olarak yazılır.
/// Tarayıcı sonraki isteklerde cookie'yi kendisi gönderir, JavaScript token'a erişemez.
/// </summary>
internal static class AuthEndpoints
{
    public sealed record LoginRequest(string Tenant, string Email, string Password);

    public sealed record RegisterRequest(string Tenant, string Email, string Password, string FirstName, string LastName, string CompanyName, string? Phone);

    public sealed record VerifyEmailRequest(string Tenant, string Email, string Code);

    public sealed record ResendCodeRequest(string Tenant, string Email);

    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/auth").WithTags("Auth");

        group.MapPost("/login", async (LoginRequest body, ISender sender, IAuthCookieService cookies, HttpContext http, CancellationToken ct) =>
            {
                AuthResult result = await sender.Send(new LoginCommand(body.Tenant, body.Email, body.Password, ClientIp(http)), ct);
                cookies.SetTokens(http.Response, result.AccessToken, result.RefreshToken);
                return TypedResults.Ok(result.User);
            })
            .AllowAnonymous()
            .WithSummary("Mağaza, e-posta ve şifre ile giriş; token'lar cookie'ye yazılır.");

        group.MapPost("/register", async (RegisterRequest body, ISender sender, CancellationToken ct) =>
                TypedResults.Ok(
                    await sender.Send(
                        new RegisterCommand(body.Tenant, body.Email, body.Password, body.FirstName, body.LastName, body.CompanyName, body.Phone),
                        ct
                    )
                ))
            .AllowAnonymous()
            .WithSummary("Siteden müşteri kaydı; e-postaya 6 haneli doğrulama kodu gönderilir.");

        group.MapPost("/verify-email", async (VerifyEmailRequest body, ISender sender, IAuthCookieService cookies, HttpContext http, CancellationToken ct) =>
            {
                AuthResult result = await sender.Send(new VerifyEmailCommand(body.Tenant, body.Email, body.Code, ClientIp(http)), ct);
                cookies.SetTokens(http.Response, result.AccessToken, result.RefreshToken);
                return TypedResults.Ok(result.User);
            })
            .AllowAnonymous()
            .WithSummary("E-posta doğrulama; başarılıysa kullanıcı giriş yapmış olur.");

        group.MapPost("/resend-code", async (ResendCodeRequest body, ISender sender, CancellationToken ct) =>
            {
                await sender.Send(new ResendVerificationCodeCommand(body.Tenant, body.Email), ct);
                return TypedResults.NoContent();
            })
            .AllowAnonymous()
            .WithSummary("Yeni doğrulama kodu gönderir.");

        group.MapPost("/refresh", async (ISender sender, IAuthCookieService cookies, HttpContext http, CancellationToken ct) =>
            {
                string refreshToken = cookies.GetRefreshToken(http.Request) ?? throw new UnauthorizedException("Oturum bulunamadı.");

                try
                {
                    AuthResult result = await sender.Send(new RefreshTokenCommand(refreshToken, ClientIp(http)), ct);
                    cookies.SetTokens(http.Response, result.AccessToken, result.RefreshToken);
                    return TypedResults.Ok(result.User);
                }
                catch (UnauthorizedException)
                {
                    // Geçersiz/çalınmış token: tarayıcıdaki cookie'leri de temizle.
                    cookies.Clear(http.Response);
                    throw;
                }
            })
            .AllowAnonymous()
            .WithSummary("Refresh token cookie'si ile yeni token çifti (rotasyon).");

        group.MapPost("/logout", async (ISender sender, IAuthCookieService cookies, HttpContext http, CancellationToken ct) =>
            {
                await sender.Send(new LogoutCommand(cookies.GetRefreshToken(http.Request), ClientIp(http)), ct);
                cookies.Clear(http.Response);
                return TypedResults.NoContent();
            })
            .AllowAnonymous()
            .WithSummary("Refresh token'ı iptal eder ve cookie'leri siler.");

        group.MapGet("/me", (ISender sender, CancellationToken ct) => sender.Send(new GetProfileQuery(), ct))
            .RequireAuthorization()
            .WithSummary("Giriş yapmış kullanıcının profili.");
    }

    private static string? ClientIp(HttpContext http) => http.Connection.RemoteIpAddress?.ToString();
}
