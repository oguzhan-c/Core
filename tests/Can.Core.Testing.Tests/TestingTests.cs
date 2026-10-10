using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Can.Core.Application;
using Can.Core.Testing.AspNetCore;
using Can.Core.Testing.EntityFrameworkCore;
using Can.Core.WebApi.DependencyInjection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Can.Core.Testing.Tests;

public class TestClockTests
{
    [Fact]
    public async Task Delays_complete_only_when_the_clock_is_advanced()
    {
        var clock = new TestClock();
        Task delay = Task.Delay(TimeSpan.FromMinutes(10), clock);

        clock.Advance(TimeSpan.FromMinutes(9));
        Assert.False(delay.IsCompleted);
        Assert.Equal(1, clock.PendingTimers);

        clock.Advance(TimeSpan.FromMinutes(1));
        await delay.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(TestClock.DefaultStart.AddMinutes(10), clock.GetUtcNow());
        Assert.Equal(0, clock.PendingTimers);
    }

    [Fact]
    public void Periodic_timers_fire_for_each_period_and_elapsed_time_follows_the_clock()
    {
        var clock = new TestClock(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.FromHours(3)));
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero), clock.GetUtcNow());

        var fired = new List<DateTimeOffset>();
        using ITimer timer = clock.CreateTimer(_ => fired.Add(clock.GetUtcNow()), null, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1));
        long started = clock.GetTimestamp();

        clock.Advance(TimeSpan.FromMinutes(3));

        Assert.Equal(3, fired.Count); // 0:30, 1:30, 2:30
        Assert.Equal(clock.GetUtcNow().AddSeconds(-30), fired[^1]);
        Assert.Equal(TimeSpan.FromMinutes(3), clock.GetElapsedTime(started));
        Assert.Throws<ArgumentOutOfRangeException>(() => clock.SetUtcNow(clock.GetUtcNow().AddSeconds(-1)));
    }

    [Fact]
    public void Identity_doubles_switch_user_and_tenant()
    {
        var user = new TestCurrentUser().SignIn("42", roles: ["Admin"], permissions: ["orders.*"]);
        Assert.True(user.IsAuthenticated);
        Assert.True(user.IsInRole("admin"));
        Assert.True(user.HasPermission("orders.write"));
        Assert.False(user.SignOut().IsAuthenticated);

        var tenant = new TestCurrentTenant { Id = "acme" };
        using (tenant.Use("globex"))
            Assert.Equal("globex", tenant.Id);
        Assert.Equal("acme", tenant.Id);

        ServiceProvider provider = new ServiceCollection().AddCanTestContext(user, tenant).BuildServiceProvider();
        Assert.Same(user, provider.GetRequiredService<ICurrentUser>());
        Assert.Same(tenant, provider.GetRequiredService<ICurrentTenant>());
        Assert.IsType<TestClock>(provider.GetRequiredService<TimeProvider>());
    }
}

public sealed class Note
{
    public int Id { get; set; }

    public string Code { get; set; } = "";
}

public sealed class NotesContext(DbContextOptions<NotesContext> options) : DbContext(options)
{
    public DbSet<Note> Notes => Set<Note>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.Entity<Note>().HasIndex(n => n.Code).IsUnique();
}

public class SqliteTestDatabaseTests
{
    [Fact]
    public async Task Real_constraints_and_data_shared_across_scopes()
    {
        await using var database = new SqliteTestDatabase();
        var services = new ServiceCollection();
        services.AddDbContext<NotesContext>(database.Configure);
        await using ServiceProvider provider = services.BuildServiceProvider();
        await database.CreateSchemaAsync<NotesContext>(provider);

        await SqliteTestDatabase.InScopeAsync<NotesContext, int>(provider, db =>
        {
            db.Notes.Add(new Note { Code = "A" });
            return db.SaveChangesAsync();
        });

        // benzersiz indeks gerçekten uygulanır (EF InMemory'de uygulanmaz)
        await Assert.ThrowsAsync<DbUpdateException>(() => SqliteTestDatabase.InScopeAsync<NotesContext, int>(provider, db =>
        {
            db.Notes.Add(new Note { Code = "A" });
            return db.SaveChangesAsync();
        }));

        Assert.Equal(1, await SqliteTestDatabase.InScopeAsync<NotesContext, int>(provider, db => db.Notes.CountAsync()));

        await database.ResetAsync<NotesContext>(provider);
        Assert.Equal(0, await SqliteTestDatabase.InScopeAsync<NotesContext, int>(provider, db => db.Notes.CountAsync()));
    }
}

public class TestAuthenticationTests
{
    private static Task<CanTestApp> StartAsync(string environment = "Testing") =>
        CanTestApp.StartAsync(
            services =>
            {
                services.AddCanWebApi();
                services.AddCanTestAuthentication();
                services.AddAuthorization();
            },
            app =>
            {
                app.UseCanExceptionHandler();
                app.UseAuthentication();
                app.UseCanTenantResolution();
                app.UseAuthorization();
                app.MapGet("/me", (ICurrentUser user, ICurrentTenant tenant) => new
                {
                    user.Id,
                    Roles = user.Roles.Order().ToArray(),
                    CanWrite = user.HasPermission("orders.write"),
                    Tenant = tenant.Id?.ToString(),
                }).RequireAuthorization();
                app.MapGet("/admin", () => "ok").RequireAuthorization(p => p.RequireRole("Admin"));
            },
            environment);

    [Fact]
    public async Task Requests_run_as_the_given_user_with_roles_permissions_and_tenant()
    {
        await using CanTestApp app = await StartAsync();
        HttpClient client = app.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/me")).StatusCode);

        const string Acme = "aaaaaaaa-0000-0000-0000-000000000001"; // varsayılan tenant kimliği Guid
        client.AsUser("42", roles: ["Admin", "Sales"], permissions: ["orders.*"], tenant: Acme);
        JsonElement me = await client.GetFromJsonAsync<JsonElement>("/me");
        Assert.Equal("42", me.GetProperty("id").GetString());
        Assert.Equal(new[] { "Admin", "Sales" }, me.GetProperty("roles").EnumerateArray().Select(r => r.GetString()!).ToArray());
        Assert.True(me.GetProperty("canWrite").GetBoolean());
        Assert.Equal(Acme, me.GetProperty("tenant").GetString());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/admin")).StatusCode);

        client.AsUser("7");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/admin")).StatusCode);

        client.AsAnonymous();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/me")).StatusCode);
    }

    [Fact]
    public async Task Refuses_to_run_in_production()
    {
        await using CanTestApp app = await StartAsync("Production");
        HttpResponseMessage response = await app.CreateClient().AsUser("42").GetAsync("/me");
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }
}
