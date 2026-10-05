using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Can.Core.Application;
using Can.Core.MultiTenancy;
using Can.Core.WebApi.DependencyInjection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;

namespace Can.Core.WebApi.Tests;

/// <summary>Tenant deposu kullanıldığında (tenant başına veritabanı senaryosu) çözümleme kuralları.</summary>
public class TenantStoreResolutionTests
{
    private static readonly TenantInfo Acme = new()
    {
        Id = "aaaaaaaa-0000-0000-0000-000000000001",
        Identifier = "acme",
        ConnectionString = "Data Source=acme",
    };

    private static readonly TenantInfo Globex = new() { Id = "bbbbbbbb-0000-0000-0000-000000000002", Identifier = "globex" };

    private static readonly TenantInfo Closed = new()
    {
        Id = "cccccccc-0000-0000-0000-000000000003",
        Identifier = "closed",
        IsActive = false,
    };

    private static async Task<(WebApplication App, HttpClient Client)> CreateAsync()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        builder.Services.AddCanMultiTenancy(o =>
        {
            o.DefaultConnectionString = "Data Source=host";
            o.Tenants = [Acme, Globex, Closed];
        });
        builder.Services.AddCanWebApi();

        WebApplication app = builder.Build();
        app.UseCanExceptionHandler();
        app.Use(async (context, next) =>
        {
            // Gerçek uygulamada imzalı JWT'den gelir; burada sahte kimlik.
            if (context.Request.Headers.TryGetValue("X-Test-Token-Tenant", out var tenant))
            {
                Claim[] claims = [new(ClaimTypes.NameIdentifier, "u-1"), new("tenant_id", tenant.ToString())];
                context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
            }

            await next(context);
        });
        app.UseCanTenantResolution();

        app.MapGet("/tenant", (ICurrentTenant tenant, TenantContext context, ITenantConnectionStringResolver connection) => new
        {
            Id = tenant.Id?.ToString(),
            Identifier = context.Tenant?.Identifier,
            Connection = connection.Resolve(),
        });

        await app.StartAsync();
        return (app, app.GetTestClient());
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> GetAsync(HttpClient client, string? tokenTenant = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/tenant");
        if (tokenTenant is not null)
            request.Headers.Add("X-Test-Token-Tenant", tokenTenant);

        HttpResponseMessage response = await client.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadFromJsonAsync<JsonElement>());
    }

    [Fact]
    public async Task Tenant_from_token_loads_its_dedicated_database()
    {
        (WebApplication app, HttpClient client) = await CreateAsync();
        await using (app)
        {
            var (status, body) = await GetAsync(client, Acme.Id);

            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Equal(Acme.Id, body.GetProperty("id").GetString());
            Assert.Equal("acme", body.GetProperty("identifier").GetString());
            Assert.Equal("Data Source=acme", body.GetProperty("connection").GetString());
        }
    }

    [Fact]
    public async Task Tenant_without_dedicated_database_and_anonymous_requests_use_host()
    {
        (WebApplication app, HttpClient client) = await CreateAsync();
        await using (app)
        {
            Assert.Equal("Data Source=host", (await GetAsync(client, Globex.Id)).Body.GetProperty("connection").GetString());

            var (status, anonymous) = await GetAsync(client);
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Equal(JsonValueKind.Null, anonymous.GetProperty("id").ValueKind);
            Assert.Equal("Data Source=host", anonymous.GetProperty("connection").GetString());
        }
    }

    [Theory]
    [InlineData("dddddddd-0000-0000-0000-000000000004")] // depoda olmayan tenant
    [InlineData("cccccccc-0000-0000-0000-000000000003")] // pasif tenant (token'ın süresi dolmamış olsa bile)
    [InlineData("acme")] // claim'de Id yerine kısa ad: kabul edilmez
    public async Task Unknown_inactive_or_non_id_tenants_are_forbidden(string tokenTenant)
    {
        (WebApplication app, HttpClient client) = await CreateAsync();
        await using (app)
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync(client, tokenTenant)).Status);
        }
    }
}
