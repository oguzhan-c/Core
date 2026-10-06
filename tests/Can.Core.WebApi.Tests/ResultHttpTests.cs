using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Can.Core.Domain.Results;
using Can.Core.WebApi.DependencyInjection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;

namespace Can.Core.WebApi.Tests;

public class ResultHttpTests
{
    private sealed record Item(int Id, string Name);

    private static async Task<(WebApplication App, HttpClient Client)> CreateAsync()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseTestServer();
        builder.Services.AddCanWebApi();

        WebApplication app = builder.Build();
        app.UseCanExceptionHandler();

        app.MapGet("/ok", () => Result.Ok(new Item(1, "Kalem")).ToHttpResult());
        app.MapGet("/empty", () => ((Result<Success>)Result.Success).ToHttpResult());
        app.MapGet("/created", () => Task.FromResult(Result.Ok(new Item(7, "Defter")))
            .ToHttpResult(item => TypedResults.Created($"/items/{item.Id}", item)));
        app.MapGet("/validation", () => Result.Validate(
                Error.Validation("required", "Ad boş olamaz.", "Name"),
                Error.Validation("range", "Fiyat pozitif olmalı.", "Price"))
            .ToHttpResult());
        app.MapGet("/failure", () => Result.Fail<Item>(Error.Failure("order.shipped", "Kargodaki sipariş iptal edilemez.")).ToHttpResult());
        app.MapGet("/notfound", () => Result.Fail<Item>(Error.NotFound("item.not_found", "Ürün yok.")).ToHttpResult());
        app.MapGet("/conflict", () => Result.Fail<Item>(Error.Conflict("item.duplicate", "Aynı ad var.")).ToHttpResult());
        app.MapGet("/unauthorized", () => Result.Fail<Item>(Error.Unauthorized()).ToHttpResult());
        app.MapGet("/forbidden", () => Result.Fail<Item>(Error.Forbidden()).ToHttpResult());
        app.MapGet("/many", () => Result.Validate(Error.Failure("a", "Birinci."), Error.Conflict("b", "İkinci.")).ToHttpResult());

        await app.StartAsync();
        return (app, app.GetTestClient());
    }

    [Fact]
    public async Task Success_returns_value_no_content_or_custom_result()
    {
        (WebApplication app, HttpClient client) = await CreateAsync();
        await using (app)
        {
            HttpResponseMessage ok = await client.GetAsync("/ok");
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
            Assert.Equal("Kalem", (await ok.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("name").GetString());

            Assert.Equal(HttpStatusCode.NoContent, (await client.GetAsync("/empty")).StatusCode);

            HttpResponseMessage created = await client.GetAsync("/created");
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            Assert.Equal("/items/7", created.Headers.Location?.ToString());
        }
    }

    [Theory]
    [InlineData("/failure", 400, "order.shipped")]
    [InlineData("/notfound", 404, "item.not_found")]
    [InlineData("/conflict", 409, "item.duplicate")]
    [InlineData("/unauthorized", 401, "unauthorized")]
    [InlineData("/forbidden", 403, "forbidden")]
    public async Task Errors_become_problem_details_with_code(string path, int status, string code)
    {
        (WebApplication app, HttpClient client) = await CreateAsync();
        await using (app)
        {
            HttpResponseMessage response = await client.GetAsync(path);

            Assert.Equal(status, (int)response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

            JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(status, body.GetProperty("status").GetInt32());
            Assert.Equal(code, body.GetProperty("code").GetString());
            Assert.False(string.IsNullOrEmpty(body.GetProperty("detail").GetString()));
        }
    }

    [Fact]
    public async Task Validation_errors_are_grouped_by_field_like_exceptions()
    {
        (WebApplication app, HttpClient client) = await CreateAsync();
        await using (app)
        {
            HttpResponseMessage response = await client.GetAsync("/validation");
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

            JsonElement errors = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
            Assert.Equal("Ad boş olamaz.", errors.GetProperty("Name")[0].GetString());
            Assert.Equal("Fiyat pozitif olmalı.", errors.GetProperty("Price")[0].GetString());
        }
    }

    [Fact]
    public async Task First_error_decides_status_and_all_errors_are_listed()
    {
        (WebApplication app, HttpClient client) = await CreateAsync();
        await using (app)
        {
            HttpResponseMessage response = await client.GetAsync("/many");
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

            JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("a", body.GetProperty("code").GetString());
            Assert.Equal(2, body.GetProperty("details").GetArrayLength());
        }
    }
}

public class LocalizedProblemTests
{
    private static async Task<(WebApplication App, HttpClient Client, string Directory)> CreateAsync()
    {
        string directory = Path.Combine(Path.GetTempPath(), "can-loc-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "en.json"), """{ "order": { "shipped": "Order {id} is already shipped." } }""");

        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseTestServer();
        builder.Services.AddCanWebApi();
        Can.Core.Localization.LocalizationServiceCollectionExtensions.AddCanLocalization(builder.Services, o => o.ResourcesPath = directory);

        WebApplication app = builder.Build();
        app.UseCanRequestLocalization();
        app.UseCanExceptionHandler();
        app.MapGet("/failure", () => Result.Fail<int>(Error.Failure("order.shipped", "Kargodaki sipariş iptal edilemez.").WithMetadata("id", 42)).ToHttpResult());
        app.MapGet("/forbidden", () => Result.Fail<int>(Error.Forbidden()).ToHttpResult());

        await app.StartAsync();
        return (app, app.GetTestClient(), directory);
    }

    private static async Task<JsonElement> GetAsync(HttpClient client, string path, string? language)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (language is not null)
            request.Headers.AcceptLanguage.ParseAdd(language);

        HttpResponseMessage response = await client.SendAsync(request);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task Problem_is_translated_by_accept_language_and_error_code()
    {
        (WebApplication app, HttpClient client, string directory) = await CreateAsync();
        try
        {
            await using (app)
            {
                JsonElement en = await GetAsync(client, "/failure", "en-US,en;q=0.9");
                Assert.Equal("Business rule violation", en.GetProperty("title").GetString());
                Assert.Equal("Order 42 is already shipped.", en.GetProperty("detail").GetString());
                Assert.Equal("order.shipped", en.GetProperty("code").GetString());

                JsonElement tr = await GetAsync(client, "/failure", null);
                Assert.Equal("İş kuralı ihlali", tr.GetProperty("title").GetString());
                Assert.Equal("Kargodaki sipariş iptal edilemez.", tr.GetProperty("detail").GetString());

                JsonElement forbidden = await GetAsync(client, "/forbidden?culture=en", null);
                Assert.Equal("You are not allowed to do this.", forbidden.GetProperty("detail").GetString());
            }
        }
        finally
        {
            System.IO.Directory.Delete(directory, recursive: true);
        }
    }
}
