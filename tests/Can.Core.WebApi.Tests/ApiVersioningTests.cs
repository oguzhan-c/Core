using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Can.Core.WebApi.DependencyInjection;
using Can.Core.WebApi.Versioning;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;

namespace Can.Core.WebApi.Tests;

public class ApiVersioningTests
{
    private static readonly DateTimeOffset Deprecated = DateTimeOffset.UtcNow.AddDays(-30);
    private static readonly DateTimeOffset Sunset = DateTimeOffset.UtcNow.AddDays(60);

    private static async Task<(WebApplication App, HttpClient Client)> CreateAsync()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseTestServer();
        builder.Services.AddCanWebApi();
        builder.Services.AddCanApiVersioning(o =>
        {
            o.Add(new ApiVersionInfo(1) { DeprecatedAt = DateTimeOffset.UtcNow.AddDays(-400), Sunset = DateTimeOffset.UtcNow.AddDays(-1) });
            o.Add(new ApiVersionInfo(2) { DeprecatedAt = Deprecated, Sunset = Sunset, Link = "https://docs.example/v3-gecis" });
            o.Add(new ApiVersionInfo(3));
            o.DefaultVersion = 3;
            o.UnversionedPaths.Add("/api/auth");
        });

        WebApplication app = builder.Build();
        app.UseCanExceptionHandler();
        app.UseCanApiVersioning();

        foreach (int major in new[] { 1, 2, 3 })
            app.MapApiVersion(major).MapGet("/orders", (HttpContext http) => TypedResults.Ok(new { version = http.GetApiVersion()!.Major }));
        app.MapPost("/api/auth/login", () => TypedResults.Ok("giriş"));
        app.MapGet("/health", () => TypedResults.Ok("ok"));

        await app.StartAsync();
        return (app, app.GetTestClient());
    }

    private static async Task<int> VersionOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version").GetInt32();

    private static async Task<string?> CodeOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString();

    [Fact]
    public async Task Version_comes_from_path_then_header_then_default()
    {
        (WebApplication app, HttpClient client) = await CreateAsync();
        await using (app)
        {
            Assert.Equal(2, await VersionOf(await client.GetAsync("/api/v2/orders")));
            Assert.Equal(2, await VersionOf(await client.GetAsync("/api/V2/orders")));
            Assert.Equal(3, await VersionOf(await client.GetAsync("/api/orders")));

            var request = new HttpRequestMessage(HttpMethod.Get, "/api/orders");
            request.Headers.Add("api-version", "2.0");
            Assert.Equal(2, await VersionOf(await client.SendAsync(request)));

            // yoldaki sürüm başlıktan önce gelir
            request = new HttpRequestMessage(HttpMethod.Get, "/api/v3/orders");
            request.Headers.Add("api-version", "2");
            Assert.Equal(3, await VersionOf(await client.SendAsync(request)));
        }
    }

    [Fact]
    public async Task Deprecated_version_announces_deprecation_and_sunset()
    {
        (WebApplication app, HttpClient client) = await CreateAsync();
        await using (app)
        {
            HttpResponseMessage v2 = await client.GetAsync("/api/v2/orders");
            Assert.Equal(HttpStatusCode.OK, v2.StatusCode);
            Assert.Equal("1, 2, 3", v2.Headers.GetValues("api-supported-versions").Single());
            Assert.Equal("1, 2", v2.Headers.GetValues("api-deprecated-versions").Single());
            Assert.Equal("@" + Deprecated.ToUnixTimeSeconds(), v2.Headers.GetValues("Deprecation").Single());
            Assert.Equal(Sunset.UtcDateTime.ToString("R"), v2.Headers.GetValues("Sunset").Single());
            Assert.Equal("<https://docs.example/v3-gecis>; rel=\"deprecation\"", v2.Headers.GetValues("Link").Single());

            HttpResponseMessage v3 = await client.GetAsync("/api/v3/orders");
            Assert.False(v3.Headers.Contains("Deprecation"));
            Assert.False(v3.Headers.Contains("Sunset"));
        }
    }

    [Fact]
    public async Task Unknown_invalid_and_retired_versions_are_rejected()
    {
        (WebApplication app, HttpClient client) = await CreateAsync();
        await using (app)
        {
            HttpResponseMessage unknown = await client.GetAsync("/api/v9/orders");
            Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
            Assert.Equal("api_version_unsupported", await CodeOf(unknown));
            Assert.Equal("1, 2, 3", unknown.Headers.GetValues("api-supported-versions").Single());

            var invalid = new HttpRequestMessage(HttpMethod.Get, "/api/orders");
            invalid.Headers.Add("api-version", "2.1");
            HttpResponseMessage badHeader = await client.SendAsync(invalid);
            Assert.Equal(HttpStatusCode.BadRequest, badHeader.StatusCode);
            Assert.Equal("api_version_unsupported", await CodeOf(badHeader));

            HttpResponseMessage retired = await client.GetAsync("/api/v1/orders");
            Assert.Equal(HttpStatusCode.Gone, retired.StatusCode);
            Assert.Equal("api_version_retired", await CodeOf(retired));
        }
    }

    [Fact]
    public async Task Unversioned_paths_are_left_alone()
    {
        (WebApplication app, HttpClient client) = await CreateAsync();
        await using (app)
        {
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/auth/login", null)).StatusCode);
            HttpResponseMessage health = await client.GetAsync("/health");
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
            Assert.False(health.Headers.Contains("api-supported-versions"));
        }
    }
}
