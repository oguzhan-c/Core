using Microsoft.AspNetCore.Authentication.OAuth;

namespace Can.Core.WebApi.ExternalLogin;

/// <summary>Bir dış giriş sağlayıcısı (OAuth 2.0 + kullanıcı bilgisi uç noktası).</summary>
public sealed class ExternalProviderOptions
{
    /// <summary>Şema ve adreslerde kullanılan ad: <c>google</c> → <c>/signin-google</c>.</summary>
    public string Name { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string ClientId { get; set; } = string.Empty;

    /// <summary>Gizli anahtar: koda/appsettings'e yazma, user-secrets ya da ortam değişkeni.</summary>
    public string ClientSecret { get; set; } = string.Empty;

    public string AuthorizationEndpoint { get; set; } = string.Empty;

    public string TokenEndpoint { get; set; } = string.Empty;

    public string UserInformationEndpoint { get; set; } = string.Empty;

    public List<string> Scopes { get; } = [];

    /// <summary>PKCE (yetki kodunun çalınmasına karşı). Destekleyen sağlayıcılarda açık bırak.</summary>
    public bool UsePkce { get; set; } = true;

    /// <summary>
    /// Sağlayıcının verdiği e-postaya güvenilir mi (sahiplik doğrulanmış mı). <see langword="false"/> ise e-posta ile
    /// mevcut hesaba otomatik bağlama yapılmamalı. Google'da <c>email_verified</c>, GitHub'da doğrulanmış e-posta kullanılır.
    /// </summary>
    public bool TrustEmail { get; set; }

    /// <summary>OAuth ayarlarında ek değişiklik (ek claim eşlemesi vb.).</summary>
    public Action<OAuthOptions>? Configure { get; set; }
}

public sealed class CanExternalLoginOptions
{
    /// <summary>Sağlayıcıdan dönüşte kimliğin kısa süre tutulduğu cookie şeması.</summary>
    public const string ExternalScheme = "Can.External";

    public List<ExternalProviderOptions> Providers { get; } = [];

    /// <summary>Hata olursa yönlendirilecek sayfa (<c>?externalError=kod</c> eklenir).</summary>
    public string ErrorPath { get; set; } = "/login";

    /// <summary>Dış kimliğin tutulduğu cookie'nin ömrü (yalnızca dönüş anı için).</summary>
    public TimeSpan CookieLifetime { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Google (OpenID Connect userinfo; <c>email_verified</c> kontrol edilir).</summary>
    public CanExternalLoginOptions AddGoogle(string clientId, string clientSecret, Action<ExternalProviderOptions>? configure = null) =>
        Add(new ExternalProviderOptions
        {
            Name = "google",
            DisplayName = "Google",
            ClientId = clientId,
            ClientSecret = clientSecret,
            AuthorizationEndpoint = "https://accounts.google.com/o/oauth2/v2/auth",
            TokenEndpoint = "https://oauth2.googleapis.com/token",
            UserInformationEndpoint = "https://openidconnect.googleapis.com/v1/userinfo",
            Scopes = { "openid", "email", "profile" },
            TrustEmail = true,
        }, configure);

    /// <summary>
    /// Microsoft (kişisel + iş/okul hesapları). Kurumsal hesaplarda e-posta değiştirilebildiği için varsayılan olarak
    /// e-postaya güvenilmez: yalnızca bağlanmış hesaplarla giriş (tek bir kuruluş için <paramref name="tenant"/> ver ve
    /// <c>TrustEmail</c>'i aç).
    /// </summary>
    public CanExternalLoginOptions AddMicrosoft(string clientId, string clientSecret, string tenant = "common", Action<ExternalProviderOptions>? configure = null) =>
        Add(new ExternalProviderOptions
        {
            Name = "microsoft",
            DisplayName = "Microsoft",
            ClientId = clientId,
            ClientSecret = clientSecret,
            AuthorizationEndpoint = $"https://login.microsoftonline.com/{tenant}/oauth2/v2.0/authorize",
            TokenEndpoint = $"https://login.microsoftonline.com/{tenant}/oauth2/v2.0/token",
            UserInformationEndpoint = "https://graph.microsoft.com/oidc/userinfo",
            Scopes = { "openid", "email", "profile" },
            TrustEmail = false,
        }, configure);

    /// <summary>GitHub (birincil ve doğrulanmış e-posta <c>/user/emails</c>'ten okunur).</summary>
    public CanExternalLoginOptions AddGitHub(string clientId, string clientSecret, Action<ExternalProviderOptions>? configure = null) =>
        Add(new ExternalProviderOptions
        {
            Name = "github",
            DisplayName = "GitHub",
            ClientId = clientId,
            ClientSecret = clientSecret,
            AuthorizationEndpoint = "https://github.com/login/oauth/authorize",
            TokenEndpoint = "https://github.com/login/oauth/access_token",
            UserInformationEndpoint = "https://api.github.com/user",
            Scopes = { "read:user", "user:email" },
            TrustEmail = true,
        }, configure);

    public CanExternalLoginOptions Add(ExternalProviderOptions provider, Action<ExternalProviderOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        configure?.Invoke(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(provider.Name);

        if (string.IsNullOrWhiteSpace(provider.ClientId) || string.IsNullOrWhiteSpace(provider.ClientSecret))
            return this; // ayarlanmamış sağlayıcı sessizce atlanır (ör. geliştirmede anahtar yok)

        provider.Name = provider.Name.Trim().ToLowerInvariant();
        Providers.RemoveAll(p => p.Name == provider.Name);
        Providers.Add(provider);
        return this;
    }
}

/// <summary>Sağlayıcıdan dönen kullanıcı bilgisi ve girişin bağlamı.</summary>
/// <param name="Provider">Sağlayıcı adı (<c>google</c>).</param>
/// <param name="ProviderDisplayName">Sağlayıcının görünen adı (<c>Google</c>).</param>
/// <param name="ProviderKey">Sağlayıcıdaki değişmez kullanıcı kimliği.</param>
/// <param name="Email">Sağlayıcının verdiği e-posta (yoksa <see langword="null"/>).</param>
/// <param name="Name">Tam ad.</param>
/// <param name="GivenName">Ad.</param>
/// <param name="FamilyName">Soyad.</param>
/// <param name="ReturnUrl">Girişten sonra dönülecek uygulama içi adres (yalnızca göreli).</param>
/// <param name="EmailVerified">E-postanın sahibi doğrulanmış ve sağlayıcının e-postasına güveniliyor.</param>
/// <param name="Mode"><c>login</c> ya da <c>link</c> (giriş yapmış kullanıcının hesabına bağlama).</param>
/// <param name="Tenant">Girişin yapıldığı tenant (giriş sayfasında seçilen ya da bağlayan kullanıcının).</param>
/// <param name="LinkUserId">Bağlama modunda bağlayan kullanıcının kimliği.</param>
public sealed record ExternalLoginInfo(
    string Provider,
    string ProviderDisplayName,
    string ProviderKey,
    string? Email,
    bool EmailVerified,
    string? Name,
    string? GivenName,
    string? FamilyName,
    string ReturnUrl,
    string Mode,
    string? Tenant,
    string? LinkUserId)
{
    public const string LoginMode = "login";
    public const string LinkMode = "link";

    public bool IsLink => Mode == LinkMode;
}

/// <summary>Uygulamaya açılan sağlayıcı bilgisi (giriş düğmeleri için).</summary>
public sealed record ExternalProviderInfo(string Name, string DisplayName);
