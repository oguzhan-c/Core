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
