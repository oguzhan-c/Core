using Can.Core.Persistence.Paging;
using Can.Core.Persistence.Repositories;
using Can.Core.Persistence.Specifications;
using Microsoft.Extensions.DependencyInjection;

namespace Can.Core.Persistence.Tests;

public sealed class ProductsCheaperThanSpec : Specification<Product>
{
    public ProductsCheaperThanSpec(int maxPrice, bool withCategory = false)
    {
        Where(p => p.Price < maxPrice);
        if (withCategory)
            Include(p => p.Category);

        OrderByDescending(p => p.Price);
        OrderBy(p => p.Name);
        AsReadOnly();
    }
}

public sealed class ProductsInCategorySpec : Specification<Product>
{
    public ProductsInCategorySpec(string category)
    {
        Where(p => p.Category != null && p.Category.Name == category);
        OrderBy(p => p.Name);
    }
}

public sealed class TopExpensiveSpec : Specification<Product>
{
    public TopExpensiveSpec(int count)
    {
        OrderByDescending(p => p.Price);
        Top(count);
    }
}

public sealed record ProductName(int Id, string Name);

public sealed class ProductNamesSpec : Specification<Product, ProductName>
{
    public ProductNamesSpec()
    {
        Where(p => p.Price >= 10);
        OrderBy(p => p.Name);
        Select(p => new ProductName(p.Id, p.Name));
    }
}

public class SpecificationTests
{
    private static async Task SeedAsync(TestHost host)
    {
        await using AsyncServiceScope scope = host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TestDbContext>();

        var office = new Category("Ofis");
        var school = new Category("Okul");

        db.Products.AddRange(
            Product.Create("Kirmizi Kalem", 10, office),
            Product.Create("Mavi Kalem", 20, office),
            Product.Create("Defter", 30, school),
            Product.Create("Silgi", 5)
        );

        await db.SaveChangesAsync();
    }

    private static async Task<T> WithRepositoryAsync<T>(TestHost host, Func<IRepository<Product, int>, Task<T>> action)
    {
        await using AsyncServiceScope scope = host.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<IRepository<Product, int>>());
    }

    [Fact]
    public async Task List_applies_criteria_include_and_ordering()
    {
        await using TestHost host = await TestHost.CreateAsync();
        await SeedAsync(host);

        IReadOnlyList<Product> products = await WithRepositoryAsync(host, r => r.ListAsync(new ProductsCheaperThanSpec(25, withCategory: true)));

        Assert.Equal(new[] { "Mavi Kalem", "Kirmizi Kalem", "Silgi" }, products.Select(p => p.Name));
        Assert.Equal("Ofis", products[0].Category?.Name);
        Assert.Null(products[2].Category);
    }

    [Fact]
    public async Task Top_and_first_or_default()
    {
        await using TestHost host = await TestHost.CreateAsync();
        await SeedAsync(host);

        IReadOnlyList<Product> top = await WithRepositoryAsync(host, r => r.ListAsync(new TopExpensiveSpec(2)));
        Product? first = await WithRepositoryAsync(host, r => r.FirstOrDefaultAsync(new ProductsInCategorySpec("Okul")));

        Assert.Equal(new[] { "Defter", "Mavi Kalem" }, top.Select(p => p.Name));
        Assert.Equal("Defter", first?.Name);
    }

    [Fact]
    public async Task Projection_reads_only_selected_shape()
    {
        await using TestHost host = await TestHost.CreateAsync();
        await SeedAsync(host);

        IReadOnlyList<ProductName> names = await WithRepositoryAsync(host, r => r.ListAsync(new ProductNamesSpec()));
        IPaginate<ProductName> page = await WithRepositoryAsync(host, r => r.PaginateAsync(new ProductNamesSpec(), index: 1, size: 2));

        Assert.Equal(new[] { "Defter", "Kirmizi Kalem", "Mavi Kalem" }, names.Select(n => n.Name));
        Assert.Equal(3, page.Count);
        Assert.Equal(new[] { "Mavi Kalem" }, page.Items.Select(n => n.Name));
    }

    [Fact]
    public async Task Count_any_and_paginate()
    {
        await using TestHost host = await TestHost.CreateAsync();
        await SeedAsync(host);

        var spec = new ProductsCheaperThanSpec(25);

        Assert.Equal(3, await WithRepositoryAsync(host, r => r.CountAsync(spec)));
        Assert.True(await WithRepositoryAsync(host, r => r.AnyAsync(new ProductsInCategorySpec("Okul"))));
        Assert.False(await WithRepositoryAsync(host, r => r.AnyAsync(new ProductsInCategorySpec("Yok"))));

        IPaginate<Product> page = await WithRepositoryAsync(host, r => r.PaginateAsync(spec, index: 0, size: 2));
        Assert.Equal(2, page.Pages);
        Assert.Equal(new[] { "Mavi Kalem", "Kirmizi Kalem" }, page.Items.Select(p => p.Name));
    }

    [Fact]
    public async Task Specifications_combine_with_and_or_not()
    {
        await using TestHost host = await TestHost.CreateAsync();
        await SeedAsync(host);

        Specification<Product> cheapOffice = new ProductsCheaperThanSpec(15) & new ProductsInCategorySpec("Ofis");
        Specification<Product> schoolOrCheap = new ProductsInCategorySpec("Okul") | Specification<Product>.Create(p => p.Price < 6);
        Specification<Product> notCheap = !new ProductsCheaperThanSpec(25);

        Assert.Equal(new[] { "Kirmizi Kalem" }, (await WithRepositoryAsync(host, r => r.ListAsync(cheapOffice))).Select(p => p.Name));
        Assert.Equal(new[] { "Defter", "Silgi" }, (await WithRepositoryAsync(host, r => r.ListAsync(schoolOrCheap))).Select(p => p.Name));
        Assert.Equal(new[] { "Defter" }, (await WithRepositoryAsync(host, r => r.ListAsync(notCheap))).Select(p => p.Name));
    }

    [Fact]
    public void Specification_works_in_memory()
    {
        var spec = new ProductsCheaperThanSpec(15);
        Product cheap = Product.Create("Silgi", 5);
        Product expensive = Product.Create("Defter", 30);

        Assert.True(spec.IsSatisfiedBy(cheap));
        Assert.False(spec.IsSatisfiedBy(expensive));
        Assert.Equal(new[] { "Silgi" }, spec.Evaluate([expensive, cheap]).Select(p => p.Name));
    }
}
