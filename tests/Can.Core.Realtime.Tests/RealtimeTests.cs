using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Can.Core.MultiTenancy;
using Can.Core.Realtime.SignalR;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Can.Core.Realtime.Tests;

/// <summary>Test kimliği: <c>X-Test-User: kullanıcı;tenant;rol1,rol2</c>.</summary>
public sealed class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string Scheme = "Test";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Test-User", out var value) || string.IsNullOrEmpty(value))
            return Task.FromResult(AuthenticateResult.NoResult());

        string[] parts = value.ToString().Split(';');
        var claims = new List<Claim> { new("sub", parts[0]), new("tenant_id", parts[1]) };
        claims.AddRange(parts[2].Split(',', StringSplitOptions.RemoveEmptyEntries).Select(r => new Claim("role", r)));

        var identity = new ClaimsIdentity(claims, Scheme, "sub", "role");
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme)));
    }
}

public sealed class AllowOrderGroups : IRealtimeSubscriptionAuthorizer
{
    public ValueTask<bool> CanSubscribeAsync(ClaimsPrincipal user, string group, CancellationToken cancellationToken) =>
        ValueTask.FromResult(group.StartsWith("order:", StringComparison.Ordinal));
}

public sealed class RealtimeTests : IAsyncDisposable
{
    private WebApplication? _app;

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
            await _app.DisposeAsync();
    }

    private async Task<TestServer> StartAsync(bool allowOrderGroups = false)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication(TestAuthHandler.Scheme).AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.Scheme, null);
        builder.Services.AddAuthorization();
        builder.Services.AddCanMultiTenancy();
        builder.Services.AddCanSignalR();
        if (allowOrderGroups)
            builder.Services.AddSingleton<IRealtimeSubscriptionAuthorizer, AllowOrderGroups>();

        _app = builder.Build();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.MapCanRealtimeHub();

        // Sunucu tarafında bildirim gönderen uç: tenant'ı istekten seçer (gerçek uygulamada middleware seçer).
        _app.MapPost("/send/{tenant}/{kind}/{target}", async (string tenant, string kind, string target, HttpContext http) =>
        {
            http.RequestServices.GetRequiredService<TenantContext>().Set(tenant);
            IRealtimeNotifier notifier = http.RequestServices.GetRequiredService<IRealtimeNotifier>();
            var message = new RealtimeMessage("test", new { kind, target });

            await (kind switch
            {
                "user" => notifier.SendToUserAsync(target, message),
                "role" => notifier.SendToRoleAsync(target, message),
                "group" => notifier.SendToGroupAsync(target, message),
                _ => notifier.SendToTenantAsync(message),
            });
        });

        await _app.StartAsync();
        return _app.GetTestServer();
    }

    private static async Task<(HubConnection Connection, ConcurrentQueue<string> Received)> ConnectAsync(TestServer server, string? identity)
    {
        var received = new ConcurrentQueue<string>();
        HubConnection connection = new HubConnectionBuilder()
            .WithUrl(
                new Uri(server.BaseAddress, "hubs/notifications"),
                o =>
                {
                    o.HttpMessageHandlerFactory = _ => server.CreateHandler();
                    o.Transports = HttpTransportType.LongPolling;
                    if (identity is not null)
                        o.Headers["X-Test-User"] = identity;
                }
            )
            .Build();

        connection.On<JsonElement>("notify", message =>
            received.Enqueue($"{message.GetProperty("type").GetString()}:{message.GetProperty("data").GetProperty("kind").GetString()}"));

        await connection.StartAsync();
        return (connection, received);
    }

    private static async Task SendAsync(TestServer server, string path)
    {
        using HttpClient client = server.CreateClient();
        (await client.PostAsync(path, null)).EnsureSuccessStatusCode();
    }

    private static async Task EventuallyAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition())
            await Task.Delay(25, timeout.Token);
    }

    [Fact]
    public async Task Messages_reach_only_the_targeted_user_role_and_tenant()
    {
        TestServer server = await StartAsync();
        var (a, receivedA) = await ConnectAsync(server, "u1;t-a;Sales");
        var (b, receivedB) = await ConnectAsync(server, "u2;t-a;Warehouse");
        var (c, receivedC) = await ConnectAsync(server, "u1;t-b;Sales"); // aynı kullanıcı id'si, başka tenant

        await SendAsync(server, "/send/t-a/user/u1");
        await SendAsync(server, "/send/t-a/role/sales");     // rol adı büyük/küçük harf duyarsız
        await SendAsync(server, "/send/t-a/tenant/-");

        await EventuallyAsync(() => receivedA.Count == 3 && receivedB.Count == 1);
        await Task.Delay(300); // C'ye bir şey gelmediğinden emin ol

        Assert.Equal(new[] { "test:user", "test:role", "test:tenant" }, receivedA);
        Assert.Equal(new[] { "test:tenant" }, receivedB);
        Assert.Empty(receivedC);

        await Task.WhenAll(a.DisposeAsync().AsTask(), b.DisposeAsync().AsTask(), c.DisposeAsync().AsTask());
    }

    [Fact]
    public async Task Anonymous_connections_are_rejected()
    {
        TestServer server = await StartAsync();

        await Assert.ThrowsAnyAsync<HttpRequestException>(() => ConnectAsync(server, identity: null));
    }

    [Fact]
    public async Task Custom_groups_need_authorization()
    {
        TestServer server = await StartAsync(allowOrderGroups: true);
        var (a, received) = await ConnectAsync(server, "u1;t-a;Customer");

        Assert.False(await a.InvokeAsync<bool>("Subscribe", "admin:all"));
        Assert.True(await a.InvokeAsync<bool>("Subscribe", "order:42"));

        await SendAsync(server, "/send/t-a/group/order:42");
        await SendAsync(server, "/send/t-b/group/order:42"); // başka tenant'ın aynı adlı grubu

        await EventuallyAsync(() => !received.IsEmpty);
        await Task.Delay(300);
        Assert.Equal(new[] { "test:group" }, received);

        await a.DisposeAsync();
    }
}
