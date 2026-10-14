using System.Net;
using System.Security.Claims;
using Can.Core.Application;
using Can.Core.WebApi.Caching;
using Can.Core.WebApi.DependencyInjection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Can.Core.WebApi.Tests;

public class HttpCachingTests
{
    private sealed class Calls
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public int Next() => Interlocked.Increment(ref _count);
    }

    private static async Task<(WebApplication App, HttpClient Client, Calls Calls)> CreateAsync(Action<CanHttpCachingOptions>? configure = null)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseTestServer();
        builder.Services.AddCanWebApi();
        builder.Services.AddCanHttpCaching(configure);
        var calls = new Calls();

        WebApplication app = builder.Build();
        app.UseCanExceptionHandler();
        app.Use(async (context, next) =>
        {
            if (context.Request.Headers.TryGetValue("X-Test-User", out var user))
            {
                List<Claim> claims = [new(ClaimTypes.NameIdentifier, user.ToString())];
                foreach (string role in context.Request.Headers["X-Test-Roles"].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries))
                    claims.Add(new Claim(ClaimTypes.Role, role));
                context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
            }

            await next(context);
        });
        app.UseCanTenantResolution();
        app.UseCanHttpCaching();

        app.MapGet("/me", (HttpContext context) => TypedResults.Ok(new { call = calls.Next(), user = context.User.Identity?.Name })).WithResponseCache();
        app.MapGet("/products", () => TypedResults.Ok(new { call = calls.Next() })).WithResponseCache(ResponseCacheScope.Tenant, "products");
        app.MapGet("/public", () => TypedResults.Ok(new { call = calls.Next() })).WithResponseCache(ResponseCacheScope.Public, TimeSpan.FromMinutes(5));
        app.MapGet("/cookie", (HttpContext context) =>
        {
            context.Response.Cookies.Append("session", "x");
            return TypedResults.Ok(new { call = calls.Next() });
        }).WithResponseCache(ResponseCacheScope.Public);
        app.MapGet("/fails", () => { calls.Next(); return TypedResults.NotFound(); }).WithResponseCache(ResponseCacheScope.Public);
        app.MapGet("/secret", () => TypedResults.Ok(new { code = "ABCDE-12345", call = calls.Next() })).WithNoStore();
        app.MapGet("/plain", () => TypedResults.Ok(new { value = "aynı", call = 1 }));
        app.MapGet("/versioned", (HttpContext context) =>
        {
            context.Response.Headers.ETag = "\"v7\"";
            return TypedResults.Ok(new { call = calls.Next() });
        });
        app.MapGet("/large", () => TypedResults.Text(new string('a', 4096)));
        app.MapPost("/products", () => TypedResults.Ok(calls.Next()));

        await app.StartAsync();
        return (app, app.GetTestClient(), calls);
    }

    private static HttpRequestMessage Get(string path, string? user = null, string? roles = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (user is not null)
            request.Headers.Add("X-Test-User", user);
        if (roles is not null)
            request.Headers.Add("X-Test-Roles", roles);
        return request;
    }

    [Fact]
    public async Task User_scoped_response_is_reused_only_for_the_same_user()
    {
        (WebApplication app, HttpClient client, Calls calls) = await CreateAsync();
        await using (app)
        {
            string first = await (await client.SendAsync(Get("/me", "u1"), TestContext.Current.CancellationToken)).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            string again = await (await client.SendAsync(Get("/me", "u1"), TestContext.Current.CancellationToken)).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.Equal(first, again);
            Assert.Equal(1, calls.Count);

            await client.SendAsync(Get("/me", "u2"), TestContext.Current.CancellationToken);
            await client.SendAsync(Get("/me"), TestContext.Current.CancellationToken);
            Assert.Equal(3, calls.Count);
        }
    }

    [Fact]
    public async Task Tenant_scoped_response_is_shared_only_between_users_with_the_same_roles()
    {
        (WebApplication app, HttpClient client, Calls calls) = await CreateAsync();
        await using (app)
        {
            await client.SendAsync(Get("/products", "u1", "Admin,Sales"), TestContext.Current.CancellationToken);
            await client.SendAsync(Get("/products", "u2", "sales,admin"), TestContext.Current.CancellationToken); // aynı küme, farklı sıra/harf
            Assert.Equal(1, calls.Count);

            await client.SendAsync(Get("/products", "u3", "Sales"), TestContext.Current.CancellationToken); // daha az yetki
            await client.SendAsync(Get("/products"), TestContext.Current.CancellationToken); // anonim
            Assert.Equal(3, calls.Count);
        }
    }

    [Fact]
    public async Task Query_string_is_part_of_the_key()
    {
        (WebApplication app, HttpClient client, Calls calls) = await CreateAsync();
        await using (app)
        {
            await client.GetAsync("/public?page=1", TestContext.Current.CancellationToken);
            await client.GetAsync("/public?page=1", TestContext.Current.CancellationToken);
            await client.GetAsync("/public?page=2", TestContext.Current.CancellationToken);
            Assert.Equal(2, calls.Count);
        }
    }

    [Fact]
    public async Task Invalidating_a_tag_drops_the_cached_responses()
    {
        (WebApplication app, HttpClient client, Calls calls) = await CreateAsync();
        await using (app)
        {
            await client.SendAsync(Get("/products", "u1"), TestContext.Current.CancellationToken);
            await client.SendAsync(Get("/products", "u1"), TestContext.Current.CancellationToken);
            Assert.Equal(1, calls.Count);

            await app.Services.GetServices<ICacheInvalidationTarget>().InvalidateTagsAsync(["products"], TestContext.Current.CancellationToken);

            await client.SendAsync(Get("/products", "u1"), TestContext.Current.CancellationToken);
            Assert.Equal(2, calls.Count);
        }
    }

    [Fact]
    public async Task Responses_that_set_cookies_or_fail_are_never_cached()
    {
        (WebApplication app, HttpClient client, Calls calls) = await CreateAsync();
        await using (app)
        {
            await client.GetAsync("/cookie", TestContext.Current.CancellationToken);
            await client.GetAsync("/cookie", TestContext.Current.CancellationToken);
            await client.GetAsync("/fails", TestContext.Current.CancellationToken);
            await client.GetAsync("/fails", TestContext.Current.CancellationToken);
            Assert.Equal(4, calls.Count);
        }
    }

    [Fact]
    public async Task Unchanged_response_returns_304_without_a_body()
    {
        (WebApplication app, HttpClient client, _) = await CreateAsync();
        await using (app)
        {
            HttpResponseMessage first = await client.GetAsync("/plain", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            Assert.NotNull(first.Headers.ETag);
            Assert.True(first.Headers.ETag!.IsWeak);
            Assert.Equal("no-cache", first.Headers.CacheControl?.ToString());

            var conditional = new HttpRequestMessage(HttpMethod.Get, "/plain");
            conditional.Headers.TryAddWithoutValidation("If-None-Match", first.Headers.ETag.ToString());
            HttpResponseMessage second = await client.SendAsync(conditional, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
            Assert.Empty(await second.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
            Assert.Equal(first.Headers.ETag, second.Headers.ETag);

            var stale = new HttpRequestMessage(HttpMethod.Get, "/plain");
            stale.Headers.TryAddWithoutValidation("If-None-Match", "W/\"eski\"");
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(stale, TestContext.Current.CancellationToken)).StatusCode);
        }
    }

    [Fact]
    public async Task Endpoint_etag_is_kept_and_authenticated_responses_are_private()
    {
        (WebApplication app, HttpClient client, _) = await CreateAsync();
        await using (app)
        {
            HttpResponseMessage response = await client.SendAsync(Get("/versioned", "u1"), TestContext.Current.CancellationToken);
            Assert.Equal("\"v7\"", response.Headers.ETag?.ToString());
            Assert.True(response.Headers.CacheControl?.Private);
            Assert.True(response.Headers.CacheControl?.NoCache);

            HttpRequestMessage conditional = Get("/versioned", "u1");
            conditional.Headers.TryAddWithoutValidation("If-None-Match", "W/\"v7\"");
            Assert.Equal(HttpStatusCode.NotModified, (await client.SendAsync(conditional, TestContext.Current.CancellationToken)).StatusCode);
        }
    }

    [Fact]
    public async Task No_store_endpoint_is_neither_cached_nor_tagged()
    {
        (WebApplication app, HttpClient client, Calls calls) = await CreateAsync();
        await using (app)
        {
            HttpResponseMessage response = await client.SendAsync(Get("/secret", "u1"), TestContext.Current.CancellationToken);
            await client.SendAsync(Get("/secret", "u1"), TestContext.Current.CancellationToken);

            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
            Assert.Null(response.Headers.ETag);
            Assert.Equal(2, calls.Count);
        }
    }

    [Fact]
    public async Task Large_responses_are_streamed_without_an_etag()
    {
        (WebApplication app, HttpClient client, _) = await CreateAsync(o => o.MaximumETagBodySize = 1024);
        await using (app)
        {
            HttpResponseMessage response = await client.GetAsync("/large", TestContext.Current.CancellationToken);
            Assert.Null(response.Headers.ETag);
            Assert.Equal(4096, (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Length);
        }
    }

    [Theory]
    [InlineData("br")]
    [InlineData("gzip")]
    public async Task Responses_are_compressed_for_clients_that_accept_it(string encoding)
    {
        (WebApplication app, HttpClient client, _) = await CreateAsync();
        await using (app)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/large");
            request.Headers.TryAddWithoutValidation("Accept-Encoding", encoding);
            HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);

            Assert.Equal(encoding, Assert.Single(response.Content.Headers.ContentEncoding));
            Assert.True((await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken)).Length < 4096);
        }
    }

    [Fact]
    public async Task Post_requests_are_not_touched()
    {
        (WebApplication app, HttpClient client, Calls calls) = await CreateAsync();
        await using (app)
        {
            HttpResponseMessage response = await client.PostAsync("/products", null, TestContext.Current.CancellationToken);
            await client.PostAsync("/products", null, TestContext.Current.CancellationToken);
            Assert.Null(response.Headers.ETag);
            Assert.Equal(2, calls.Count);
        }
    }

    [Fact]
    public void Use_without_add_explains_what_is_missing()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        WebApplication app = builder.Build();

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => app.UseCanHttpCaching());
        Assert.Contains("AddCanHttpCaching", error.Message, StringComparison.Ordinal);
    }
}
