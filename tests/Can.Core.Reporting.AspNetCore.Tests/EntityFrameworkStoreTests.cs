using Can.Core.Reporting.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Can.Core.Reporting.AspNetCore.Tests;

public sealed class ReportsDbContext(DbContextOptions<ReportsDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.ApplyCanReportingModel();
}

public class EntityFrameworkStoreTests
{
    [Fact]
    public async Task Stores_lists_updates_and_deletes_reports()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        DbContextOptions<ReportsDbContext> options = new DbContextOptionsBuilder<ReportsDbContext>().UseSqlite(connection).Options;
        await using (var setup = new ReportsDbContext(options))
            await setup.Database.EnsureCreatedAsync();

        var definition = new ReportDefinition { DataSource = "sales", Rows = { new ReportDimension("category") }, Measures = { new ReportMeasure("amount", ReportAggregate.Sum) } };
        var mine = new SavedReport { Name = "B raporu", TenantId = "t1", OwnerId = "u1", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        mine.WriteDefinition(definition);
        var shared = new SavedReport { Name = "A raporu", TenantId = "t1", OwnerId = "u2", IsShared = true };
        shared.WriteDefinition(definition);
        var otherTenant = new SavedReport { Name = "C", TenantId = "t2", OwnerId = "u1", IsShared = true };
        otherTenant.WriteDefinition(definition);
        var privateOfOther = new SavedReport { Name = "D", TenantId = "t1", OwnerId = "u2" };
        privateOfOther.WriteDefinition(definition);

        await using (var db = new ReportsDbContext(options))
        {
            var store = new EfSavedReportStore<ReportsDbContext>(db);
            foreach (SavedReport report in new[] { mine, shared, otherTenant, privateOfOther })
                await store.AddAsync(report);
        }

        await using (var db = new ReportsDbContext(options))
        {
            var store = new EfSavedReportStore<ReportsDbContext>(db);
            IReadOnlyList<SavedReport> list = await store.ListAsync("t1", "u1");
            Assert.Equal(["A raporu", "B raporu"], list.Select(r => r.Name));

            SavedReport found = (await store.FindAsync(mine.Id))!;
            Assert.Equal("sales", found.DataSource);
            ReportDefinition read = found.ReadDefinition();
            Assert.Equal("category", read.Rows[0].Field);
            Assert.Equal(ReportAggregate.Sum, read.Measures[0].Aggregate);

            found.Name = "Yeni";
            await store.UpdateAsync(found);
        }

        await using (var db = new ReportsDbContext(options))
        {
            var store = new EfSavedReportStore<ReportsDbContext>(db);
            SavedReport found = (await store.FindAsync(mine.Id))!;
            Assert.Equal("Yeni", found.Name);
            await store.DeleteAsync(found);
            Assert.Null(await store.FindAsync(mine.Id));
        }
    }
}
