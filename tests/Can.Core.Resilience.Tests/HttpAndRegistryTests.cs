using System.Net;
using System.Net.Http.Headers;
using Can.Core.Resilience.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Can.Core.Resilience.Tests;

public class HttpResilienceTests
{
    private sealed class ScriptedHandler(params HttpStatusCode[] statuses) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            HttpStatusCode status = statuses[Math.Min(Calls, statuses.Length - 1)];
            Calls++;
            return Task.FromResult(new HttpResponseMessage(status) { RequestMessage = request });
        }
    }

    private static (HttpClient Client, ScriptedHandler Inner) Create(Action<HttpStandardResilienceOptions>? configure, params HttpStatusCode[] statuses)
    {
        var inner = new ScriptedHandler(statuses);
        var services = new ServiceCollection();
        services.AddHttpClient("api", c => c.BaseAddress = new Uri("http://api.test/"))
            .ConfigurePrimaryHttpMessageHandler(() => inner)
            .AddCanStandardResilienceHandler(o =>
            {
                o.Retry.Delay = TimeSpan.FromMilliseconds(1);
                configure?.Invoke(o);
            });

        ServiceProvider provider = services.BuildServiceProvider();
        return (provider.GetRequiredService<IHttpClientFactory>().CreateClient("api"), inner);
    }

    [Fact]
    public async Task Standard_handler_retries_transient_status_codes()
    {
        (HttpClient client, ScriptedHandler inner) = Create(null, HttpStatusCode.ServiceUnavailable, HttpStatusCode.TooManyRequests, HttpStatusCode.OK);

        HttpResponseMessage response = await client.GetAsync("orders");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, inner.Calls);
    }

    [Fact]
    public async Task Client_errors_are_not_retried()
    {
        (HttpClient client, ScriptedHandler inner) = Create(null, HttpStatusCode.BadRequest, HttpStatusCode.OK);

        HttpResponseMessage response = await client.GetAsync("orders");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public async Task Last_response_is_returned_when_retries_are_exhausted()
    {
        (HttpClient client, ScriptedHandler inner) = Create(o => o.Retry.MaxRetryAttempts = 2, HttpStatusCode.InternalServerError);

        HttpResponseMessage response = await client.GetAsync("orders");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(3, inner.Calls);
    }

    [Fact]
    public void Retry_after_header_is_honored()
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(7));

        TimeSpan? delay = HttpPredicates.RetryAfterDelay(new RetryDelayArguments<HttpResponseMessage>(Outcome.FromResult(response), new ResilienceContext(), 0));

        Assert.Equal(TimeSpan.FromSeconds(7), delay);
        Assert.Null(HttpPredicates.RetryAfterDelay(new RetryDelayArguments<HttpResponseMessage>(Outcome.FromResult(new HttpResponseMessage()), new ResilienceContext(), 0)));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, true)]
    [InlineData(HttpStatusCode.BadGateway, true)]
    [InlineData(HttpStatusCode.RequestTimeout, true)]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.NotFound, false)]
    [InlineData(HttpStatusCode.Conflict, false)]
    public void Transient_status_codes(HttpStatusCode status, bool transient) => Assert.Equal(transient, HttpPredicates.IsTransient(status));
}

public class RegistryTests
{
    [Fact]
    public async Task Named_pipelines_are_built_once_and_shared()
    {
        var services = new ServiceCollection();
        int builds = 0;
        services.AddCanResiliencePipeline("payments", (builder, _) =>
        {
            builds++;
            builder.AddRetry(new RetryOptions { MaxRetryAttempts = 1, Delay = TimeSpan.Zero });
        });
        services.AddCanResiliencePipeline<string>("lookup", (builder, _) => builder.AddTimeout(TimeSpan.FromSeconds(1)));

        await using ServiceProvider provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<ResiliencePipelineProvider>();

        Assert.Same(registry.GetPipeline("payments"), registry.GetPipeline("payments"));
        Assert.Equal(1, builds);
        Assert.Equal("x", await registry.GetPipeline<string>("lookup").ExecuteAsync(_ => ValueTask.FromResult("x")));
        Assert.Throws<KeyNotFoundException>(() => registry.GetPipeline("yok"));
    }
}
