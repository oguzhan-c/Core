using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Can.Core.Application;
using Can.Core.Application.Exceptions;
using Can.Core.Domain.Exceptions;
using Can.Core.WebApi.DependencyInjection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;

namespace Can.Core.WebApi.Tests;

public class WebApiTests
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    /// <summary>Gerçek JWT yerine test header'larından kullanıcı oluşturan sahte authentication.</summary>
    private static async Task FakeAuthentication(HttpContext context, RequestDelegate next)
    {
        if (context.Request.Headers.TryGetValue("X-Test-User", out var userId))
        {
            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, userId.ToString()),
                new(ClaimTypes.Name, $"name-{userId}"),
                new(ClaimTypes.Email, $"{userId}@test.local"),
            };

            foreach (string role in context.Request.Headers["X-Test-Roles"].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries))
                claims.Add(new Claim(ClaimTypes.Role, role));

            foreach (string tenant in context.Request.Headers["X-Test-Tenants"].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries))
                claims.Add(new Claim("tenant_id", tenant));

            context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test"));
        }

        await next(context);
    }

    private static async Task<(WebApplication App, HttpClient Client)> CreateAsync(
        Action<CanWebApiOptions>? configure = null,
        string environment = "Production")
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.WebHost.UseTestServer();
        builder.Services.AddCanWebApi(configure);

        WebApplication app = builder.Build();

        app.UseCanExceptionHandler();
        app.Use(FakeAuthentication);
        app.UseCanTenantResolution();

        app.MapGet("/validation", () => { throw new ValidationException("Name", "Ad boş olamaz."); });
        app.MapGet("/business", () => { throw new BusinessException("Onaylı sipariş iptal edilemez."); });
        app.MapGet("/unauthorized", () => { throw new UnauthorizedException(); });
        app.MapGet("/forbidden", () => { throw new ForbiddenException(); });
        app.MapGet("/notfound", () => { throw NotFoundException.For<string>(42); });
        app.MapGet("/conflict", () => { throw new ConflictException("Aynı isim var."); });
        app.MapGet("/crash", () => { throw new InvalidOperationException("Server=db;Password=gizli"); });

        app.MapGet("/me", (ICurrentUser user) => new
        {
            user.Id,
            user.UserName,
            user.Email,
            Roles = user.Roles.OrderBy(r => r).ToArray(),
            user.IsAuthenticated,
        });

        app.MapGet("/tenant", (ICurrentTenant tenant) => new { Tenant = tenant.Id?.ToString() });

        await app.StartAsync();
        return (app, app.GetTestClient());
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();

    private static HttpRequestMessage Get(string url, string? user = null, string? roles = null, string? tenants = null, string? tenantHeader = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (user is not null) request.Headers.Add("X-Test-User", user);
        if (roles is not null) request.Headers.Add("X-Test-Roles", roles);
        if (tenants is not null) request.Headers.Add("X-Test-Tenants", tenants);
        if (tenantHeader is not null) request.Headers.Add("X-Tenant-Id", tenantHeader);
        return request;
    }

    // ---------------------------------------------------------------- hata → HTTP

    [Theory]
    [InlineData("/validation", 400)]
    [InlineData("/business", 400)]
    [InlineData("/unauthorized", 401)]
    [InlineData("/forbidden", 403)]
    [InlineData("/notfound", 404)]
    [InlineData("/conflict", 409)]
    [InlineData("/crash", 500)]
    public async Task Exceptions_are_mapped_to_problem_details(string path, int expectedStatus)
    {
        (WebApplication app, HttpClient client) = await CreateAsync();
        await using (app)
        {
            HttpResponseMessage response = await client.GetAsync(path);

            Assert.Equal(expectedStatus, (int)response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

            JsonElement body = await JsonAsync(response);
            Assert.Equal(expectedStatus, body.GetProperty("status").GetInt32());
        }
    }

    [Fact]
    public async Task Validation_errors_are_returned_per_field()
    {
        (WebApplication app, HttpClient client) = await CreateAsync();
        await using (app)
        {
            JsonElement body = await JsonAsync(await client.GetAsync("/validation"));

            Assert.Equal("Ad boş olamaz.", body.GetProperty("errors").GetProperty("Name")[0].GetString());
        }
    }

    [Fact]
    public async Task Server_error_details_are_hidden_in_production()
    {
        (WebApplication app, HttpClient client) = await CreateAsync();
        await using (app)
        {
            string body = await (await client.GetAsync("/crash")).Content.ReadAsStringAsync();

            Assert.DoesNotContain("gizli", body);
        }
    }

    [Fact]
    public async Task Server_error_details_are_shown_in_development()
    {
        (WebApplication app, HttpClient client) = await CreateAsync(environment: "Development");
        await using (app)
        {
            string body = await (await client.GetAsync("/crash")).Content.ReadAsStringAsync();

            Assert.Contains("gizli", body);
        }
    }

    // ---------------------------------------------------------------- ICurrentUser

    [Fact]
    public async Task Current_user_is_empty_for_anonymous_requests()
    {
        (WebApplication app, HttpClient client) = await CreateAsync();
        await using (app)
        {
            JsonElement me = await JsonAsync(await client.SendAsync(Get("/me")));

            Assert.Equal(JsonValueKind.Null, me.GetProperty("id").ValueKind);
            Assert.False(me.GetProperty("isAuthenticated").GetBoolean());
            Assert.Equal(0, me.GetProperty("roles").GetArrayLength());
        }
    }

    [Fact]
    public async Task Current_user_is_read_from_claims()
    {
        (WebApplication app, HttpClient client) = await CreateAsync();
        await using (app)
        {
            JsonElement me = await JsonAsync(await client.SendAsync(Get("/me", user: "u-7", roles: "Editor,Admin")));

            Assert.Equal("u-7", me.GetProperty("id").GetString());
            Assert.Equal("name-u-7", me.GetProperty("userName").GetString());
            Assert.Equal("u-7@test.local", me.GetProperty("email").GetString());
            Assert.Equal(new[] { "Admin", "Editor" }, me.GetProperty("roles").EnumerateArray().Select(r => r.GetString()));
            Assert.True(me.GetProperty("isAuthenticated").GetBoolean());
        }
    }

    // ---------------------------------------------------------------- ICurrentTenant

    private static async Task<(HttpStatusCode Status, string? Tenant)> TenantAsync(HttpClient client, HttpRequestMessage request)
    {
        HttpResponseMessage response = await client.SendAsync(request);
        if (!response.IsSuccessStatusCode)
            return (response.StatusCode, null);

        JsonElement body = await JsonAsync(response);
        return (response.StatusCode, body.GetProperty("tenant").GetString());
    }

    [Fact]
    public async Task Single_tenant_membership_is_used_automatically()
    {
        (WebApplication app, HttpClient client) = await CreateAsync();
        await using (app)
        {
            var (status, tenant) = await TenantAsync(client, Get("/tenant", user: "u", tenants: TenantA.ToString()));

            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Equal(TenantA.ToString(), tenant);
        }
    }

    [Fact]
    public async Task Anonymous_and_multi_tenant_without_selection_have_no_tenant()
    {
        (WebApplication app, HttpClient client) = await CreateAsync();
        await using (app)
        {
            Assert.Null((await TenantAsync(client, Get("/tenant"))).Tenant);
            Assert.Null((await TenantAsync(client, Get("/tenant", user: "u", tenants: $"{TenantA},{TenantB}"))).Tenant);
        }
    }

    [Fact]
    public async Task Header_is_ignored_when_not_enabled()
    {
        (WebApplication app, HttpClient client) = await CreateAsync();
        await using (app)
        {
            var (_, tenant) = await TenantAsync(client, Get("/tenant", user: "u", tenants: TenantA.ToString(), tenantHeader: TenantB.ToString()));

            Assert.Equal(TenantA.ToString(), tenant); // header kapalı: token'daki tenant geçerli
        }
    }

    [Fact]
    public async Task Header_selects_a_tenant_the_user_belongs_to()
    {
        (WebApplication app, HttpClient client) = await CreateAsync(o => o.TenantHeaderName = "X-Tenant-Id");
        await using (app)
        {
            var (status, tenant) = await TenantAsync(client, Get("/tenant", user: "u", tenants: $"{TenantA},{TenantB}", tenantHeader: TenantB.ToString()));

            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Equal(TenantB.ToString(), tenant);
        }
    }

    [Fact]
    public async Task Header_for_a_foreign_tenant_is_forbidden()
    {
        (WebApplication app, HttpClient client) = await CreateAsync(o => o.TenantHeaderName = "X-Tenant-Id");
        await using (app)
        {
            var (status, _) = await TenantAsync(client, Get("/tenant", user: "u", tenants: TenantA.ToString(), tenantHeader: TenantB.ToString()));

            Assert.Equal(HttpStatusCode.Forbidden, status);
        }
    }

    [Fact]
    public async Task Tenant_admin_can_select_any_tenant()
    {
        (WebApplication app, HttpClient client) = await CreateAsync(o =>
        {
            o.TenantHeaderName = "X-Tenant-Id";
            o.TenantAdminRole = "SystemAdmin";
        });
        await using (app)
        {
            var (status, tenant) = await TenantAsync(client, Get("/tenant", user: "root", roles: "SystemAdmin", tenantHeader: TenantB.ToString()));

            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Equal(TenantB.ToString(), tenant);
        }
    }
}
