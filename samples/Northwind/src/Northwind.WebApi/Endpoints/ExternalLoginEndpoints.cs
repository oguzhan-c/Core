using Can.Core.Domain.Results;
using Can.Core.Mediator;
using Can.Core.WebApi.DependencyInjection;
using Can.Core.WebApi.ExternalLogin;
using Microsoft.AspNetCore.WebUtilities;
using Northwind.Application.Features.Auth;
using Northwind.WebApi.Security;

namespace Northwind.WebApi.Endpoints;

/// <summary>
/// Google / Microsoft / GitHub ile giriş (<c>/api/auth/external/...</c>). Sağlayıcıdan dönüşte kullanıcı bulunur ya da
/// oluşturulur, token'lar şifreli girişteki gibi HttpOnly cookie'ye yazılır ve kullanıcı sayfaya yönlendirilir.
/// Sağlayıcı ayarlanmamışsa (<c>Security:External:*</c> boş) düğmeler hiç görünmez.
/// </summary>
internal static class ExternalLoginEndpoints
{
    public static void MapExternalLoginEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapCanExternalLogin("/auth/external", OnCallbackAsync);
    }

    private static async Task<IResult> OnCallbackAsync(ExternalLoginInfo info, HttpContext http)
    {
        IServiceProvider services = http.RequestServices;
        ISender sender = services.GetRequiredService<ISender>();
        CanExternalLoginOptions options = services.GetRequiredService<CanExternalLoginOptions>();
        CancellationToken ct = http.RequestAborted;

        if (info.IsLink)
        {
            if (string.IsNullOrWhiteSpace(info.Tenant) || !Guid.TryParse(info.LinkUserId, out Guid userId))
                return info.RedirectWithError(options, "failed");

            Result<Success> linked = await sender.Send(
                new LinkExternalLoginCommand(info.Tenant, userId, info.Provider, info.ProviderDisplayName, info.ProviderKey),
                ct
            );

            return linked.IsSuccess
                ? Results.Redirect(QueryHelpers.AddQueryString(info.ReturnUrl, "externalLinked", info.Provider))
                : info.RedirectWithError(options, linked.FirstError.Code);
        }

        if (string.IsNullOrWhiteSpace(info.Tenant))
            return info.RedirectWithError(options, "tenant_required");

        Result<LoginResult> result = await sender.Send(
            new ExternalLoginCommand(
                info.Tenant,
                info.Provider,
                info.ProviderDisplayName,
                info.ProviderKey,
                info.Email,
                info.EmailVerified,
                info.GivenName,
                info.FamilyName,
                info.Name,
                http.Connection.RemoteIpAddress?.ToString()
            ),
            ct
        );

        if (result.IsFailure)
            return info.RedirectWithError(options, result.FirstError.Code);

        LoginResult login = result.Value;
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

        AuthResult session = login.Session!;
        services.GetRequiredService<TwoFactorCookie>().Clear(http.Response);
        services.GetRequiredService<IAuthCookieService>().SetTokens(http.Response, session.AccessToken, session.RefreshToken);
        return Results.Redirect(info.ReturnUrl);
    }
}
