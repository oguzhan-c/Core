using CanFx.Persistence.Database;
using Microsoft.EntityFrameworkCore;

namespace CanFx.Persistence.Tests;

public class DatabaseStartupTests
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
}
