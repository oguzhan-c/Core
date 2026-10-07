using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Can.Core.WebApi.DependencyInjection;
using Can.Core.WebApi.ExternalLogin;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;

namespace Can.Core.WebApi.Tests;

public class ExternalLoginTests
{
    [Fact]
    public async Task Providers_lists_only_configured_ones()
    {
        await using WebApplication app = await StartAsync(new FakeIdentityProvider());
        HttpClient client = app.GetTestClient();

        JsonElement providers = await client.GetFromJsonAsync<JsonElement>("/api/auth/external/providers");

        // GitHub'ın gizli anahtarı boş: atlanır.
        Assert.Equal(new string?[] { "google", "test" }, providers.EnumerateArray().Select(p => p.GetProperty("name").GetString()).Order().ToArray());
    }

    [Fact]
    public async Task Login_redirects_to_provider_with_pkce_and_callback()
    {
        await using WebApplication app = await StartAsync(new FakeIdentityProvider());
        HttpClient client = app.GetTestClient();

        HttpResponseMessage response = await client.GetAsync("/api/auth/external/google/login?tenant=northwind&returnUrl=/orders");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Uri location = response.Headers.Location!;
        Assert.Equal("accounts.google.com", location.Host);
        Dictionary<string, Microsoft.Extensions.Primitives.StringValues> query = QueryHelpers.ParseQuery(location.Query);
        Assert.Equal("google-id", query["client_id"].ToString());
        Assert.Equal("S256", query["code_challenge_method"].ToString());
        Assert.NotEmpty(query["code_challenge"].ToString());
        Assert.Equal("http://localhost/signin-google", query["redirect_uri"].ToString());
        Assert.Contains("email", query["scope"].ToString(), StringComparison.Ordinal);
        Assert.NotEmpty(query["state"].ToString()); // CSRF koruması (korelasyon cookie'siyle eşleşir)
    }

    [Fact]
    public async Task Unknown_provider_is_not_found()
    {
        await using WebApplication app = await StartAsync(new FakeIdentityProvider());

        HttpResponseMessage response = await app.GetTestClient().GetAsync("/api/auth/external/facebook/login");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Callback_without_external_identity_redirects_with_error()
    {
        await using WebApplication app = await StartAsync(new FakeIdentityProvider());

        HttpResponseMessage response = await app.GetTestClient().GetAsync("/api/auth/external/callback");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/login?externalError=failed", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Full_round_trip_reads_identity_and_keeps_tenant_and_return_url()
    {
        var idp = new FakeIdentityProvider();
        await using WebApplication app = await StartAsync(idp);
        HttpClient client = app.GetTestClient();

        // 1) Sağlayıcıya yönlendirme: state + korelasyon cookie'si
        HttpResponseMessage challenge = await client.GetAsync("/api/auth/external/test/login?tenant=northwind&returnUrl=/orders?page=2");
        string state = QueryHelpers.ParseQuery(challenge.Headers.Location!.Query)["state"].ToString();

        // 2) Sağlayıcıdan dönüş: kod token'a çevrilir, kullanıcı bilgisi okunur, kısa ömürlü dış cookie yazılır
        using var signin = new HttpRequestMessage(HttpMethod.Get, $"/signin-test?code=auth-code&state={Uri.EscapeDataString(state)}");
        signin.Headers.Add("Cookie", CookiesOf(challenge));
        HttpResponseMessage returned = await client.SendAsync(signin);
        Assert.Equal(HttpStatusCode.Redirect, returned.StatusCode);
        Assert.Equal("/api/auth/external/callback", returned.Headers.Location!.OriginalString);
        Assert.Contains("code_verifier=", idp.TokenRequestBody, StringComparison.Ordinal); // PKCE

        // 3) Uygulamanın dönüş noktası: kimlik + bağlam
        using var callback = new HttpRequestMessage(HttpMethod.Get, "/api/auth/external/callback");
        callback.Headers.Add("Cookie", CookiesOf(returned));
        HttpResponseMessage final = await client.SendAsync(callback);
        Assert.Equal(HttpStatusCode.OK, final.StatusCode);

        JsonElement info = await final.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("test", info.GetProperty("provider").GetString());
        Assert.Equal("user-123", info.GetProperty("providerKey").GetString());
        Assert.Equal("ada@example.com", info.GetProperty("email").GetString());
        Assert.True(info.GetProperty("emailVerified").GetBoolean());
        Assert.Equal("Ada", info.GetProperty("givenName").GetString());
        Assert.Equal("northwind", info.GetProperty("tenant").GetString());
        Assert.Equal("/orders?page=2", info.GetProperty("returnUrl").GetString());
        Assert.Equal("login", info.GetProperty("mode").GetString());

        // Dış cookie dönüşte silinir: tekrar kullanılamaz.
        Assert.Contains(final.Headers.GetValues("Set-Cookie"), c => c.StartsWith("can_external=;", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unverified_email_is_not_marked_verified()
    {
        var idp = new FakeIdentityProvider { EmailVerified = false };
        await using WebApplication app = await StartAsync(idp);
        HttpClient client = app.GetTestClient();

        HttpResponseMessage challenge = await client.GetAsync("/api/auth/external/test/login?tenant=northwind");
        string state = QueryHelpers.ParseQuery(challenge.Headers.Location!.Query)["state"].ToString();
        using var signin = new HttpRequestMessage(HttpMethod.Get, $"/signin-test?code=c&state={Uri.EscapeDataString(state)}");
        signin.Headers.Add("Cookie", CookiesOf(challenge));
        HttpResponseMessage returned = await client.SendAsync(signin);
        using var callback = new HttpRequestMessage(HttpMethod.Get, "/api/auth/external/callback");
        callback.Headers.Add("Cookie", CookiesOf(returned));

        JsonElement info = await (await client.SendAsync(callback)).Content.ReadFromJsonAsync<JsonElement>();

        Assert.False(info.GetProperty("emailVerified").GetBoolean());
        Assert.Equal("/", info.GetProperty("returnUrl").GetString());
    }

    [Fact]
    public async Task Denied_at_provider_redirects_to_error_page()
    {
        await using WebApplication app = await StartAsync(new FakeIdentityProvider());
        HttpClient client = app.GetTestClient();

        HttpResponseMessage challenge = await client.GetAsync("/api/auth/external/test/login?tenant=northwind");
        string state = QueryHelpers.ParseQuery(challenge.Headers.Location!.Query)["state"].ToString();
        using var denied = new HttpRequestMessage(HttpMethod.Get, $"/signin-test?error=access_denied&state={Uri.EscapeDataString(state)}");
        denied.Headers.Add("Cookie", CookiesOf(challenge));

        HttpResponseMessage response = await client.SendAsync(denied);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/login?externalError=denied", response.Headers.Location!.OriginalString);
    }

    [Theory]
    [InlineData(null, "/")]
    [InlineData("", "/")]
    [InlineData("/orders", "/orders")]
    [InlineData("/", "/")]
    [InlineData("//evil.com", "/")]
    [InlineData("/\\evil.com", "/")]
    [InlineData("https://evil.com", "/")]
    [InlineData("orders", "/")]
    public void Return_url_must_be_local(string? url, string expected) =>
        Assert.Equal(expected, ExternalLoginExtensions.LocalOrRoot(url));

    [Theory]
    [InlineData("/login", "/login?externalError=x%20y")]
    [InlineData("/login?tenant=a", "/login?tenant=a&externalError=x%20y")]
    public void Append_query_keeps_existing_parameters(string url, string expected) =>
        Assert.Equal(expected, ExternalLoginExtensions.AppendQuery(url, "externalError", "x y"));

    // ---------------------------------------------------------------- yardımcılar

    private static async Task<WebApplication> StartAsync(FakeIdentityProvider idp)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseTestServer();
        builder.Services.AddCanWebApi();
        builder.Services.AddCanExternalLogin(o => o
            .AddGoogle("google-id", "google-secret")
            .AddGitHub("github-id", "")
            .Add(new ExternalProviderOptions
            {
                Name = "test",
                DisplayName = "Test",
                ClientId = "test-id",
                ClientSecret = "test-secret",
                AuthorizationEndpoint = "https://idp.test/authorize",
                TokenEndpoint = "https://idp.test/token",
                UserInformationEndpoint = "https://idp.test/userinfo",
                Scopes = { "openid", "email" },
                TrustEmail = true,
                Configure = oauth => oauth.BackchannelHttpHandler = idp,
            }));

        WebApplication app = builder.Build();
        app.UseAuthentication();
        app.MapGroup("/api").MapCanExternalLogin("/auth/external", (info, _) => Task.FromResult<IResult>(TypedResults.Ok(info)));
        await app.StartAsync();
        return app;
    }

    /// <summary>Yanıttaki Set-Cookie'leri sonraki isteğin Cookie başlığına çevirir.</summary>
    private static string CookiesOf(HttpResponseMessage response) =>
        string.Join("; ", response.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0]));

    /// <summary>Token ve kullanıcı bilgisi uç noktalarını taklit eden sağlayıcı.</summary>
    private sealed class FakeIdentityProvider : HttpMessageHandler
    {
        public bool EmailVerified { get; init; } = true;

        public string TokenRequestBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string json;
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/token":
                    TokenRequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
                    json = """{"access_token":"access","token_type":"Bearer","expires_in":3600}""";
                    break;
                case "/userinfo":
                    Assert.Equal("access", request.Headers.Authorization?.Parameter);
                    json = $$"""{"sub":"user-123","email":"ada@example.com","email_verified":{{(EmailVerified ? "true" : "false")}},"name":"Ada Lovelace","given_name":"Ada","family_name":"Lovelace"}""";
                    break;
                default:
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }
}
