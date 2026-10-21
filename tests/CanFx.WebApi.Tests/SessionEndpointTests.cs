using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using CanFx.Security.Events;
using CanFx.Security.Sessions;
using CanFx.Security.Tokens;
using CanFx.WebApi.DependencyInjection;
using CanFx.WebApi.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace CanFx.WebApi.Tests;

public class SessionEndpointTests
{
    /// <summary>Kullanıcı "42"nin üç oturumu: s1, s2, s3.</summary>
    private sealed class FakeSessions : IUserSessionService
    {
        public List<string> Open { get; } = ["s1", "s2", "s3"];

        public string? LastCurrent { get; private set; }

        public Task<IReadOnlyList<UserSession>> ListAsync(string userId, string? currentSessionId = null, CancellationToken cancellationToken = default)
        {
            LastCurrent = currentSessionId;
            IReadOnlyList<UserSession> list = userId != "42" ? [] : Open.Select(id =>
                new UserSession(id, null, "Cihaz " + id, "test", "127.0.0.1", DateTimeOffset.UnixEpoch, null, DateTimeOffset.MaxValue, id == currentSessionId)).ToList();
            return Task.FromResult(list);
        }

        public Task<bool> RevokeAsync(string userId, string sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(userId == "42" && Open.Remove(sessionId));

        public Task<int> RevokeOthersAsync(string userId, string currentSessionId, CancellationToken cancellationToken = default)
        {
            int removed = Open.RemoveAll(id => id != currentSessionId);
            return Task.FromResult(removed);
        }

        public Task<bool> IsKnownDeviceAsync(string userId, string deviceId, CancellationToken cancellationToken = default) => Task.FromResult(false);

        public Task<int> RevokeAllAsync(string userId, CancellationToken cancellationToken = default)
        {
            int removed = Open.Count;
            Open.Clear();
            return Task.FromResult(removed);
        }
    }

    private sealed class RecordingSink : ISecurityEventSink
    {
        public List<SecurityEvent> Events { get; } = [];

        public ValueTask WriteAsync(SecurityEvent securityEvent, CancellationToken cancellationToken = default)
        {
            lock (Events)
                Events.Add(securityEvent);
            return ValueTask.CompletedTask;
        }
    }

    private static readonly RecordingSink Sink = new();

    private static async Task<(WebApplication App, HttpClient Client, FakeSessions Sessions)> CreateAsync()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseTestServer();
        builder.Services.AddCanWebApi();
        builder.Services.AddCanAccessTokenRevocation();
        builder.Services.AddSingleton<IAuthCookieService>(new AuthCookieService(new AuthCookieOptions()));
        var sessions = new FakeSessions();
        builder.Services.AddSingleton<IUserSessionService>(sessions);
        builder.Services.AddCanSecurityEvents(o => o.WriteToLog = false);
        builder.Services.AddSingleton<ISecurityEventSink>(Sink);

        WebApplication app = builder.Build();
        app.UseCanExceptionHandler();
        app.Use(async (context, next) =>
        {
            // test kimliği: X-User ve X-Sid başlıklarından
            if (context.Request.Headers.TryGetValue("X-User", out var user))
            {
                var claims = new List<Claim> { new("sub", user.ToString()), new("jti", Guid.NewGuid().ToString("N")) };
                if (context.Request.Headers.TryGetValue("X-Sid", out var sid))
                    claims.Add(new Claim(TokenService.SessionIdClaimType, sid.ToString()));
                context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
            }

            await next(context);
        });
        app.UseCanTenantResolution();
        app.UseCanAccessTokenRevocation();
        app.MapCanSessions();
        app.MapGet("/me", () => "ok");
        await app.StartAsync();
        return (app, app.GetTestClient(), sessions);
    }

    private static HttpRequestMessage As(HttpMethod method, string path, string? user = "42", string? sid = "s1")
    {
        var request = new HttpRequestMessage(method, path);
        if (user is not null)
            request.Headers.Add("X-User", user);
        if (sid is not null)
            request.Headers.Add("X-Sid", sid);
        return request;
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("code").GetString();

    [Fact]
    public async Task Lists_the_users_sessions_and_marks_this_device()
    {
        (WebApplication app, HttpClient client, FakeSessions sessions) = await CreateAsync();
        await using (app)
        {
            HttpResponseMessage response = await client.SendAsync(As(HttpMethod.Get, "/api/sessions", sid: "s2"), TestContext.Current.CancellationToken);
            JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

            Assert.Equal("s2", sessions.LastCurrent);
            JsonElement[] list = body.GetProperty("sessions").EnumerateArray().ToArray();
            Assert.Equal(3, list.Length);
            Assert.Equal("s2", Assert.Single(list, s => s.GetProperty("isCurrent").GetBoolean()).GetProperty("sessionId").GetString());
        }
    }

    [Fact]
    public async Task Revoking_another_device_keeps_this_one_and_unknown_sessions_are_404()
    {
        (WebApplication app, HttpClient client, FakeSessions sessions) = await CreateAsync();
        await using (app)
        {
            HttpResponseMessage revoked = await client.SendAsync(As(HttpMethod.Delete, "/api/sessions/s2"), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
            Assert.False(revoked.Headers.Contains("Set-Cookie")); // bu cihaz oturumda kalır
            Assert.Equal(new[] { "s1", "s3" }, sessions.Open);

            HttpResponseMessage missing = await client.SendAsync(As(HttpMethod.Delete, "/api/sessions/yok"), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            Assert.Equal("session_not_found", await CodeAsync(missing));
        }
    }

    [Fact]
    public async Task Revoking_the_current_session_also_clears_the_cookies()
    {
        (WebApplication app, HttpClient client, _) = await CreateAsync();
        await using (app)
        {
            HttpResponseMessage response = await client.SendAsync(As(HttpMethod.Delete, "/api/sessions/s1"), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            Assert.True(response.Headers.Contains("Set-Cookie"));
        }
    }

    [Fact]
    public async Task Revoke_others_needs_a_session_id()
    {
        (WebApplication app, HttpClient client, FakeSessions sessions) = await CreateAsync();
        await using (app)
        {
            HttpResponseMessage noSid = await client.SendAsync(As(HttpMethod.Post, "/api/sessions/revoke-others", sid: null), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.BadRequest, noSid.StatusCode);
            Assert.Equal("session_unknown", await CodeAsync(noSid));

            HttpResponseMessage response = await client.SendAsync(As(HttpMethod.Post, "/api/sessions/revoke-others"), TestContext.Current.CancellationToken);
            Assert.Equal(2, (await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("revoked").GetInt32());
            Assert.Equal(new[] { "s1" }, sessions.Open);
        }
    }

    [Fact]
    public async Task Anonymous_requests_are_rejected()
    {
        (WebApplication app, HttpClient client, _) = await CreateAsync();
        await using (app)
        {
            HttpResponseMessage response = await client.SendAsync(As(HttpMethod.Get, "/api/sessions", user: null, sid: null), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [Fact]
    public async Task Access_tokens_of_a_closed_session_are_rejected_immediately()
    {
        (WebApplication app, HttpClient client, _) = await CreateAsync();
        await using (app)
        {
            IAccessTokenDenylist denylist = app.Services.GetRequiredService<IAccessTokenDenylist>();
            await denylist.RevokeAsync(AccessTokenDenylistKeys.Session("s2"), DateTimeOffset.UtcNow.AddMinutes(15), TestContext.Current.CancellationToken);

            HttpRequestMessage request = As(HttpMethod.Get, "/me", user: "77", sid: "s2");
            request.Headers.UserAgent.ParseAdd("CanTest/1.0");
            HttpResponseMessage closed = await client.SendAsync(request, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Unauthorized, closed.StatusCode);
            Assert.Equal("session_revoked", await CodeAsync(closed));

            // olay isteğin bilgisiyle zenginleşti
            SecurityEvent used;
            lock (Sink.Events)
                used = Sink.Events.Single(e => e.UserId == "77");
            Assert.Equal(SecurityEventTypes.RevokedTokenUsed, used.Type);
            Assert.Equal("session_revoked", used.Reason);
            Assert.Equal(SecurityEventSeverity.Info, used.Severity);
            Assert.Equal("s2", used.SessionId);
            Assert.Equal("CanTest/1.0", used.UserAgent);

            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(As(HttpMethod.Get, "/me", sid: "s1"), TestContext.Current.CancellationToken)).StatusCode);
        }
    }
}
