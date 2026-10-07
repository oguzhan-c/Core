using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Can.Core.WebApi.ExternalLogin;

/// <summary>
/// Dış sağlayıcılarla giriş (Google, Microsoft, GitHub ...): ASP.NET Core'un yerleşik OAuth handler'ı ile, ek paket
/// olmadan. Sağlayıcıdan dönüşte uygulamanın kendi oturumu (cookie'deki JWT) açılır; sağlayıcının token'ı saklanmaz.
/// </summary>
public static class ExternalLoginExtensions
{
    private const string ClaimEmailVerified = "can:email_verified";
    private const string ItemReturnUrl = "can:return_url";
    private const string ItemMode = "can:mode";
    private const string ItemTenant = "can:tenant";
    private const string ItemLinkUser = "can:link_user";
    private const string ItemProvider = "can:provider";

    /// <summary>Dönüş uç noktasının adı (adres, grup ön ekleri dahil buradan üretilir).</summary>
    public const string CallbackEndpointName = "CanExternalLoginCallback";

    /// <summary>Sağlayıcıları kaydeder. ClientId/ClientSecret boş olan sağlayıcılar atlanır.</summary>
    /// <example>
    /// <code>
    /// builder.Services.AddCanExternalLogin(o =&gt; o
    ///     .AddGoogle(config["Auth:Google:ClientId"]!, config["Auth:Google:ClientSecret"]!)
    ///     .AddGitHub(config["Auth:GitHub:ClientId"]!, config["Auth:GitHub:ClientSecret"]!));
    /// </code>
    /// </example>
    public static IServiceCollection AddCanExternalLogin(this IServiceCollection services, Action<CanExternalLoginOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new CanExternalLoginOptions();
        configure(options);
        services.AddSingleton(options);

        AuthenticationBuilder authentication = services
            .AddAuthentication()
            .AddCookie(CanExternalLoginOptions.ExternalScheme, cookie =>
            {
                cookie.Cookie.Name = "can_external";
                cookie.Cookie.HttpOnly = true;
                cookie.Cookie.SameSite = SameSiteMode.Lax; // sağlayıcıdan dönüş (siteler arası GET) cookie'yi taşısın
                cookie.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                cookie.ExpireTimeSpan = options.CookieLifetime;
                cookie.SlidingExpiration = false;
            });

        foreach (ExternalProviderOptions provider in options.Providers)
        {
            authentication.AddOAuth(provider.Name, provider.DisplayName, oauth =>
            {
                oauth.SignInScheme = CanExternalLoginOptions.ExternalScheme;
                oauth.ClientId = provider.ClientId;
                oauth.ClientSecret = provider.ClientSecret;
                oauth.AuthorizationEndpoint = provider.AuthorizationEndpoint;
                oauth.TokenEndpoint = provider.TokenEndpoint;
                oauth.UserInformationEndpoint = provider.UserInformationEndpoint;
                oauth.CallbackPath = $"/signin-{provider.Name}";
                oauth.UsePkce = provider.UsePkce;
                oauth.SaveTokens = false;
                oauth.CorrelationCookie.SameSite = SameSiteMode.Lax;
                oauth.CorrelationCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;

                oauth.Scope.Clear();
                foreach (string scope in provider.Scopes)
                    oauth.Scope.Add(scope);

                oauth.ClaimActions.MapJsonKey(ClaimTypes.NameIdentifier, "sub");
                oauth.ClaimActions.MapJsonKey(ClaimTypes.NameIdentifier, "id"); // GitHub (sayı)
                oauth.ClaimActions.MapJsonKey(ClaimTypes.Email, "email");
                oauth.ClaimActions.MapJsonKey(ClaimTypes.Name, "name");
                oauth.ClaimActions.MapJsonKey(ClaimTypes.GivenName, "given_name");
                oauth.ClaimActions.MapJsonKey(ClaimTypes.Surname, "family_name");

                oauth.Events.OnCreatingTicket = context => OnCreatingTicketAsync(context, provider);
                oauth.Events.OnRemoteFailure = context =>
                {
                    // Kullanıcı izni reddetti ya da sağlayıcı hata döndü: giriş sayfasına hata koduyla dön.
                    context.Response.Redirect(AppendQuery(options.ErrorPath, "externalError", "denied"));
                    context.HandleResponse();
                    return Task.CompletedTask;
                };

                provider.Configure?.Invoke(oauth);
            });
        }

        return services;
    }

    /// <summary>
    /// Dış giriş uç noktaları:
    /// <list type="bullet">
    /// <item><c>GET {prefix}/providers</c>: kayıtlı sağlayıcılar (giriş düğmeleri).</item>
    /// <item><c>GET {prefix}/{provider}/login?tenant=..&amp;returnUrl=..</c>: sağlayıcıya yönlendirir.</item>
    /// <item><c>GET {prefix}/{provider}/link?returnUrl=..</c>: giriş yapmış kullanıcının hesabına bağlar.</item>
    /// <item><c>GET {prefix}/callback</c>: dönüş; kimlik okunur, <paramref name="onCallback"/> oturumu açar.</item>
    /// </list>
    /// </summary>
    /// <param name="endpoints">Uç nokta kurucusu.</param>
    /// <param name="prefix">Ön ek (ör. <c>/api/auth/external</c>).</param>
    /// <param name="onCallback">Uygulamanın kullanıcıyı bulup/oluşturup oturumu açtığı yer; genelde yönlendirme döner.</param>
    public static RouteGroupBuilder MapCanExternalLogin(
        this IEndpointRouteBuilder endpoints,
        string prefix,
        Func<ExternalLoginInfo, HttpContext, Task<IResult>> onCallback)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(onCallback);

        CanExternalLoginOptions options = endpoints.ServiceProvider.GetRequiredService<CanExternalLoginOptions>();
        RouteGroupBuilder group = endpoints.MapGroup(prefix).WithTags("External login");

        group.MapGet("/providers", () => TypedResults.Ok(options.Providers.Select(p => new ExternalProviderInfo(p.Name, p.DisplayName)).ToArray()))
            .AllowAnonymous();

        group.MapGet("/{provider}/login", (string provider, string? tenant, string? returnUrl, HttpContext http, LinkGenerator links) =>
            {
                if (Find(options, provider) is not { } found)
                    return Results.NotFound();

                var properties = new AuthenticationProperties { RedirectUri = CallbackPath(http, links) };
                properties.Items[ItemProvider] = found.Name;
                properties.Items[ItemMode] = ExternalLoginInfo.LoginMode;
                properties.Items[ItemReturnUrl] = LocalOrRoot(returnUrl);
                if (!string.IsNullOrWhiteSpace(tenant))
                    properties.Items[ItemTenant] = tenant.Trim();

                return Results.Challenge(properties, [found.Name]);
            })
            .AllowAnonymous();

        group.MapGet("/{provider}/link", (string provider, string? returnUrl, HttpContext http, LinkGenerator links, CanWebApiOptions webApi) =>
            {
                if (Find(options, provider) is not { } found)
                    return Results.NotFound();

                // Dönüş isteği siteler arası olduğundan (SameSite=Strict) oturum cookie'si gelmez: bağlayan kullanıcı
                // ve tenant, sağlayıcıya giden şifreli state'e konur.
                var properties = new AuthenticationProperties { RedirectUri = CallbackPath(http, links) };
                properties.Items[ItemProvider] = found.Name;
                properties.Items[ItemMode] = ExternalLoginInfo.LinkMode;
                properties.Items[ItemReturnUrl] = LocalOrRoot(returnUrl);
                properties.Items[ItemLinkUser] = FindFirst(http.User, webApi.UserIdClaimTypes);
                properties.Items[ItemTenant] = http.User.FindFirstValue(webApi.TenantClaimType);

                return Results.Challenge(properties, [found.Name]);
            })
            .RequireAuthorization();

        group.MapGet("/callback", async (HttpContext http) =>
            {
                AuthenticateResult external = await http.AuthenticateAsync(CanExternalLoginOptions.ExternalScheme);
                await http.SignOutAsync(CanExternalLoginOptions.ExternalScheme);

                if (!external.Succeeded || external.Principal is null || external.Properties is null)
                    return Results.Redirect(AppendQuery(options.ErrorPath, "externalError", "failed"));

                ClaimsPrincipal principal = external.Principal;
                IDictionary<string, string?> items = external.Properties.Items;
                string provider = items.TryGetValue(ItemProvider, out string? p) && p is not null ? p : string.Empty;
                ExternalProviderOptions? providerOptions = Find(options, provider);
                string? key = principal.FindFirstValue(ClaimTypes.NameIdentifier);

                if (providerOptions is null || string.IsNullOrWhiteSpace(key))
                    return Results.Redirect(AppendQuery(options.ErrorPath, "externalError", "failed"));

                var info = new ExternalLoginInfo(
                    providerOptions.Name,
                    providerOptions.DisplayName,
                    key,
                    principal.FindFirstValue(ClaimTypes.Email),
                    principal.HasClaim(ClaimEmailVerified, "true"),
                    principal.FindFirstValue(ClaimTypes.Name),
                    principal.FindFirstValue(ClaimTypes.GivenName),
                    principal.FindFirstValue(ClaimTypes.Surname),
                    LocalOrRoot(items.TryGetValue(ItemReturnUrl, out string? r) ? r : null),
                    items.TryGetValue(ItemMode, out string? m) && m == ExternalLoginInfo.LinkMode ? ExternalLoginInfo.LinkMode : ExternalLoginInfo.LoginMode,
                    items.TryGetValue(ItemTenant, out string? t) ? t : null,
                    items.TryGetValue(ItemLinkUser, out string? u) ? u : null
                );

                return await onCallback(info, http);
            })
            .WithName(CallbackEndpointName)
            .AllowAnonymous();

        return group;
    }

    /// <summary>Hata koduyla giriş sayfasına dönüş (uygulamanın callback'i için yardımcı).</summary>
    public static IResult RedirectWithError(this ExternalLoginInfo info, CanExternalLoginOptions options, string errorCode)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(options);
        return Results.Redirect(AppendQuery(info.IsLink ? info.ReturnUrl : options.ErrorPath, "externalError", errorCode));
    }

    /// <summary>Kullanıcı bilgisini okur; sağlayıcıya özel ayrıntılar (e-posta doğrulaması, GitHub e-postaları).</summary>
    private static async Task OnCreatingTicketAsync(OAuthCreatingTicketContext context, ExternalProviderOptions provider)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, context.Options.UserInformationEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", context.AccessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("Can.Core"); // GitHub zorunlu tutar

        using HttpResponseMessage response = await context.Backchannel.SendAsync(request, context.HttpContext.RequestAborted);
        response.EnsureSuccessStatusCode();

        using JsonDocument user = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(context.HttpContext.RequestAborted),
            cancellationToken: context.HttpContext.RequestAborted
        );
        context.RunClaimActions(user.RootElement);

        ClaimsIdentity identity = context.Identity!;
        bool verified = false;

        if (provider.Name == "github")
        {
            (string? email, bool emailVerified) = await ReadGitHubEmailAsync(context);
            if (email is not null)
            {
                RemoveClaims(identity, ClaimTypes.Email);
                identity.AddClaim(new Claim(ClaimTypes.Email, email));
                verified = emailVerified;
            }

            if (identity.FindFirst(ClaimTypes.Name) is null && user.RootElement.TryGetProperty("login", out JsonElement login))
                identity.AddClaim(new Claim(ClaimTypes.Name, login.GetString() ?? string.Empty));
        }
        else
        {
            // OIDC userinfo: "email_verified" true/"true"
            verified = !user.RootElement.TryGetProperty("email_verified", out JsonElement value)
                ? provider.TrustEmail
                : value.ValueKind == JsonValueKind.True || (value.ValueKind == JsonValueKind.String && value.GetString() == "true");
        }

        if (verified && provider.TrustEmail && identity.FindFirst(ClaimTypes.Email) is not null)
            identity.AddClaim(new Claim(ClaimEmailVerified, "true"));
    }

    private static async Task<(string? Email, bool Verified)> ReadGitHubEmailAsync(OAuthCreatingTicketContext context)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user/emails");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", context.AccessToken);
        request.Headers.UserAgent.ParseAdd("Can.Core");

        using HttpResponseMessage response = await context.Backchannel.SendAsync(request, context.HttpContext.RequestAborted);
        if (!response.IsSuccessStatusCode)
            return (null, false);

        using JsonDocument emails = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(context.HttpContext.RequestAborted),
            cancellationToken: context.HttpContext.RequestAborted
        );

        foreach (JsonElement item in emails.RootElement.EnumerateArray())
        {
            if (item.TryGetProperty("primary", out JsonElement primary) && primary.ValueKind == JsonValueKind.True)
            {
                bool verified = item.TryGetProperty("verified", out JsonElement v) && v.ValueKind == JsonValueKind.True;
                return (item.GetProperty("email").GetString(), verified);
            }
        }

        return (null, false);
    }

    private static void RemoveClaims(ClaimsIdentity identity, string type)
    {
        foreach (Claim claim in identity.FindAll(type).ToList())
            identity.RemoveClaim(claim);
    }

    private static string CallbackPath(HttpContext http, LinkGenerator links) =>
        links.GetPathByName(http, CallbackEndpointName) ?? throw new InvalidOperationException("Dış giriş dönüş uç noktası bulunamadı.");

    private static ExternalProviderOptions? Find(CanExternalLoginOptions options, string? name) =>
        options.Providers.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    private static string? FindFirst(ClaimsPrincipal user, string[] types) =>
        types.Select(user.FindFirstValue).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    /// <summary>Açık yönlendirmeyi (open redirect) engeller: yalnızca uygulama içi göreli adresler.</summary>
    internal static string LocalOrRoot(string? url) =>
        !string.IsNullOrEmpty(url) && url[0] == '/' && (url.Length == 1 || (url[1] != '/' && url[1] != '\\')) ? url : "/";

    internal static string AppendQuery(string url, string key, string value) =>
        $"{url}{(url.Contains('?', StringComparison.Ordinal) ? '&' : '?')}{Uri.EscapeDataString(key)}={Uri.EscapeDataString(value)}";
}
