using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CanFx.Application;
using CanFx.WebApi.DependencyInjection;
using CanFx.WebApi.Features;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace CanFx.WebApi.Tests;

public class FeatureEndpointTests
{
    private sealed class FixedFeatures(params string[] enabled) : IFeatureFlags
    {
        public Task<bool> IsEnabledAsync(string feature, CancellationToken cancellationToken = default) =>
            Task.FromResult(enabled.Contains(feature, StringComparer.OrdinalIgnoreCase));

        public Task<IReadOnlyList<string>> GetEnabledAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(enabled);
    }

    [Fact]
    public async Task Endpoint_requires_all_features_and_client_can_list_them()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseTestServer();
        builder.Services.AddCanWebApi();
        builder.Services.AddSingleton<IFeatureFlags>(new FixedFeatures("dark-mode", "new-checkout"));

        await using WebApplication app = builder.Build();
        app.UseCanExceptionHandler();
        app.MapPost("/checkout/v2", () => TypedResults.Ok("yeni")).RequireFeature("new-checkout");
        app.MapPost("/checkout/v3", () => TypedResults.Ok("en yeni")).RequireFeature("new-checkout", "checkout-v3");
        app.MapCanFeatures();
        await app.StartAsync();
        HttpClient client = app.GetTestClient();

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/checkout/v2", null)).StatusCode);

        HttpResponseMessage hidden = await client.PostAsync("/checkout/v3", null);
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        Assert.Equal("feature_disabled", (await hidden.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        JsonElement list = await client.GetFromJsonAsync<JsonElement>("/api/features");
        Assert.Equal(new[] { "dark-mode", "new-checkout" }, list.GetProperty("features").EnumerateArray().Select(e => e.GetString()).ToArray());
    }
}
