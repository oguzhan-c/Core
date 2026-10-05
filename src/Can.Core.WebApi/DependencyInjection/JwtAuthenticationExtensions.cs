using Can.Core.Security.Tokens;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Can.Core.WebApi.DependencyInjection;

/// <summary>Token'ların taşındığı cookie'lerin ayarları.</summary>
public sealed class AuthCookieOptions
{
    /// <summary>Access token (JWT) cookie'si.</summary>
    public string AccessTokenCookieName { get; set; } = "can_access_token";

    /// <summary>Refresh token cookie'si.</summary>
    public string RefreshTokenCookieName { get; set; } = "can_refresh_token";

    /// <summary>
    /// Refresh token cookie'si yalnızca bu yola gönderilir (her istekte taşınmaz). Token yenileme endpoint'inin yolu.
    /// </summary>
    public string RefreshTokenPath { get; set; } = "/auth/refresh";

    /// <summary>
    /// <see cref="SameSiteMode.Strict"/>: cookie başka sitelerden gelen isteklerle gönderilmez (CSRF koruması).
    /// Ön yüz farklı bir sitedeyse <see cref="SameSiteMode.None"/> gerekir; o durumda ayrıca antiforgery kullan.
    /// </summary>
    public SameSiteMode SameSite { get; set; } = SameSiteMode.Strict;

    /// <summary>Cookie'ler yalnızca HTTPS üzerinden gönderilir. Yalnızca yerel testlerde kapat.</summary>
    public bool Secure { get; set; } = true;

    /// <summary>Alt alan adlarıyla paylaşmak için (ör. <c>".example.com"</c>). Boşsa yalnızca mevcut alan adı.</summary>
    public string? Domain { get; set; }

    /// <summary>
    /// <c>Authorization: Bearer</c> header'ından token kabul et. Varsayılan kapalı: token yalnızca HttpOnly
    /// cookie'den okunur (JavaScript token'a erişemez, XSS ile çalınamaz). Mobil/servis istemcileri için açılabilir.
    /// </summary>
    public bool AllowAuthorizationHeader { get; set; }
}

/// <summary>Giriş/çıkışta token cookie'lerini yazar, okur ve siler.</summary>
public interface IAuthCookieService
{
    /// <summary>Access token'ı (ve varsa refresh token'ı) HttpOnly cookie olarak yazar.</summary>
    void SetTokens(HttpResponse response, AccessToken accessToken, RefreshTokenValue? refreshToken = null);

    /// <summary>Token yenileme endpoint'inde istemcinin refresh token'ını okur.</summary>
    string? GetRefreshToken(HttpRequest request);

    /// <summary>Çıkışta cookie'leri siler.</summary>
    void Clear(HttpResponse response);
}

public sealed class AuthCookieService : IAuthCookieService
{
    private readonly AuthCookieOptions _options;

    public AuthCookieService(AuthCookieOptions options)
    {
        _options = options;
    }

    public void SetTokens(HttpResponse response, AccessToken accessToken, RefreshTokenValue? refreshToken = null)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(accessToken);

        response.Cookies.Append(_options.AccessTokenCookieName, accessToken.Token, Create("/", accessToken.ExpiresAt));

        if (refreshToken is not null)
        {
            response.Cookies.Append(
                _options.RefreshTokenCookieName,
                refreshToken.Token,
                Create(_options.RefreshTokenPath, refreshToken.ExpiresAt)
            );
        }
    }

    public string? GetRefreshToken(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Cookies.TryGetValue(_options.RefreshTokenCookieName, out string? token) ? token : null;
    }

    public void Clear(HttpResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        response.Cookies.Delete(_options.AccessTokenCookieName, Create("/", expires: null));
        response.Cookies.Delete(_options.RefreshTokenCookieName, Create(_options.RefreshTokenPath, expires: null));
    }

    private CookieOptions Create(string path, DateTimeOffset? expires) =>
        new()
        {
            HttpOnly = true,
            Secure = _options.Secure,
            SameSite = _options.SameSite,
            Path = path,
            Domain = _options.Domain,
            Expires = expires,
            IsEssential = true,
        };
}

public static class JwtAuthenticationExtensions
{
    /// <summary>
    /// <c>Can.Core.Security</c>'nin ürettiği token'ları doğrulayan JWT authentication'ı ekler. Token varsayılan olarak
    /// yalnızca HttpOnly cookie'den okunur; cookie'leri yazmak için <see cref="IAuthCookieService"/> kullan.
    /// Önce <c>AddCanSecurity(...)</c> çağrılmalı.
    /// </summary>
    /// <example>
    /// <code>
    /// app.MapPost("/auth/login", async (LoginCommand command, ISender sender, IAuthCookieService cookies, HttpResponse response) =&gt;
    /// {
    ///     LoginResult result = await sender.Send(command);
    ///     cookies.SetTokens(response, result.AccessToken, result.RefreshToken);
    ///     return Results.NoContent();
    /// });
    /// </code>
    /// </example>
    /// <remarks>
    /// Claim adları dönüştürülmez (<c>MapInboundClaims = false</c>): <c>sub</c>, <c>role</c>, <c>tenant_id</c>
    /// olduğu gibi kalır; <c>User.IsInRole</c> ve <c>[Authorize(Roles = ...)]</c> <c>role</c> claim'ini kullanır.
    /// </remarks>
    public static AuthenticationBuilder AddCanJwtAuthentication(this IServiceCollection services, Action<AuthCookieOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var cookieOptions = new AuthCookieOptions();
        configure?.Invoke(cookieOptions);

        services.TryAddSingleton(cookieOptions);
        services.TryAddSingleton<IAuthCookieService, AuthCookieService>();

        AuthenticationBuilder builder = services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer();

        services
            .AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<ITokenService, AuthCookieOptions>(
                (bearer, tokens, cookies) =>
                {
                    bearer.MapInboundClaims = false;
                    bearer.TokenValidationParameters = tokens.CreateValidationParameters();
                    bearer.Events = new JwtBearerEvents
                    {
                        OnMessageReceived = context =>
                        {
                            if (context.Request.Cookies.TryGetValue(cookies.AccessTokenCookieName, out string? token)
                                && !string.IsNullOrEmpty(token))
                            {
                                context.Token = token;
                            }
                            else if (!cookies.AllowAuthorizationHeader)
                            {
                                // Cookie yok ve header'a izin verilmiyor: Authorization header'ı yok sayılır.
                                context.NoResult();
                            }

                            return Task.CompletedTask;
                        },
                    };
                }
            );

        return builder;
    }
}
