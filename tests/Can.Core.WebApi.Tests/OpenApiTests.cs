using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Can.Core.WebApi.DependencyInjection;
using Can.Core.WebApi.Idempotency;
using Can.Core.WebApi.OpenApi;
using Can.Core.WebApi.Versioning;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Can.Core.WebApi.Tests;

public class OpenApiTests
{
    public sealed record CreateOrder(string Product, int Quantity);

    private static async Task<(WebApplication App, HttpClient Client)> CreateAsync(string environment = "Development", Action<CanOpenApiOptions>? configure = null)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.WebHost.UseTestServer();
        builder.Services.AddCanWebApi();
        builder.Services.AddAuthorization();
        builder.Services.AddCanIdempotency();
        builder.Services.AddCanApiVersioning(o =>
        {
            o.Add(new ApiVersionInfo(1) { DeprecatedAt = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), Sunset = new(2027, 3, 1, 0, 0, 0, TimeSpan.Zero) });
            o.Add(new ApiVersionInfo(2));
        });
        builder.Services.AddCanOpenApi(o =>
        {
            o.Title = "Mağaza API";
            configure?.Invoke(o);
        });

        WebApplication app = builder.Build();
        app.UseCanApiVersioning();
        app.MapApiVersion(1).MapGet("/orders", () => TypedResults.Ok(Array.Empty<string>())).RequireAuthorization();
        RouteGroupBuilder v2 = app.MapApiVersion(2);
        v2.MapGet("/orders", () => TypedResults.Ok(Array.Empty<string>())).RequireAuthorization("orders.read");
        v2.MapPost("/orders", (CreateOrder order) => TypedResults.Created("/api/v2/orders/1", order)).WithIdempotency().RequireAuthorization();
        app.MapGet("/api/health-info", () => "ok"); // sürümsüz: her belgede
        app.MapCanOpenApi();
        await app.StartAsync();
        return (app, app.GetTestClient());
    }

    private static async Task<JsonElement> DocumentAsync(HttpClient client, string name)
    {
        HttpResponseMessage response = await client.GetAsync($"/openapi/{name}.json", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Each_api_version_gets_its_own_document_and_deprecated_versions_are_marked()
    {
        (WebApplication app, HttpClient client) = await CreateAsync();
        await using (app)
        {
            JsonElement v1 = await DocumentAsync(client, "v1");
            JsonElement v2 = await DocumentAsync(client, "v2");

            Assert.Equal("Mağaza API", v1.GetProperty("info").GetProperty("title").GetString());
            Assert.Equal("v1", v1.GetProperty("info").GetProperty("version").GetString());
            Assert.Contains("2027-03-01", v1.GetProperty("info").GetProperty("description").GetString(), StringComparison.Ordinal);

            JsonElement v1Paths = v1.GetProperty("paths");
            Assert.True(v1Paths.TryGetProperty("/api/v1/orders", out JsonElement v1Orders));
            Assert.False(v1Paths.TryGetProperty("/api/v2/orders", out _));
            Assert.True(v1Paths.TryGetProperty("/api/health-info", out _));
            Assert.True(v1Orders.GetProperty("get").GetProperty("deprecated").GetBoolean());

            JsonElement v2Get = v2.GetProperty("paths").GetProperty("/api/v2/orders").GetProperty("get");
            Assert.False(v2Get.TryGetProperty("deprecated", out JsonElement deprecated) && deprecated.GetBoolean());
        }
    }

    [Fact]
    public async Task Cookie_security_and_problem_responses_are_described()
    {
        (WebApplication app, HttpClient client) = await CreateAsync();
        await using (app)
        {
            JsonElement v2 = await DocumentAsync(client, "v2");

            JsonElement cookie = v2.GetProperty("components").GetProperty("securitySchemes").GetProperty(CanOpenApiExtensions.CookieScheme);
            Assert.Equal("apiKey", cookie.GetProperty("type").GetString());
            Assert.Equal("cookie", cookie.GetProperty("in").GetString());
            Assert.Equal(new AuthCookieOptions().AccessTokenCookieName, cookie.GetProperty("name").GetString());
            Assert.False(v2.GetProperty("components").GetProperty("securitySchemes").TryGetProperty(CanOpenApiExtensions.BearerScheme, out _));

            JsonElement get = v2.GetProperty("paths").GetProperty("/api/v2/orders").GetProperty("get");
            Assert.True(get.GetProperty("security")[0].TryGetProperty(CanOpenApiExtensions.CookieScheme, out _));
            JsonElement responses = get.GetProperty("responses");
            Assert.True(responses.TryGetProperty("401", out JsonElement unauthorized));
            Assert.True(unauthorized.GetProperty("content").TryGetProperty("application/problem+json", out _));
            Assert.True(responses.TryGetProperty("403", out _)); // politika var
            Assert.True(responses.TryGetProperty("500", out _));
            Assert.True(responses.TryGetProperty("200", out _)); // uç noktanın kendi yanıtı korunur

            JsonElement health = v2.GetProperty("paths").GetProperty("/api/health-info").GetProperty("get");
            Assert.False(health.TryGetProperty("security", out _));
            Assert.False(health.GetProperty("responses").TryGetProperty("401", out _));
        }
    }

    [Fact]
    public async Task Idempotent_endpoints_document_the_key_header_and_its_errors()
    {
        (WebApplication app, HttpClient client) = await CreateAsync();
        await using (app)
        {
            JsonElement post = (await DocumentAsync(client, "v2")).GetProperty("paths").GetProperty("/api/v2/orders").GetProperty("post");

            JsonElement header = post.GetProperty("parameters").EnumerateArray().Single(p => p.GetProperty("name").GetString() == "Idempotency-Key");
            Assert.Equal("header", header.GetProperty("in").GetString());
            Assert.True(header.GetProperty("required").GetBoolean());

            JsonElement responses = post.GetProperty("responses");
            Assert.True(responses.TryGetProperty("400", out _)); // gövdesi var: doğrulama hatası
            Assert.True(responses.TryGetProperty("409", out _));
            Assert.True(responses.TryGetProperty("422", out _));
        }
    }

    [Fact]
    public async Task Reference_page_lists_every_document()
    {
        (WebApplication app, HttpClient client) = await CreateAsync();
        await using (app)
        {
            HttpResponseMessage response = await client.GetAsync("/docs", TestContext.Current.CancellationToken);
            string html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

            Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
            Assert.Contains("/openapi/v1.json", html, StringComparison.Ordinal);
            Assert.Contains("/openapi/v2.json", html, StringComparison.Ordinal);
            Assert.Contains("Mağaza API", html, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Nothing_is_published_in_production_unless_asked()
    {
        (WebApplication app, HttpClient client) = await CreateAsync("Production");
        await using (app)
        {
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/openapi/v2.json", TestContext.Current.CancellationToken)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/docs", TestContext.Current.CancellationToken)).StatusCode);
        }

        (WebApplication exposed, HttpClient exposedClient) = await CreateAsync("Production", o => o.ExposeInProduction = true);
        await using (exposed)
            Assert.Equal(HttpStatusCode.OK, (await exposedClient.GetAsync("/openapi/v2.json", TestContext.Current.CancellationToken)).StatusCode);
    }
}
