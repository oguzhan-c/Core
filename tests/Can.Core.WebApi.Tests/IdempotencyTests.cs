using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Can.Core.WebApi.DependencyInjection;
using Can.Core.WebApi.Idempotency;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Can.Core.WebApi.Tests;

public class IdempotencyTests
{
    public sealed record CreateOrder(string Product, int Quantity);

    private sealed class Calls
    {
        private int _count;

        public int Count => _count;

        public int Next() => Interlocked.Increment(ref _count);

        /// <summary>Ayarlıysa handler bu görev bitene kadar bekler (eşzamanlılık testi).</summary>
        public TaskCompletionSource? Gate { get; set; }

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool FailNext { get; set; }
    }

    private static async Task<(WebApplication App, HttpClient Client, Calls Calls)> CreateAsync()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseTestServer();
        builder.Services.AddCanWebApi();
        builder.Services.AddCanIdempotency();
        var calls = new Calls();

        WebApplication app = builder.Build();
        app.UseCanExceptionHandler();
        app.Use(async (context, next) =>
        {
            if (context.Request.Headers.TryGetValue("X-Test-User", out var user))
                context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user.ToString())], "Test"));
            await next(context);
        });
        app.UseCanTenantResolution();
        app.UseCanIdempotency();

        app.MapPost("/orders", async (CreateOrder order) =>
        {
            int number = calls.Next();
            calls.Entered.TrySetResult();
            if (calls.Gate is { } gate)
                await gate.Task;
            if (calls.FailNext)
            {
                calls.FailNext = false;
                throw new InvalidOperationException("veritabanı yanıt vermedi");
            }

            return TypedResults.Created($"/orders/{number}", new { number, order.Product, order.Quantity });
        }).WithIdempotency();
        app.MapPost("/optional", () => TypedResults.Ok(calls.Next())).WithIdempotency(required: false);
        app.MapPost("/plain", () => TypedResults.Ok(calls.Next()));

        await app.StartAsync();
        return (app, app.GetTestClient(), calls);
    }

    private static HttpRequestMessage Post(string path, string? key, object? body = null, string? user = "u1")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body ?? new CreateOrder("çay", 2)) };
        if (key is not null)
            request.Headers.Add("Idempotency-Key", key);
        if (user is not null)
            request.Headers.Add("X-Test-User", user);
        return request;
    }

    [Fact]
    public async Task Same_key_returns_the_first_response_without_running_again()
    {
        (WebApplication app, HttpClient client, Calls calls) = await CreateAsync();
        await using (app)
        {
            HttpResponseMessage first = await client.SendAsync(Post("/orders", "k-1"));
            HttpResponseMessage second = await client.SendAsync(Post("/orders", "k-1"));

            Assert.Equal(HttpStatusCode.Created, first.StatusCode);
            Assert.Equal(HttpStatusCode.Created, second.StatusCode);
            Assert.Equal(1, calls.Count);
            Assert.Equal(await first.Content.ReadAsStringAsync(), await second.Content.ReadAsStringAsync());
            Assert.Equal(first.Headers.Location, second.Headers.Location);
            Assert.Equal(first.Content.Headers.ContentType, second.Content.Headers.ContentType);
            Assert.False(first.Headers.Contains("Idempotent-Replayed"));
            Assert.Equal("true", second.Headers.GetValues("Idempotent-Replayed").Single());

            // yeni anahtar: yeni işlem
            HttpResponseMessage third = await client.SendAsync(Post("/orders", "k-2"));
            Assert.Equal(2, calls.Count);
            Assert.Equal("/orders/2", third.Headers.Location!.OriginalString);
        }
    }

    [Fact]
    public async Task Same_key_with_a_different_request_is_rejected()
    {
        (WebApplication app, HttpClient client, Calls calls) = await CreateAsync();
        await using (app)
        {
            await client.SendAsync(Post("/orders", "k-1", new CreateOrder("çay", 2)));
            HttpResponseMessage reused = await client.SendAsync(Post("/orders", "k-1", new CreateOrder("çay", 3)));

            Assert.Equal(HttpStatusCode.UnprocessableEntity, reused.StatusCode);
            Assert.Equal("idempotency_key_reused", (await reused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
            Assert.Equal(1, calls.Count);
        }
    }

    [Fact]
    public async Task Keys_are_scoped_to_the_user()
    {
        (WebApplication app, HttpClient client, Calls calls) = await CreateAsync();
        await using (app)
        {
            await client.SendAsync(Post("/orders", "k-1", user: "u1"));
            HttpResponseMessage other = await client.SendAsync(Post("/orders", "k-1", user: "u2"));

            Assert.Equal(HttpStatusCode.Created, other.StatusCode);
            Assert.False(other.Headers.Contains("Idempotent-Replayed"));
            Assert.Equal(2, calls.Count);
        }
    }

    [Fact]
    public async Task Missing_or_invalid_key()
    {
        (WebApplication app, HttpClient client, Calls calls) = await CreateAsync();
        await using (app)
        {
            HttpResponseMessage missing = await client.SendAsync(Post("/orders", null));
            Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
            Assert.Equal("idempotency_key_required", (await missing.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

            HttpResponseMessage tooLong = await client.SendAsync(Post("/orders", new string('x', 300)));
            Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
            Assert.Equal("idempotency_key_invalid", (await tooLong.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
            Assert.Equal(0, calls.Count);

            // isteğe bağlı: anahtarsız her istek işlenir, anahtarlı tekrar edilmez
            await client.SendAsync(Post("/optional", null));
            await client.SendAsync(Post("/optional", null));
            await client.SendAsync(Post("/optional", "o-1"));
            await client.SendAsync(Post("/optional", "o-1"));
            Assert.Equal(3, calls.Count);

            // işaretsiz uç nokta etkilenmez
            await client.SendAsync(Post("/plain", "p-1"));
            await client.SendAsync(Post("/plain", "p-1"));
            Assert.Equal(5, calls.Count);
        }
    }

    [Fact]
    public async Task Concurrent_request_with_the_same_key_gets_409()
    {
        (WebApplication app, HttpClient client, Calls calls) = await CreateAsync();
        await using (app)
        {
            calls.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<HttpResponseMessage> first = client.SendAsync(Post("/orders", "k-1"));
            await calls.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

            HttpResponseMessage second = await client.SendAsync(Post("/orders", "k-1"));
            Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
            Assert.Equal("idempotency_in_progress", (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
            Assert.NotNull(second.Headers.RetryAfter);

            calls.Gate.SetResult();
            Assert.Equal(HttpStatusCode.Created, (await first).StatusCode);

            HttpResponseMessage third = await client.SendAsync(Post("/orders", "k-1"));
            Assert.Equal(HttpStatusCode.Created, third.StatusCode);
            Assert.Equal(1, calls.Count);
        }
    }

    [Fact]
    public async Task Server_errors_are_not_stored()
    {
        (WebApplication app, HttpClient client, Calls calls) = await CreateAsync();
        await using (app)
        {
            calls.FailNext = true;
            HttpResponseMessage failed = await client.SendAsync(Post("/orders", "k-1"));
            Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);

            HttpResponseMessage retried = await client.SendAsync(Post("/orders", "k-1"));
            Assert.Equal(HttpStatusCode.Created, retried.StatusCode);
            Assert.False(retried.Headers.Contains("Idempotent-Replayed"));
            Assert.Equal(2, calls.Count);
        }
    }
}
