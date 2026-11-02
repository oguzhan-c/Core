using CanFx.Persistence.Database;
using CanFx.Persistence.Seeding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CanFx.Persistence.Tests;

/// <summary>Yalnızca geliştirmede çalışan örnek veri seeder'ı.</summary>
public sealed class SampleCategorySeeder(EventLog log) : IDataSeeder
{
    public int Order => 5;

    public bool DevelopmentOnly => true;

    public Task SeedAsync(DataSeedContext context, CancellationToken cancellationToken)
    {
        log.Add("seed:sample");
        return Task.CompletedTask;
    }
}

public class DatabaseStartupAndSeedTests
{
    [Theory]
    [InlineData("Host=localhost;Port=5432;Database=shop;Username=shop", true)]
    [InlineData("Server=localhost,1433;Database=Shop;User Id=sa", true)]
    [InlineData("Server=tcp:127.0.0.1,1433;Database=Shop", true)]
    [InlineData("Server=(localdb)\\MSSQLLocalDB;Database=Shop", true)]
    [InlineData("Host=host.docker.internal;Database=shop", true)]
    [InlineData("Server=.\\SQLEXPRESS;Database=Shop", true)]
    [InlineData("Host=db.example.com;Database=shop", false)]
    [InlineData("Server=tcp:myapp.database.windows.net,1433;Database=Shop", false)]
    [InlineData("", false)]
    public void Recognizes_local_connection_strings(string connectionString, bool local) =>
        Assert.Equal(local, DatabaseStartup.IsLocalConnectionString(connectionString));

    [Fact]
    public async Task Recreate_is_refused_outside_development()
    {
        await using var db = new DbContext(new DbContextOptionsBuilder().UseSqlite("Data Source=:memory:").Options);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DatabaseStartup.PrepareAsync(db, DatabaseStartupMode.Recreate, isDevelopment: false));
        Assert.Contains("Development", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Development_only_seeders_run_only_when_requested()
    {
        await using TestHost host = await TestHost.CreateAsync(s => s.AddCanDataSeeders(typeof(SampleCategorySeeder).Assembly));

        await host.Services.SeedDataAsync();
        Assert.DoesNotContain("seed:sample", host.Events.Entries);

        await host.Services.SeedDataAsync(includeDevelopmentData: true);
        Assert.Contains("seed:sample", host.Events.Entries);
    }

    [Fact]
    public async Task Seed_file_is_read_from_json()
    {
        string file = Path.Combine(Path.GetTempPath(), $"seed-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(file, """
            // yorum satırı ve sondaki virgül kabul edilir
            [ { "name": "Kırmızı" }, { "name": "Mavi" }, ]
            """);
        try
        {
            IReadOnlyList<ColorSeed> colors = await SeedFiles.ReadJsonAsync<ColorSeed>(file);
            Assert.Equal(["Kırmızı", "Mavi"], colors.Select(c => c.Name));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Sample_data_is_repeatable_with_the_same_seed()
    {
        var first = new SampleData(seed: 7);
        var second = new SampleData(seed: 7);

        Assert.Equal(first.FullName(), second.FullName());
        Assert.Equal(first.Email(), second.Email());
        Assert.Matches(@"^[a-z.]+\d+@example\.com$", new SampleData().Email());
    }

    private sealed record ColorSeed(string Name);
}
