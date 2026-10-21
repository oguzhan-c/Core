using Can.Core.Domain.Exceptions;
using Can.Core.Mediator;
using Can.Core.WebApi.DependencyInjection;
using Can.Core.WebApi.ExternalLogin;
using Microsoft.AspNetCore.WebUtilities;
using CanAppTemplate.Application.Features.Auth.Commands.ExternalLogin;
using CanAppTemplate.Application.Features.Auth.Commands.Login;
using CanAppTemplate.Application.Features.Auth.Commands.LinkExternalLogin;
using CanAppTemplate.Application.Services.AuthService;

namespace CanAppTemplate.WebAPI.Security;

/// <summary>
/// Google / Microsoft / GitHub ile giriş (<c>/api/auth/external/...</c>). Dönüşte kullanıcı bulunur ya da oluşturulur,
/// token'lar şifreli girişteki gibi HttpOnly cookie'ye yazılır ve kullanıcı sayfaya yönlendirilir. Sağlayıcı
/// ayarlanmamışsa (<c>Security:External:*</c> boş) düğmeler hiç görünmez.
/// </summary>
internal static class ExternalLoginCallback
{
    public static void MapExternalLoginEndpoints(this IEndpointRouteBuilder app) => app.MapCanExternalLogin("/api/auth/external", OnCallbackAsync);

    private static async Task<IResult> OnCallbackAsync(ExternalLoginInfo info, HttpContext http)
    {
        IServiceProvider services = http.RequestServices;
        ISender sender = services.GetRequiredService<ISender>();
        CanExternalLoginOptions options = services.GetRequiredService<CanExternalLoginOptions>();
        CancellationToken ct = http.RequestAborted;

        if (info.IsLink)
        {
            if (!IdentityKey.TryParse(info.LinkUserId, out IdentityKey userId))
                return info.RedirectWithError(options, "failed");

            try
            {
                await sender.Send(new LinkExternalLoginCommand(userId, info.Provider, info.ProviderDisplayName, info.ProviderKey), ct);
            }
            catch (Exception ex) when (ex is IHasErrorCode)
            {
                return info.RedirectWithError(options, ErrorCodeOf(ex));
            }

            return Results.Redirect(QueryHelpers.AddQueryString(info.ReturnUrl, "externalLinked", info.Provider));
        }

        LoggedResponse login;
        try
        {
            login = await sender.Send(
                new ExternalLoginCommand(
                    info.Provider,
                    info.ProviderDisplayName,
                    info.ProviderKey,
                    info.Email,
                    info.EmailVerified,
                    info.GivenName,
                    info.FamilyName,
                    info.Name,
                    services.GetRequiredService<ClientInfoFactory>().Create(http)
                ),
                ct
            );
        }
        catch (Exception ex) when (ex is IHasErrorCode)
        {
            // Tarayıcı sağlayıcıdan dönüyor: hata ProblemDetails değil, hata sayfasına yönlendirme (?error=kod).
            return info.RedirectWithError(options, ErrorCodeOf(ex));
        }

        if (login.Challenge is { } challenge)
        {
            // İkinci adım: bekleyen giriş şifreli cookie'de; giriş sayfası kod adımını açar.
            services.GetRequiredService<TwoFactorCookie>().Write(http.Response, challenge);
            var query = new Dictionary<string, string?>
            {
                ["twoFactor"] = login.Prompt!.Method.ToString(),
                ["destination"] = login.Prompt.Destination,
                ["returnUrl"] = info.ReturnUrl,
            };
            return Results.Redirect(QueryHelpers.AddQueryString(options.ErrorPath, query.Where(p => p.Value is not null)));
        }

        services.GetRequiredService<TwoFactorCookie>().Clear(http.Response);
        services.GetRequiredService<IAuthCookieService>().SetTokens(http.Response, login.Session!.AccessToken, login.Session.RefreshToken);
        return Results.Redirect(info.ReturnUrl);
    }

    private static string ErrorCodeOf(Exception exception) => (exception as IHasErrorCode)?.Code ?? "failed";
}
