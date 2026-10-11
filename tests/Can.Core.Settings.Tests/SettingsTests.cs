using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Can.Core.Application.Exceptions;
using Can.Core.Settings.AspNetCore;
using Can.Core.Settings.EntityFrameworkCore;
using Can.Core.Testing;
using Can.Core.Testing.AspNetCore;
using Can.Core.Testing.EntityFrameworkCore;
using Can.Core.WebApi.DependencyInjection;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Can.Core.Settings.Tests;

public static class TestSettings
{
    public static readonly SettingKey<int> MaxLines = new SettingKey<int>("orders.max-lines", 100)
    {
        DisplayName = "Siparişte en fazla satır",
        Group = "Siparişler",
    }.Validate(v => v is > 0 and <= 1000, "1 ile 1000 arasında olmalı.");

    public static readonly SettingKey<string> Currency = new("orders.currency", "TRY") { IsVisibleToClients = true };

    public static readonly SettingKey<bool> Maintenance = new("system.maintenance", false) { Scopes = SettingScope.Global, IsVisibleToClients = true };

    public static readonly SettingKey<string> SmsApiKey = new("sms.api-key", "") { Scopes = SettingScope.Tenant, IsSecret = true, IsVisibleToClients = true };

    public static readonly SettingKey<int> Undefined = new("not.registered", 0);

    public static SettingDefinition[] All => [MaxLines, Currency, Maintenance, SmsApiKey];
}

/// <summary>Testte gizli değerin şifrelendiğini görmek için.</summary>
public sealed class ReversingProtector : ISettingProtector
{
    public string Protect(string value) => "enc:" + new string(value.Reverse().ToArray());

    public string Unprotect(string value) => new(value["enc:".Length..].Reverse().ToArray());
}

public class SettingServiceTests
{
    private sealed class Host : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;

        public Host(bool protector = true, Action<IServiceCollection>? configure = null)
        {
            var services = new ServiceCollection();
            services.AddCanSettings(s => s.Add(TestSettings.All));
            if (protector)
                services.AddSingleton<ISettingProtector, ReversingProtector>();
            configure?.Invoke(services);
            services.AddCanTestContext(User, Tenant);
            _provider = services.BuildServiceProvider();
        }

        public TestCurrentUser User { get; } = new TestCurrentUser().SignIn("admin-1");

        public TestCurrentTenant Tenant { get; } = new();

        public IServiceProvider Services => _provider;

        public async Task<T> InScope<T>(Func<IServiceProvider, Task<T>> work)
        {
            await using AsyncServiceScope scope = _provider.CreateAsyncScope();
            return await work(scope.ServiceProvider);
        }

        public Task<T> GetAsync<T>(SettingKey<T> key) => InScope(sp => sp.GetRequiredService<ISettings>().GetAsync(key));

        public Task SetAsync<T>(SettingKey<T> key, T value, SettingScope scope) =>
            InScope(async sp => { await sp.GetRequiredService<ISettingManager>().SetAsync(key, value, scope); return 0; });

        public Task ResetAsync(string name, SettingScope scope) =>
            InScope(async sp => { await sp.GetRequiredService<ISettingManager>().ResetAsync(name, scope); return 0; });

        public ValueTask DisposeAsync() => _provider.DisposeAsync();
    }

    [Fact]
    public async Task Tenant_value_overrides_global_which_overrides_default()
    {
        await using var host = new Host();
        host.Tenant.Id = "acme";

        Assert.Equal(100, await host.GetAsync(TestSettings.MaxLines));

        await host.SetAsync(TestSettings.MaxLines, 200, SettingScope.Global);
        Assert.Equal(200, await host.GetAsync(TestSettings.MaxLines)); // önbellek temizlendi

        await host.SetAsync(TestSettings.MaxLines, 50, SettingScope.Tenant);
        Assert.Equal(50, await host.GetAsync(TestSettings.MaxLines));

        using (host.Tenant.Use("globex"))
            Assert.Equal(200, await host.GetAsync(TestSettings.MaxLines)); // başka tenant: genel değer

        await host.ResetAsync(TestSettings.MaxLines.Name, SettingScope.Tenant);
        Assert.Equal(200, await host.GetAsync(TestSettings.MaxLines));

        await host.ResetAsync(TestSettings.MaxLines.Name, SettingScope.Global);
        Assert.Equal(100, await host.GetAsync(TestSettings.MaxLines));
    }

    [Fact]
    public async Task Rules_scopes_and_types_are_enforced()
    {
        await using var host = new Host();
        host.Tenant.Id = "acme";

        ValidationException rule = await Assert.ThrowsAsync<ValidationException>(() => host.SetAsync(TestSettings.MaxLines, 5000, SettingScope.Tenant));
        Assert.Equal("setting_invalid", rule.Code);
        Assert.Contains("1 ile 1000", rule.Message, StringComparison.Ordinal);

        ValidationException scope = await Assert.ThrowsAsync<ValidationException>(() => host.SetAsync(TestSettings.Maintenance, true, SettingScope.Tenant));
        Assert.Equal("setting_scope_invalid", scope.Code);

        ValidationException type = await Assert.ThrowsAsync<ValidationException>(() => host.InScope(async sp =>
        {
            await sp.GetRequiredService<ISettingManager>().SetAsync("orders.max-lines", JsonDocument.Parse("\"çok\"").RootElement, SettingScope.Tenant);
            return 0;
        }));
        Assert.Equal("setting_invalid", type.Code);

        await Assert.ThrowsAsync<NotFoundException>(() => host.ResetAsync("yok.boyle", SettingScope.Global));
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.GetAsync(TestSettings.Undefined));

        host.Tenant.Id = null;
        await Assert.ThrowsAsync<ValidationException>(() => host.SetAsync(TestSettings.MaxLines, 10, SettingScope.Tenant)); // tenant yok
    }

    [Fact]
    public async Task Secrets_are_protected_hidden_from_lists_and_clients()
    {
        var store = new InMemorySettingStore();
        await using var host = new Host(configure: s => s.AddSingleton<ISettingStore>(store));
        host.Tenant.Id = "acme";

        await host.SetAsync(TestSettings.SmsApiKey, "gizli-anahtar", SettingScope.Tenant);

        Assert.Equal("gizli-anahtar", await host.GetAsync(TestSettings.SmsApiKey));
        string stored = (await store.GetAllAsync("acme"))[TestSettings.SmsApiKey.Name];
        Assert.StartsWith("enc:", stored, StringComparison.Ordinal);
        Assert.DoesNotContain("gizli", stored, StringComparison.Ordinal);

        IReadOnlyList<SettingView> list = await host.InScope(sp => sp.GetRequiredService<ISettingManager>().ListAsync(SettingScope.Tenant));
        SettingView secret = list.Single(v => v.Name == TestSettings.SmsApiKey.Name);
        Assert.Null(secret.Value);
        Assert.True(secret.IsOverridden);
        Assert.DoesNotContain(list, v => v.Name == TestSettings.Maintenance.Name); // yalnızca global

        IReadOnlyDictionary<string, object?> client = await host.InScope(sp => sp.GetRequiredService<ISettings>().GetClientValuesAsync());
        Assert.Equal(new[] { "orders.currency", "system.maintenance" }, client.Keys.Order());
    }

    [Fact]
    public async Task Secret_without_protector_is_refused()
    {
        await using var host = new Host(protector: false);
        host.Tenant.Id = "acme";
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.SetAsync(TestSettings.SmsApiKey, "x", SettingScope.Tenant));
    }

    [Fact]
    public async Task List_shows_source_and_default()
    {
        await using var host = new Host();
        host.Tenant.Id = "acme";
        await host.SetAsync(TestSettings.Currency, "EUR", SettingScope.Global);

        IReadOnlyList<SettingView> tenant = await host.InScope(sp => sp.GetRequiredService<ISettingManager>().ListAsync(SettingScope.Tenant));
        SettingView currency = tenant.Single(v => v.Name == "orders.currency");
        Assert.Equal("EUR", currency.Value);
        Assert.Equal("TRY", currency.DefaultValue);
        Assert.Equal(SettingSource.Global, currency.Source);
        Assert.False(currency.IsOverridden); // bu düzeyde değil
        Assert.Equal("integer", tenant.Single(v => v.Name == "orders.max-lines").Kind);
    }
}

public sealed class SettingsDbContext(DbContextOptions<SettingsDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.ApplyCanSettingsModel();
}

public class EfSettingStoreTests
{
    [Fact]
    public async Task Values_are_upserted_per_tenant_and_removed()
    {
        await using var database = new SqliteTestDatabase();
        var services = new ServiceCollection();
        services.AddDbContext<SettingsDbContext>(database.Configure);
        services.AddCanSettings(s => s.Add(TestSettings.All).UseEntityFrameworkStore<SettingsDbContext>());
        var tenant = new TestCurrentTenant { Id = "acme" };
        services.AddCanTestContext(new TestCurrentUser().SignIn("u1"), tenant);
        await using ServiceProvider provider = services.BuildServiceProvider();
        await database.CreateSchemaAsync<SettingsDbContext>(provider);

        async Task Run(Func<IServiceProvider, Task> work)
        {
            await using AsyncServiceScope scope = provider.CreateAsyncScope();
            await work(scope.ServiceProvider);
        }

        await Run(sp => sp.GetRequiredService<ISettingManager>().SetAsync(TestSettings.MaxLines, 10, SettingScope.Tenant));
        await Run(sp => sp.GetRequiredService<ISettingManager>().SetAsync(TestSettings.MaxLines, 20, SettingScope.Tenant)); // güncelleme
        await Run(sp => sp.GetRequiredService<ISettingManager>().SetAsync(TestSettings.MaxLines, 30, SettingScope.Global));

        List<SettingValue> rows = await SqliteTestDatabase.InScopeAsync<SettingsDbContext, List<SettingValue>>(provider, db => db.Set<SettingValue>().OrderBy(v => v.TenantId).ToListAsync());
        Assert.Equal(2, rows.Count);
        Assert.Equal(("", "30"), (rows[0].TenantId, rows[0].Value));
        Assert.Equal(("acme", "20"), (rows[1].TenantId, rows[1].Value));
        Assert.Equal("u1", rows[1].UpdatedBy);

        await Run(async sp => Assert.Equal(20, await sp.GetRequiredService<ISettings>().GetAsync(TestSettings.MaxLines)));
        await Run(sp => sp.GetRequiredService<ISettingManager>().ResetAsync(TestSettings.MaxLines.Name, SettingScope.Tenant));
        await Run(async sp => Assert.Equal(30, await sp.GetRequiredService<ISettings>().GetAsync(TestSettings.MaxLines)));
    }
}

public class SettingsEndpointTests
{
    private const string Acme = "aaaaaaaa-0000-0000-0000-000000000001";

    private static Task<CanTestApp> StartAsync() =>
        CanTestApp.StartAsync(
            services =>
            {
                services.AddCanWebApi();
                services.AddCanTestAuthentication();
                services.AddAuthorization();
                services.AddCanSettings(s => s.Add(TestSettings.All).UseDataProtection());
            },
            app =>
            {
                app.UseCanExceptionHandler();
                app.UseAuthentication();
                app.UseCanTenantResolution();
                app.UseAuthorization();
                app.MapCanSettings();
            });

    private static async Task<string?> CodeOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString();

    [Fact]
    public async Task Tenant_admin_manages_only_tenant_settings()
    {
        await using CanTestApp app = await StartAsync();
        HttpClient client = app.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/settings")).StatusCode);

        client.AsUser("u1", permissions: ["settings.tenant"], tenant: Acme);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync("/api/settings/orders.max-lines", new { value = 250 })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync("/api/settings/sms.api-key", new { value = "anahtar" })).StatusCode);

        JsonElement list = await client.GetFromJsonAsync<JsonElement>("/api/settings");
        JsonElement maxLines = list.EnumerateArray().Single(v => v.GetProperty("name").GetString() == "orders.max-lines");
        Assert.Equal(250, maxLines.GetProperty("value").GetInt32());
        Assert.Equal("Tenant", maxLines.GetProperty("source").GetString());
        JsonElement secret = list.EnumerateArray().Single(v => v.GetProperty("name").GetString() == "sms.api-key");
        Assert.Equal(JsonValueKind.Null, secret.GetProperty("value").ValueKind);

        HttpResponseMessage global = await client.PutAsJsonAsync("/api/settings/orders.max-lines?scope=global", new { value = 1 });
        Assert.Equal(HttpStatusCode.Forbidden, global.StatusCode);

        HttpResponseMessage invalid = await client.PutAsJsonAsync("/api/settings/orders.max-lines", new { value = 0 });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal("setting_invalid", await CodeOf(invalid));

        HttpResponseMessage missing = await client.PutAsJsonAsync("/api/settings/yok.boyle", new { value = 1 });
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/settings/orders.max-lines")).StatusCode);
        list = await client.GetFromJsonAsync<JsonElement>("/api/settings");
        Assert.Equal(100, list.EnumerateArray().Single(v => v.GetProperty("name").GetString() == "orders.max-lines").GetProperty("value").GetInt32());
    }

    [Fact]
    public async Task Admin_sets_global_values_and_clients_read_visible_ones()
    {
        await using CanTestApp app = await StartAsync();
        HttpClient client = app.CreateClient().AsUser("root", roles: ["Admin"]);

        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync("/api/settings/system.maintenance?scope=global", new { value = true })).StatusCode);

        client.AsUser("u2", tenant: Acme); // yetkisiz ama giriş yapmış
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/settings")).StatusCode);

        JsonElement values = await client.GetFromJsonAsync<JsonElement>("/api/settings/client");
        Assert.True(values.GetProperty("system.maintenance").GetBoolean());
        Assert.Equal("TRY", values.GetProperty("orders.currency").GetString());
        Assert.False(values.TryGetProperty("sms.api-key", out _)); // gizli: istemciye verilmez
    }
}
