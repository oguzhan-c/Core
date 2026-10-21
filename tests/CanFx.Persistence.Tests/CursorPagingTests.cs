using CanFx.Domain.Exceptions;
using CanFx.Persistence.Paging;
using CanFx.Persistence.Repositories;
using Microsoft.Extensions.DependencyInjection;

namespace CanFx.Persistence.Tests;

public class CursorPagingTests
{
    // Aynı adlı kayıtlar bilerek var: sıralama yalnızca ada göre olsaydı sayfa sınırında kaybolurlardı.
    private static readonly string[] Names = ["Kalem", "Defter", "Silgi", "Kalem", "Cetvel", "Defter", "Boya", "Kalem", "Zımba", "Ataş", "Kalem", "Makas", "Defter"];

    private static async Task<TestHost> SeedAsync()
    {
        TestHost host = await TestHost.CreateAsync();
        await using AsyncServiceScope scope = host.CreateScope();
        TestDbContext db = scope.ServiceProvider.GetRequiredService<TestDbContext>();
        db.Categories.AddRange(Names.Select(n => new Category(n)));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return host;
    }

    private static async Task<List<ICursorPage<Category>>> WalkForwardAsync(TestDbContext db, CursorOrder<Category> order, int size)
    {
        var pages = new List<ICursorPage<Category>>();
        string? cursor = null;
        do
        {
            ICursorPage<Category> page = await db.Categories.ToCursorPageAsync(order, cursor, size, TestContext.Current.CancellationToken);
            pages.Add(page);
            cursor = page.NextCursor;
        }
        while (cursor is not null && pages.Count < 50);

        return pages;
    }

    [Fact]
    public async Task Walking_forward_visits_every_row_once_in_order()
    {
        await using TestHost host = await SeedAsync();
        await using AsyncServiceScope scope = host.CreateScope();
        TestDbContext db = scope.ServiceProvider.GetRequiredService<TestDbContext>();
        CursorOrder<Category> order = CursorOrder<Category>.By(c => c.Name).ThenBy(c => c.Id);

        List<ICursorPage<Category>> pages = await WalkForwardAsync(db, order, size: 4);

        int[] expected = db.Categories.AsEnumerable().OrderBy(c => c.Name, StringComparer.Ordinal).ThenBy(c => c.Id).Select(c => c.Id).ToArray();
        Assert.Equal(expected, pages.SelectMany(p => p.Items).Select(c => c.Id).ToArray());
        Assert.Equal(4, pages.Count); // 13 kayıt / 4
        Assert.False(pages[0].HasPrevious);
        Assert.True(pages[1].HasPrevious);
        Assert.False(pages[^1].HasNext);
    }

    [Fact]
    public async Task Previous_cursor_returns_the_same_pages_backwards()
    {
        await using TestHost host = await SeedAsync();
        await using AsyncServiceScope scope = host.CreateScope();
        TestDbContext db = scope.ServiceProvider.GetRequiredService<TestDbContext>();
        CursorOrder<Category> order = CursorOrder<Category>.ByDescending(c => c.Name).ThenByDescending(c => c.Id);

        List<ICursorPage<Category>> forward = await WalkForwardAsync(db, order, size: 5);

        ICursorPage<Category> page = forward[^1];
        for (int i = forward.Count - 2; i >= 0; i--)
        {
            page = await db.Categories.ToCursorPageAsync(order, page.PreviousCursor, 5, TestContext.Current.CancellationToken);
            Assert.Equal(forward[i].Items.Select(c => c.Id), page.Items.Select(c => c.Id));
            Assert.True(page.HasNext);
        }

        Assert.False(page.HasPrevious); // ilk sayfaya dönüldü
    }

    [Fact]
    public async Task Rows_added_between_pages_are_neither_skipped_nor_repeated()
    {
        await using TestHost host = await SeedAsync();
        await using AsyncServiceScope scope = host.CreateScope();
        TestDbContext db = scope.ServiceProvider.GetRequiredService<TestDbContext>();
        IRepository<Category, int> repository = scope.ServiceProvider.GetRequiredService<IRepository<Category, int>>();

        CursorOrder<Category> newestFirst = CursorOrder<Category>.ByDescending(c => c.Id);
        ICursorPage<Category> first = await repository.GetCursorPageAsync(newestFirst, size: 5, enableTracking: false, cancellationToken: TestContext.Current.CancellationToken);

        // Sayfalar arasında en başa yeni kayıt eklenir. OFFSET'te ikinci sayfa bir kayıt kayar ve ilk sayfanın son
        // kaydı tekrar gelirdi; imleç "son görülen kayıttan sonrası"nı istediği için kaymaz.
        db.Categories.Add(new Category("Yeni"));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        ICursorPage<Category> second = await repository.GetCursorPageAsync(newestFirst, first.NextCursor, 5, enableTracking: false, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(first.Items[^1].Id - 1, second.Items[0].Id);
        Assert.Empty(first.Items.Select(c => c.Id).Intersect(second.Items.Select(c => c.Id)));
    }

    [Fact]
    public async Task Repository_appends_id_so_equal_values_are_not_lost()
    {
        await using TestHost host = await SeedAsync();
        await using AsyncServiceScope scope = host.CreateScope();
        IRepository<Category, int> repository = scope.ServiceProvider.GetRequiredService<IRepository<Category, int>>();
        CursorOrder<Category> byName = CursorOrder<Category>.By(c => c.Name); // benzersiz değil: dört "Kalem" var

        var seen = new List<int>();
        string? cursor = null;
        do
        {
            ICursorPage<Category> page = await repository.GetCursorPageAsync(byName, cursor, 3, predicate: c => c.Name != "Zımba", cancellationToken: TestContext.Current.CancellationToken);
            seen.AddRange(page.Items.Select(c => c.Id));
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        Assert.Equal(Names.Length - 1, seen.Count);
        Assert.Equal(seen.Count, seen.Distinct().Count());
    }

    [Fact]
    public async Task Tampered_or_foreign_cursors_are_rejected_as_bad_requests()
    {
        await using TestHost host = await SeedAsync();
        await using AsyncServiceScope scope = host.CreateScope();
        TestDbContext db = scope.ServiceProvider.GetRequiredService<TestDbContext>();
        CursorOrder<Category> byName = CursorOrder<Category>.By(c => c.Name).ThenBy(c => c.Id);
        CursorOrder<Category> byId = CursorOrder<Category>.By(c => c.Id);

        string next = (await db.Categories.ToCursorPageAsync(byName, null, 3, TestContext.Current.CancellationToken)).NextCursor!;

        foreach (string bad in new[] { "çöp", "e30", next[..^2], next + "AA" })
        {
            BusinessException error = await Assert.ThrowsAsync<BusinessException>(() => db.Categories.ToCursorPageAsync(byName, bad, 3, TestContext.Current.CancellationToken));
            Assert.Equal("cursor_invalid", error.Code);
        }

        // Başka bir sıralamanın imleci
        BusinessException foreign = await Assert.ThrowsAsync<BusinessException>(() => db.Categories.ToCursorPageAsync(byId, next, 3, TestContext.Current.CancellationToken));
        Assert.Equal("cursor_invalid", foreign.Code);
    }

    [Fact]
    public void In_memory_queries_and_enum_or_guid_keys_work()
    {
        var items = Enumerable.Range(1, 10)
            .Select(i => (Id: Guid.CreateVersion7(DateTimeOffset.UnixEpoch.AddDays(i)), Day: (DayOfWeek)(i % 7)))
            .ToList();
        CursorOrder<(Guid Id, DayOfWeek Day)> order = CursorOrder<(Guid Id, DayOfWeek Day)>.By(x => x.Day).ThenBy(x => x.Id);

        var seen = new List<Guid>();
        string? cursor = null;
        do
        {
            ICursorPage<(Guid Id, DayOfWeek Day)> page = items.AsQueryable().ToCursorPage(order, cursor, 3);
            seen.AddRange(page.Items.Select(x => x.Id));
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        Assert.Equal(items.OrderBy(x => x.Day).ThenBy(x => x.Id).Select(x => x.Id), seen);
    }

    [Fact]
    public void Nullable_keys_are_rejected_with_an_explanation()
    {
        ArgumentException error = Assert.Throws<ArgumentException>(() => CursorOrder<Customer>.By(c => (DateTimeOffset?)c.CreatedAt));
        Assert.Contains("null", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Map_keeps_the_cursors()
    {
        var page = new CursorPage<int>([1, 2], 2, "n", "p");
        ICursorPage<string> mapped = page.Map(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture));

        Assert.Equal(new[] { "1", "2" }, mapped.Items);
        Assert.Equal("n", mapped.NextCursor);
        Assert.True(mapped.HasPrevious);
    }
}
