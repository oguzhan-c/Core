namespace Can.Core.Generator.Tests;

public sealed class NamesTests
{
    [Theory]
    [InlineData("Brand", "Brands")]
    [InlineData("Category", "Categories")]
    [InlineData("Box", "Boxes")]
    [InlineData("Address", "Addresses")]
    [InlineData("Day", "Days")]
    [InlineData("Branch", "Branches")]
    public void Plural(string name, string expected) => Assert.Equal(expected, Names.Plural(name));

    [Theory]
    [InlineData("ProductCategories", "product-categories")]
    [InlineData("Brand", "brand")]
    [InlineData("SKUItem", "sku-item")]
    public void Kebab(string name, string expected) => Assert.Equal(expected, Names.Kebab(name));

    [Fact]
    public void Snake_and_words()
    {
        Assert.Equal("product_category", Names.Snake("ProductCategory"));
        Assert.Equal("Product category", Names.Words("ProductCategory"));
    }
}

public sealed class EntityReaderTests
{
    private const string Brand = """
        using Can.Core.Domain.Auditing;
        using Can.Core.Domain.Results;

        namespace Shop.Domain.Catalog;

        public enum BrandTier { Standard, Premium }

        [Audited]
        public sealed class Brand : FullAuditedAggregateRoot<Guid>, IMultiTenant<Guid?>
        {
            public const int NameMaxLength = 60;

            private Brand() { Name = string.Empty; }

            public string Name { get; private set; }
            public string? Description { get; private set; }
            public decimal Rating { get; private set; }
            public BrandTier Tier { get; private set; }
            public Guid? TenantId { get; private set; }
            public IReadOnlyCollection<string> Tags => _tags;
            public List<BrandLogo> Logos { get; private set; } = [];
            public Money Price { get; private set; } = null!;
            public string Display => Name;

            public static Result<Brand> Create(string name, string? description, BrandTier tier) => new Brand { Name = name };

            public Result<Success> Update(string name, string? description) => Result.Success;
        }
        """;

    [Fact]
    public void Reads_entity_shape()
    {
        using var solution = new TestSolution();
        solution.Write("src/Shop.Domain/Catalog/Brand.cs", Brand);

        EntityModel model = EntityReader.Read(SolutionLayout.Find(solution.Root), "Brand");

        Assert.Equal("Shop.Domain.Catalog", model.Namespace);
        Assert.Equal("Guid", model.IdType);
        Assert.True(model.IsAudited);
        Assert.True(model.IsSoftDeletable);
        Assert.True(model.IsMultiTenant);
        Assert.Equal(new[] { "Name", "Description", "Rating", "Tier", "TenantId", "Logos", "Price" }, model.Properties.Select(p => p.Name));
        Assert.Equal(PropertyKind.Enum, model.Properties.Single(p => p.Name == "Tier").Kind);
        Assert.Equal(PropertyKind.Collection, model.Properties.Single(p => p.Name == "Logos").Kind);
        Assert.Equal(PropertyKind.Complex, model.Properties.Single(p => p.Name == "Price").Kind);
        Assert.Equal("NameMaxLength", model.MaxLengthConstant("name"));
        Assert.True(model.Factory!.ReturnsResult);
        Assert.Equal(new[] { "name", "description", "tier" }, model.Factory.Parameters.Select(p => p.Name));
        Assert.Equal(2, model.Update!.Parameters.Count);
    }

    [Fact]
    public void Reads_entity_by_file_and_lists_entities()
    {
        using var solution = new TestSolution();
        solution.Write("src/Shop.Domain/Catalog/Brand.cs", Brand);
        solution.Write("src/Shop.Domain/Tags/Tag.cs", "namespace Shop.Domain.Tags; public class Tag : Entity<int> { public string Name { get; set; } = \"\"; }");
        SolutionLayout layout = SolutionLayout.Find(solution.Path_("src/Shop.Domain/Tags"));

        Assert.Equal("Tag", EntityReader.Read(layout, solution.Path_("src/Shop.Domain/Tags/Tag.cs")).Name);
        Assert.Equal(new[] { "Brand", "Tag" }, EntityReader.List(layout).Select(e => e.Name));
    }

    [Fact]
    public void Missing_entity_is_reported()
    {
        using var solution = new TestSolution();
        Assert.Throws<GeneratorException>(() => EntityReader.Read(SolutionLayout.Find(solution.Root), "Nope"));
    }
}

public sealed class CrudGeneratorTests
{
    [Fact]
    public void Generates_feature_and_injects_registrations_once()
    {
        using var solution = new TestSolution();
        solution.Write("src/Shop.Domain/Catalog/Brand.cs", """
            namespace Shop.Domain.Catalog;

            public sealed class Brand : FullAuditedAggregateRoot<Guid>
            {
                public const int NameMaxLength = 60;
                public string Name { get; private set; } = "";
                public bool IsActive { get; private set; }
                public static Result<Brand> Create(string name) => new Brand();
                public void Update(string name, bool isActive) { }
            }
            """);

        SolutionLayout layout = SolutionLayout.Find(solution.Root);
        GenerationPlan plan = CrudGenerator.Plan(layout, EntityReader.Read(layout, "Brand"));
        IReadOnlyList<ChangeResult> first = plan.Apply();

        Assert.DoesNotContain(first, r => r.Kind == ChangeKind.Failed);
        string feature = "src/Shop.Application/Features/Brands/";
        foreach (string file in new[]
        {
            "Commands/Create/CreateBrandCommand.cs", "Commands/Create/CreateBrandCommandValidator.cs",
            "Commands/Update/UpdateBrandCommand.cs", "Commands/Delete/DeleteBrandCommand.cs",
            "Queries/GetById/GetBrandByIdQuery.cs", "Queries/GetList/GetBrandsQuery.cs",
            "Dtos/BrandDto.cs", "Dtos/BrandListItemDto.cs",
            "Constants/BrandErrors.cs", "Constants/BrandPermissions.cs", "Rules/BrandRules.cs",
        })
        {
            Assert.True(solution.Exists(feature + file), file);
        }

        Assert.Contains("Result<Brand> created = Brand.Create(request.Name);", solution.Read(feature + "Commands/Create/CreateBrandCommand.cs"));
        Assert.Contains("brand.Update(request.Name, request.IsActive);", solution.Read(feature + "Commands/Update/UpdateBrandCommand.cs"));
        Assert.Contains("RuleFor(x => x.Name).NotEmpty().MaximumLength(Brand.NameMaxLength);", solution.Read(feature + "Commands/Create/CreateBrandCommandValidator.cs"));
        Assert.Contains("builder.Property(x => x.Name).HasMaxLength(Brand.NameMaxLength).IsRequired();", solution.Read("src/Shop.Persistence/Configurations/BrandConfiguration.cs"));
        Assert.Contains("MapGroup(\"/brands\")", solution.Read("src/Shop.WebApi/Endpoints/BrandEndpoints.cs"));

        string context = solution.Read("src/Shop.Persistence/AppDbContext.cs");
        Assert.Contains("    public DbSet<Brand> Brands => Set<Brand>();\n    // can-gen:dbsets", context.ReplaceLineEndings("\n"));
        Assert.Contains("using Shop.Domain.Catalog;", context);

        string permissions = solution.Read("src/Shop.Application/Common/AppPermissions.cs");
        Assert.Contains("..BrandPermissions.All,", permissions);
        Assert.Contains("using Shop.Application.Features.Brands.Constants;", permissions);

        Assert.Contains("services.AddScoped<BrandRules>();", solution.Read("src/Shop.Application/DependencyInjection.cs"));
        Assert.Contains("app.MapBrandEndpoints();", solution.Read("src/Shop.WebApi/Endpoints/EndpointRegistration.cs"));

        string tr = solution.Read("src/Shop.WebApi/Localization/tr.json");
        Assert.Contains("\"invalid_credentials\": \"E-posta ya da şifre hatalı.\",\n  \"brand\": { \"not_found\": \"Brand kaydı bulunamadı.\" }", tr.ReplaceLineEndings("\n"));
        Assert.Contains("\"brand\": { \"not_found\": \"Brand not found.\" }", solution.Read("src/Shop.WebApi/Localization/en.json"));

        // İkinci çalıştırma: dosyalar korunur, eklemeler tekrarlanmaz.
        IReadOnlyList<ChangeResult> second = CrudGenerator.Plan(layout, EntityReader.Read(layout, "Brand")).Apply();
        Assert.All(second, r => Assert.Contains(r.Kind, new[] { ChangeKind.Skipped, ChangeKind.Unchanged }));
        Assert.Single(solution.Read("src/Shop.Persistence/AppDbContext.cs").Split("DbSet<Brand>")[1..]);
    }

    [Fact]
    public void Leaves_todos_when_entity_has_no_factory_or_update()
    {
        using var solution = new TestSolution();
        solution.Write("src/Shop.Domain/Tags/Tag.cs", """
            namespace Shop.Domain.Tags;

            public class Tag : Entity<int>
            {
                public string Name { get; set; } = "";
            }
            """);

        SolutionLayout layout = SolutionLayout.Find(solution.Root);
        CrudGenerator.Plan(layout, EntityReader.Read(layout, "Tag")).Apply();

        string create = solution.Read("src/Shop.Application/Features/Tags/Commands/Create/CreateTagCommand.cs");
        Assert.Contains("TODO(can-gen): Tag'de 'public static Result<Tag> Create(...)'", create);
        Assert.Contains("Result<int>", create);
        Assert.Contains("TODO(can-gen): Tag'de 'Update(...)' metodu bulunamadı", solution.Read("src/Shop.Application/Features/Tags/Commands/Update/UpdateTagCommand.cs"));
        Assert.Contains("\"/{id:int}\"", solution.Read("src/Shop.WebApi/Endpoints/TagEndpoints.cs"));
        Assert.Contains("q => q.OrderBy(x => x.Id)", solution.Read("src/Shop.Application/Features/Tags/Queries/GetList/GetTagsQuery.cs"));
    }

    [Fact]
    public void Dry_run_writes_nothing_and_force_overwrites()
    {
        using var solution = new TestSolution();
        solution.Write("src/Shop.Domain/Tags/Tag.cs", "namespace Shop.Domain.Tags; public class Tag : Entity<int> { public string Name { get; set; } = \"\"; }");
        SolutionLayout layout = SolutionLayout.Find(solution.Root);

        CrudGenerator.Plan(layout, EntityReader.Read(layout, "Tag")).Apply(dryRun: true);
        Assert.False(solution.Exists("src/Shop.WebApi/Endpoints/TagEndpoints.cs"));
        Assert.DoesNotContain("DbSet<Tag>", solution.Read("src/Shop.Persistence/AppDbContext.cs"));

        CrudGenerator.Plan(layout, EntityReader.Read(layout, "Tag")).Apply();
        solution.Write("src/Shop.WebApi/Endpoints/TagEndpoints.cs", "// değiştirildi");
        IReadOnlyList<ChangeResult> forced = CrudGenerator.Plan(layout, EntityReader.Read(layout, "Tag")).Apply(force: true);

        Assert.Contains(forced, r => r.Kind == ChangeKind.Overwritten && r.Path.EndsWith("TagEndpoints.cs", StringComparison.Ordinal));
        Assert.Contains("MapTagEndpoints", solution.Read("src/Shop.WebApi/Endpoints/TagEndpoints.cs"));
    }
}
