using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CanFx.WebApi.DependencyInjection;
using CanFx.WebApi.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace CanFx.WebApi.Tests;

public class SecurityHeadersTests
{
    private static async Task<(WebApplication App, HttpClient Client)> CreateAsync(string environment = "Production", Action<SecurityHeadersOptions>? configure = null, bool enabled = true)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.WebHost.UseTestServer();
        builder.Services.AddCanWebApi();
        if (enabled)
            builder.Services.AddCanSecurityHeaders(configure);

        WebApplication app = builder.Build();
        if (enabled)
            app.UseCanSecurityHeaders();
        app.UseCanExceptionHandler();
        app.MapGet("/api/data", () => TypedResults.Ok(1));
        app.MapGet("/api/framed", (HttpContext http) =>
        {
            http.Response.Headers["X-Frame-Options"] = "SAMEORIGIN"; // uç noktanın kendi kararı
            return TypedResults.Ok(1);
        });
        app.MapGet("/docs", () => Results.Content("<html></html>", "text/html"));
        await app.StartAsync();
        return (app, app.GetTestClient());
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out IEnumerable<string>? values) ? string.Join(",", values) : null;

    [Fact]
    public async Task Api_responses_get_strict_browser_headers()
    {
        (WebApplication app, HttpClient client) = await CreateAsync();
        await using (app)
        {
            HttpResponseMessage response = await client.GetAsync("https://localhost/api/data", TestContext.Current.CancellationToken);

            Assert.Equal("nosniff", Header(response, "X-Content-Type-Options"));
            Assert.Equal("DENY", Header(response, "X-Frame-Options"));
            Assert.Equal(SecurityHeadersOptions.ApiContentSecurityPolicy, Header(response, "Content-Security-Policy"));
            Assert.Equal("strict-origin-when-cross-origin", Header(response, "Referrer-Policy"));
            Assert.Equal("same-origin", Header(response, "Cross-Origin-Resource-Policy"));
            Assert.Equal("0", Header(response, "X-XSS-Protection"));
            Assert.Equal("max-age=31536000; includeSubDomains", Header(response, "Strict-Transport-Security"));
        }
    }

    [Fact]
    public async Task Hsts_is_only_sent_over_https_and_outside_development()
    {
        (WebApplication app, HttpClient client) = await CreateAsync();
        await using (app)
            Assert.Null(Header(await client.GetAsync("http://localhost/api/data", TestContext.Current.CancellationToken), "Strict-Transport-Security"));

        (WebApplication dev, HttpClient devClient) = await CreateAsync("Development");
        await using (dev)
            Assert.Null(Header(await devClient.GetAsync("https://localhost/api/data", TestContext.Current.CancellationToken), "Strict-Transport-Security"));
    }

    [Fact]
    public async Task Endpoint_headers_win_and_paths_can_have_their_own_policy()
    {
        (WebApplication app, HttpClient client) = await CreateAsync(configure: o => o.ContentSecurityPolicyReportOnly = true);
        await using (app)
        {
            Assert.Equal("SAMEORIGIN", Header(await client.GetAsync("/api/framed", TestContext.Current.CancellationToken), "X-Frame-Options"));

            HttpResponseMessage docs = await client.GetAsync("/docs", TestContext.Current.CancellationToken);
            Assert.Equal(SecurityHeadersOptions.ApiReferenceContentSecurityPolicy, Header(docs, "Content-Security-Policy-Report-Only"));
            Assert.Null(Header(docs, "Content-Security-Policy"));
        }
    }

    [Fact]
    public async Task Nothing_is_added_unless_enabled()
    {
        (WebApplication app, HttpClient client) = await CreateAsync(enabled: false);
        await using (app)
        {
            HttpResponseMessage response = await client.GetAsync("/api/data", TestContext.Current.CancellationToken);
            Assert.Null(Header(response, "Content-Security-Policy"));
            Assert.Null(Header(response, "X-Frame-Options"));
        }
    }
}

public class CorsTests
{
    private static async Task<(WebApplication App, HttpClient Client)> CreateAsync(string environment = "Production")
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.WebHost.UseTestServer();
        builder.Services.AddCanWebApi();
        builder.Services.AddCanCors(o => o.AllowedOrigins.Add("https://app.example.com"));

        WebApplication app = builder.Build();
        app.UseRouting();
        app.UseCanCors();
        app.MapPost("/api/orders", () => TypedResults.Ok(1));
        await app.StartAsync();
        return (app, app.GetTestClient());
    }

    private static HttpRequestMessage Preflight(string origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Options, "/api/orders");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "content-type,x-xsrf-token");
        return request;
    }

    [Fact]
    public async Task Listed_origin_gets_credentials_and_exposed_headers_others_get_nothing()
    {
        (WebApplication app, HttpClient client) = await CreateAsync();
        await using (app)
        {
            HttpResponseMessage allowed = await client.SendAsync(Preflight("https://app.example.com"), TestContext.Current.CancellationToken);
            Assert.Equal("https://app.example.com", allowed.Headers.GetValues("Access-Control-Allow-Origin").Single());
            Assert.Equal("true", allowed.Headers.GetValues("Access-Control-Allow-Credentials").Single());
            Assert.Equal("600", allowed.Headers.GetValues("Access-Control-Max-Age").Single());

            var post = new HttpRequestMessage(HttpMethod.Post, "/api/orders");
            post.Headers.Add("Origin", "https://app.example.com");
            HttpResponseMessage actual = await client.SendAsync(post, TestContext.Current.CancellationToken);
            Assert.Contains("ETag", actual.Headers.GetValues("Access-Control-Expose-Headers").Single(), StringComparison.Ordinal);

            HttpResponseMessage evil = await client.SendAsync(Preflight("https://evil.example"), TestContext.Current.CancellationToken);
            Assert.False(evil.Headers.Contains("Access-Control-Allow-Origin")); // gelen origin geri yansıtılmaz

            HttpResponseMessage localhost = await client.SendAsync(Preflight("http://localhost:5173"), TestContext.Current.CancellationToken);
            Assert.False(localhost.Headers.Contains("Access-Control-Allow-Origin")); // Production
        }
    }

    [Fact]
    public async Task Localhost_is_allowed_in_development()
    {
        (WebApplication app, HttpClient client) = await CreateAsync("Development");
        await using (app)
        {
            HttpResponseMessage response = await client.SendAsync(Preflight("http://localhost:5173"), TestContext.Current.CancellationToken);
            Assert.Equal("http://localhost:5173", response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        }
    }

    [Fact]
    public void Wildcard_with_credentials_and_malformed_origins_are_rejected()
    {
        var services = new ServiceCollection();
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => services.AddCanCors(o =>
        {
            o.AllowedOrigins.Add("*");
            o.AllowedOrigins.Add("https://app.example.com/");
        }));
        Assert.Contains("\"*\"", error.Message, StringComparison.Ordinal);
        Assert.Contains("https://app.example.com/", error.Message, StringComparison.Ordinal);

        services.AddCanCors(o => { o.AllowedOrigins.Add("*"); o.AllowCredentials = false; }); // kimlik bilgisiz herkese açık API
    }
}

public class CsrfTests
{
    private static async Task<(WebApplication App, HttpClient Client)> CreateAsync(Action<CsrfOptions>? configure = null, SameSiteMode? sameSite = null)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseTestServer();
        builder.Services.AddCanWebApi();
        if (sameSite is { } mode)
            builder.Services.AddSingleton(new AuthCookieOptions { SameSite = mode });
        builder.Services.AddCanCors(o => o.AllowedOrigins.Add("https://app.example.com"));
        builder.Services.AddCanCsrfProtection(configure);

        WebApplication app = builder.Build();
        app.UseCanExceptionHandler();
        app.UseRouting();
        app.UseCanCsrfProtection();
        app.MapGet("/api/me", () => TypedResults.Ok(1));
        app.MapPost("/api/transfer", () => TypedResults.Ok("gönderildi"));
        app.MapPost("/webhooks/payment", () => TypedResults.Ok("alındı")).DisableCsrfProtection();
        await app.StartAsync();
        return (app, app.GetTestClient());
    }

    private static HttpRequestMessage Post(string path, string? site = null, string? origin = null, string? cookie = null, string? header = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "https://api.example.com" + path);
        if (site is not null)
            request.Headers.Add("Sec-Fetch-Site", site);
        if (origin is not null)
            request.Headers.Add("Origin", origin);
        if (cookie is not null)
            request.Headers.Add("Cookie", "XSRF-TOKEN=" + cookie);
        if (header is not null)
            request.Headers.Add("X-XSRF-TOKEN", header);
        return request;
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("code").GetString();

    [Fact]
    public async Task Cross_site_form_posts_are_rejected_but_same_origin_trusted_and_non_browser_pass()
    {
        (WebApplication app, HttpClient client) = await CreateAsync();
        await using (app)
        {
            HttpResponseMessage attack = await client.SendAsync(Post("/api/transfer", site: "cross-site", origin: "https://evil.example"), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Forbidden, attack.StatusCode);
            Assert.Equal("csrf_rejected", await CodeAsync(attack));

            // ele geçirilmiş alt alan adı da "same-site" sayılır: güvenilir listede değilse reddedilir
            Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Post("/api/transfer", site: "same-site", origin: "https://blog.example.com"), TestContext.Current.CancellationToken)).StatusCode);

            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Post("/api/transfer", site: "same-origin", origin: "https://api.example.com"), TestContext.Current.CancellationToken)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Post("/api/transfer", site: "cross-site", origin: "https://app.example.com"), TestContext.Current.CancellationToken)).StatusCode); // CORS listesinde
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Post("/api/transfer"), TestContext.Current.CancellationToken)).StatusCode); // mobil/servis: tarayıcı değil
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Post("/webhooks/payment", site: "cross-site", origin: "https://psp.example"), TestContext.Current.CancellationToken)).StatusCode); // muaf

            // eski tarayıcı: Sec-Fetch-Site yok, Origin var
            Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Post("/api/transfer", origin: "https://evil.example"), TestContext.Current.CancellationToken)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Post("/api/transfer", origin: "null"), TestContext.Current.CancellationToken)).StatusCode);
        }
    }

    [Fact]
    public async Task Double_submit_token_is_issued_and_required_when_cookies_are_not_strict()
    {
        (WebApplication app, HttpClient client) = await CreateAsync(sameSite: SameSiteMode.None);
        await using (app)
        {
            // token GET yanıtında cookie olarak gelir; JavaScript okuyabilsin diye HttpOnly değil
            HttpResponseMessage get = await client.GetAsync("https://api.example.com/api/me", TestContext.Current.CancellationToken);
            string setCookie = get.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("XSRF-TOKEN=", StringComparison.Ordinal));
            Assert.DoesNotContain("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
            string token = Uri.UnescapeDataString(setCookie["XSRF-TOKEN=".Length..setCookie.IndexOf(';', StringComparison.Ordinal)]);

            HttpResponseMessage missing = await client.SendAsync(Post("/api/transfer", site: "same-origin", cookie: token), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Forbidden, missing.StatusCode);
            Assert.Equal("csrf_token_invalid", await CodeAsync(missing));

            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Post("/api/transfer", site: "same-origin", cookie: token, header: token), TestContext.Current.CancellationToken)).StatusCode);

            // sunucunun imzalamadığı (uydurma) token: cookie ve başlık aynı olsa da geçmez
            Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Post("/api/transfer", site: "same-origin", cookie: "uydurma", header: "uydurma"), TestContext.Current.CancellationToken)).StatusCode);
        }
    }

    [Fact]
    public async Task Strict_cookies_need_only_the_origin_check()
    {
        (WebApplication app, HttpClient client) = await CreateAsync(sameSite: SameSiteMode.Strict);
        await using (app)
        {
            HttpResponseMessage get = await client.GetAsync("https://api.example.com/api/me", TestContext.Current.CancellationToken);
            Assert.False(get.Headers.Contains("Set-Cookie"));
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Post("/api/transfer", site: "same-origin"), TestContext.Current.CancellationToken)).StatusCode);
        }

        (WebApplication always, HttpClient alwaysClient) = await CreateAsync(o => o.TokenMode = CsrfTokenMode.Always, SameSiteMode.Strict);
        await using (always)
            Assert.Equal(HttpStatusCode.Forbidden, (await alwaysClient.SendAsync(Post("/api/transfer", site: "same-origin"), TestContext.Current.CancellationToken)).StatusCode);
    }
}
