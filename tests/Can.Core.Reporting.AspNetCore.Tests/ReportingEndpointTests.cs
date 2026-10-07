using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Can.Core.Reporting.AspNetCore.Tests;

public class ReportingEndpointTests
{
    private static object Definition(string source = "sales", string? filterExpression = null) => new
    {
        dataSource = source,
        filterExpression,
        rows = new[] { new { field = "category" } },
        measures = new[] { new { field = "amount", aggregate = "Sum", name = "total" } },
    };

    private static object Save(string name, bool shared = false, string source = "sales") => new
    {
        name,
        description = "açıklama",
        isShared = shared,
        definition = Definition(source),
    };

    private static async Task<JsonElement> Json(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    [Fact]
    public async Task Sources_are_filtered_by_permission_and_admin_sees_all()
    {
        await using ReportingHost host = await ReportingHost.StartAsync();

        JsonElement sources = await host.Client.GetFromJsonAsync<JsonElement>("/api/reporting/sources");
        Assert.Equal(["sales"], sources.EnumerateArray().Select(s => s.GetProperty("name").GetString()));
        Assert.Equal("Satışlar", sources[0].GetProperty("caption").GetString());

        host.Identity.As("boss", permissions: [], roles: ["Admin"]);
        sources = await host.Client.GetFromJsonAsync<JsonElement>("/api/reporting/sources");
        Assert.Equal(2, sources.GetArrayLength());
    }

    [Fact]
    public async Task Source_detail_lists_fields_with_captions()
    {
        await using ReportingHost host = await ReportingHost.StartAsync();

        JsonElement source = await host.Client.GetFromJsonAsync<JsonElement>("/api/reporting/sources/sales");
        JsonElement amount = source.GetProperty("fields").EnumerateArray().Single(f => f.GetProperty("name").GetString() == "amount");
        Assert.Equal("Tutar", amount.GetProperty("caption").GetString());
        Assert.Equal("Number", amount.GetProperty("type").GetString());
        Assert.Equal("N2", amount.GetProperty("format").GetString());

        Assert.Equal(HttpStatusCode.Forbidden, (await host.Client.GetAsync("/api/reporting/sources/payroll")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync("/api/reporting/sources/yok")).StatusCode);
    }

    [Fact]
    public async Task Anonymous_requests_are_rejected()
    {
        await using ReportingHost host = await ReportingHost.StartAsync();
        host.Identity.Id = null;

        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.GetAsync("/api/reporting/sources")).StatusCode);
    }

    [Fact]
    public async Task Run_returns_pivot_result()
    {
        await using ReportingHost host = await ReportingHost.StartAsync();

        HttpResponseMessage response = await host.Client.PostAsJsonAsync("/api/reporting/run", Definition());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement result = await Json(response);

        Assert.Equal(["Çeşni", "İçecek", "Şekerleme", "Genel toplam"], result.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("label").GetString()));
        Assert.Equal(186m, result.GetProperty("values")[3][0].GetDecimal());
        Assert.Equal("GrandTotal", result.GetProperty("rows")[3].GetProperty("kind").GetString());
        Assert.Equal("memory", result.GetProperty("mode").GetString());
    }

    [Fact]
    public async Task Run_maps_errors_to_problem_details()
    {
        await using ReportingHost host = await ReportingHost.StartAsync();

        HttpResponseMessage forbidden = await host.Client.PostAsJsonAsync("/api/reporting/run", Definition("payroll"));
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal("report.forbidden", (await Json(forbidden)).GetProperty("code").GetString());

        HttpResponseMessage missing = await host.Client.PostAsJsonAsync("/api/reporting/run", Definition("yok"));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        HttpResponseMessage invalid = await host.Client.PostAsJsonAsync("/api/reporting/run", Definition(filterExpression: "[amount] >"));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        JsonElement problem = await Json(invalid);
        Assert.Equal("report.invalid", problem.GetProperty("code").GetString());
        Assert.False(string.IsNullOrEmpty(problem.GetProperty("detail").GetString()));
    }

    [Fact]
    public async Task Global_permission_option_is_enforced()
    {
        await using ReportingHost host = await ReportingHost.StartAsync(o => o.Permission = "reports.view");

        Assert.Equal(HttpStatusCode.Forbidden, (await host.Client.GetAsync("/api/reporting/sources")).StatusCode);

        host.Identity.As("u1", permissions: ["reports.view", "reports.sales"]);
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync("/api/reporting/sources")).StatusCode);
    }

    [Theory]
    [InlineData("Xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", ".xlsx")]
    [InlineData("Pdf", "application/pdf", ".pdf")]
    [InlineData("Csv", "text/csv", ".csv")]
    public async Task Export_returns_file(string format, string contentType, string extension)
    {
        await using ReportingHost host = await ReportingHost.StartAsync();

        HttpResponseMessage response = await host.Client.PostAsJsonAsync("/api/reporting/export", new { definition = Definition(), format, title = "Kategori: satış/ay" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(contentType, response.Content.Headers.ContentType!.MediaType);
        string? fileName = response.Content.Headers.ContentDisposition!.FileNameStar ?? response.Content.Headers.ContentDisposition.FileName;
        Assert.Equal("Kategori_ satış_ay" + extension, fileName);
        byte[] bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.True(bytes.Length > 50);
        if (format == "Csv")
            Assert.Matches("İçecek[;,]86", Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public async Task Saved_reports_are_private_until_shared_and_scoped_to_tenant()
    {
        await using ReportingHost host = await ReportingHost.StartAsync();

        HttpResponseMessage created = await host.Client.PostAsJsonAsync("/api/reporting/saved", Save("Benim raporum"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        JsonElement report = await Json(created);
        string id = report.GetProperty("id").GetString()!;
        Assert.True(report.GetProperty("isOwner").GetBoolean());
        Assert.Equal("sales", report.GetProperty("dataSource").GetString());
        Assert.Equal("category", report.GetProperty("definition").GetProperty("rows")[0].GetProperty("field").GetString());
        Assert.EndsWith($"/api/reporting/saved/{id}", created.Headers.Location!.ToString(), StringComparison.Ordinal);

        await host.Client.PostAsJsonAsync("/api/reporting/saved", Save("Ortak rapor", shared: true));

        // aynı tenant'ta başka kullanıcı: yalnızca paylaşılanı görür, özel raporu açamaz
        host.Identity.As("u2");
        JsonElement list = await host.Client.GetFromJsonAsync<JsonElement>("/api/reporting/saved");
        Assert.Equal(["Ortak rapor"], list.EnumerateArray().Select(r => r.GetProperty("name").GetString()));
        Assert.False(list[0].GetProperty("canEdit").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync($"/api/reporting/saved/{id}")).StatusCode);

        string sharedId = list[0].GetProperty("id").GetString()!;
        Assert.Equal(HttpStatusCode.Forbidden, (await host.Client.PutAsJsonAsync($"/api/reporting/saved/{sharedId}", Save("Değişti", shared: true))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.Client.DeleteAsync($"/api/reporting/saved/{sharedId}")).StatusCode);

        // başka tenant: paylaşılan da görünmez
        host.Identity.As("u3", tenant: "t2");
        Assert.Empty((await host.Client.GetFromJsonAsync<JsonElement>("/api/reporting/saved")).EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync($"/api/reporting/saved/{sharedId}")).StatusCode);

        // yönetici paylaşılanı silebilir
        host.Identity.As("boss", roles: ["Admin"]);
        Assert.Equal(HttpStatusCode.NoContent, (await host.Client.DeleteAsync($"/api/reporting/saved/{sharedId}")).StatusCode);

        // sahip günceller ve siler
        host.Identity.As("u1");
        HttpResponseMessage updated = await host.Client.PutAsJsonAsync($"/api/reporting/saved/{id}", Save("Yeni ad"));
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal("Yeni ad", (await Json(updated)).GetProperty("name").GetString());
        Assert.Equal(HttpStatusCode.NoContent, (await host.Client.DeleteAsync($"/api/reporting/saved/{id}")).StatusCode);
        Assert.Empty((await host.Client.GetFromJsonAsync<JsonElement>("/api/reporting/saved")).EnumerateArray());
    }

    [Fact]
    public async Task Saving_validates_name_source_and_share_permission()
    {
        await using ReportingHost host = await ReportingHost.StartAsync(o => o.SharePermission = "reports.share");

        HttpResponseMessage empty = await host.Client.PostAsJsonAsync("/api/reporting/saved", Save("  "));
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.True((await Json(empty)).GetProperty("errors").TryGetProperty("name", out _));

        Assert.Equal(HttpStatusCode.Forbidden, (await host.Client.PostAsJsonAsync("/api/reporting/saved", Save("Bordro", source: "payroll"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.Client.PostAsJsonAsync("/api/reporting/saved", Save("Ortak", shared: true))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await host.Client.PostAsJsonAsync("/api/reporting/saved", Save("Özel"))).StatusCode);
    }

    [Fact]
    public void File_names_are_sanitized()
    {
        Assert.Equal("a_b_c", ReportingEndpoints.FileName("a/b:c"));
        Assert.Equal("rapor", ReportingEndpoints.FileName("   "));
        Assert.Equal(80, ReportingEndpoints.FileName(new string('x', 200)).Length);
    }
}
