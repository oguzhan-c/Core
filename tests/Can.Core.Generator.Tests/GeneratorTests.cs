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
        solution.Write("src/Shop.Domain/Entities/Brand.cs", Brand);

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
        solution.Write("src/Shop.Domain/Entities/Brand.cs", Brand);
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
    private const string Feature = "src/Shop.Application/Features/Brands/";

    [Fact]
    public void Generates_narchitecture_layout_and_injects_registrations_once()
    {
        using var solution = new TestSolution();
        solution.Write("src/Shop.Domain/Entities/Brand.cs", """
            namespace Shop.Domain.Entities;

            public sealed class Brand : FullAuditedAggregateRoot<Guid>
            {
                public const int NameMaxLength = 60;
                private Brand() { }
                public string Name { get; private set; } = "";
                public bool IsActive { get; private set; }
                public static Result<Brand> Create(string name) => new Brand();
                public void Update(string name, bool isActive) { }
            }
            """);

        SolutionLayout layout = SolutionLayout.Find(solution.Root);
        IReadOnlyList<ChangeResult> first = CrudGenerator.Plan(layout, EntityReader.Read(layout, "Brand")).Apply();

        Assert.DoesNotContain(first, r => r.Kind == ChangeKind.Failed);
        foreach (string file in new[]
        {
            "Commands/Create/CreateBrandCommand.cs", "Commands/Create/CreateBrandCommandValidator.cs", "Commands/Create/CreatedBrandResponse.cs",
            "Commands/Update/UpdateBrandCommand.cs", "Commands/Update/UpdateBrandCommandValidator.cs", "Commands/Update/UpdatedBrandResponse.cs",
            "Commands/Delete/DeleteBrandCommand.cs", "Commands/Delete/DeletedBrandResponse.cs",
            "Queries/GetById/GetByIdBrandQuery.cs", "Queries/GetById/GetByIdBrandResponse.cs",
            "Queries/GetList/GetListBrandQuery.cs", "Queries/GetList/GetListBrandQueryValidator.cs", "Queries/GetList/GetListBrandListItemDto.cs",
            "Queries/GetListByDynamic/GetListByDynamicBrandQuery.cs", "Queries/GetListByDynamic/GetListByDynamicBrandQueryValidator.cs",
            "Constants/BrandsBusinessMessages.cs", "Constants/BrandsOperationClaims.cs",
            "Commands/Delete/DeleteBrandCommandValidator.cs", "Queries/GetListByDynamic/GetListByDynamicBrandListItemDto.cs",
            "Profiles/MappingProfiles.cs", "Rules/BrandBusinessRules.cs",
        })
        {
            Assert.True(solution.Exists(Feature + file), file);
        }

        Assert.True(solution.Exists("src/Shop.Application/Services/Repositories/IBrandRepository.cs"));
        Assert.True(solution.Exists("src/Shop.Persistence/Repositories/BrandRepository.cs"));
        Assert.True(solution.Exists("src/Shop.Persistence/EntityConfigurations/BrandConfiguration.cs"));
        Assert.True(solution.Exists("src/Shop.WebAPI/Controllers/BrandsController.cs"));
        Assert.True(solution.Exists("src/Shop.Application/Services/Brands/IBrandService.cs"));
        Assert.True(solution.Exists("src/Shop.Application/Services/Brands/BrandManager.cs"));
        Assert.Contains("services.AddScoped<IBrandService, BrandManager>();", solution.Read("src/Shop.Application/ApplicationServiceRegistration.cs"));
        Assert.Contains("ISecuredRequest, ILoggableRequest, ITransactionalRequest", solution.Read(Feature + "Commands/Create/CreateBrandCommand.cs"));
        Assert.Contains("ICachableRequest", solution.Read(Feature + "Queries/GetList/GetListBrandQuery.cs"));
        Assert.Contains("// can-gen:endpoints", solution.Read("src/Shop.WebAPI/Controllers/BrandsController.cs"));

        string create = solution.Read(Feature + "Commands/Create/CreateBrandCommand.cs");
        Assert.Contains("Brand brand = Brand.Create(request.Name).OrThrow();", create);
        Assert.Contains("public string Name { get; set; } = string.Empty;", create);
        Assert.Contains("[BrandsOperationClaims.Admin, BrandsOperationClaims.Write, BrandsOperationClaims.Create]", create);
        Assert.DoesNotContain("SaveChangesAsync", create); // Guid kimlik kodda üretilir
        Assert.Contains("brand.Update(request.Name, request.IsActive);", solution.Read(Feature + "Commands/Update/UpdateBrandCommand.cs"));
        Assert.Contains("RuleFor(x => x.Name).NotEmpty().MaximumLength(Brand.NameMaxLength);", solution.Read(Feature + "Commands/Create/CreateBrandCommandValidator.cs"));
        Assert.Contains("throw new NotFoundException(BrandsBusinessMessages.BrandNotExists)", solution.Read(Feature + "Rules/BrandBusinessRules.cs"));
        Assert.DoesNotContain("CreateMap<CreateBrandCommand, Brand>", solution.Read(Feature + "Profiles/MappingProfiles.cs"));
        Assert.Contains("public DateTimeOffset CreatedAt { get; set; }", solution.Read(Feature + "Queries/GetById/GetByIdBrandResponse.cs"));
        Assert.Contains("[Route(\"api/v1/[controller]\")]", solution.Read("src/Shop.WebAPI/Controllers/BrandsController.cs"));
        Assert.Contains("builder.Property(x => x.Name).HasColumnName(\"Name\").HasMaxLength(Brand.NameMaxLength).IsRequired();", solution.Read("src/Shop.Persistence/EntityConfigurations/BrandConfiguration.cs"));

        string context = solution.Read("src/Shop.Persistence/Contexts/BaseDbContext.cs");
        Assert.Contains("    public DbSet<Brand> Brands => Set<Brand>();\n    // can-gen:dbsets", context.ReplaceLineEndings("\n"));
        Assert.Contains("using Shop.Domain.Entities;", context);

        string registration = solution.Read("src/Shop.Persistence/PersistenceServiceRegistration.cs");
        Assert.Contains("services.AddScoped<IBrandRepository, BrandRepository>();", registration);
        Assert.Contains("using Shop.Persistence.Repositories;", registration);

        string claims = solution.Read("src/Shop.Application/Services/OperationClaims/AppOperationClaims.cs");
        Assert.Contains("..BrandsOperationClaims.All,", claims);
        Assert.Contains("using Shop.Application.Features.Brands.Constants;", claims);

        string tr = solution.Read("src/Shop.WebAPI/Localization/tr.json");
        Assert.Contains("\"invalid_credentials\": \"E-posta ya da şifre hatalı.\",\n  \"brand\": { \"not_exists\": \"Brand kaydı bulunamadı.\" }", tr.ReplaceLineEndings("\n"));
        Assert.Contains("\"brand\": { \"not_exists\": \"Brand not found.\" }", solution.Read("src/Shop.WebAPI/Localization/en.json"));

        // İkinci çalıştırma: dosyalar korunur, eklemeler tekrarlanmaz.
        IReadOnlyList<ChangeResult> second = CrudGenerator.Plan(layout, EntityReader.Read(layout, "Brand")).Apply();
        Assert.All(second, r => Assert.Contains(r.Kind, new[] { ChangeKind.Skipped, ChangeKind.Unchanged }));
        Assert.Single(solution.Read("src/Shop.Persistence/Contexts/BaseDbContext.cs").Split("DbSet<Brand>")[1..]);
    }

    [Fact]
    public void Uses_mapper_for_public_constructor_and_setters()
    {
        using var solution = new TestSolution();
        solution.Write("src/Shop.Domain/Entities/Color.cs", """
            namespace Shop.Domain.Entities;

            public class Color : FullAuditedEntity<int>
            {
                public string ColorName { get; set; }

                public Color(string colorName) => ColorName = colorName;
            }
            """);

        SolutionLayout layout = SolutionLayout.Find(solution.Root);
        CrudGenerator.Plan(layout, EntityReader.Read(layout, "Color")).Apply();

        string feature = "src/Shop.Application/Features/Colors/";
        string create = solution.Read(feature + "Commands/Create/CreateColorCommand.cs");
        Assert.Contains("Color color = _mapper.Map<Color>(request);", create);
        Assert.Contains("await _unitOfWork.SaveChangesAsync(cancellationToken);", create); // int kimlik veritabanında
        Assert.Contains("_mapper.Map(request, color);", solution.Read(feature + "Commands/Update/UpdateColorCommand.cs"));

        string profiles = solution.Read(feature + "Profiles/MappingProfiles.cs");
        Assert.Contains("CreateMap<CreateColorCommand, Color>();", profiles);
        Assert.Contains("CreateMap<UpdateColorCommand, Color>().ForMember(x => x.Id, o => o.Ignore());", profiles);
        Assert.Contains("\"{Id}\"", solution.Read("src/Shop.WebAPI/Controllers/ColorsController.cs"));
        Assert.Contains(".OrderByDescending(x => x.CreatedAt)", solution.Read(feature + "Queries/GetList/GetListColorQuery.cs"));
    }

    [Fact]
    public void Leaves_todos_when_entity_cannot_be_created_or_updated()
    {
        using var solution = new TestSolution();
        solution.Write("src/Shop.Domain/Entities/Tag.cs", """
            namespace Shop.Domain.Entities;

            public class Tag : Entity<int>
            {
                private Tag() { }
                public string Name { get; private set; } = "";
            }
            """);

        SolutionLayout layout = SolutionLayout.Find(solution.Root);
        CrudGenerator.Plan(layout, EntityReader.Read(layout, "Tag")).Apply();

        string create = solution.Read("src/Shop.Application/Features/Tags/Commands/Create/CreateTagCommand.cs");
        Assert.Contains("TODO(can-gen): Tag'de fabrika metodu", create);
        Assert.Contains("Tag tag = NewTag(request);", create);
        Assert.Contains("TODO(can-gen): Tag'de 'Update(...)' metodu ya da public setter'lar bulunamadı", solution.Read("src/Shop.Application/Features/Tags/Commands/Update/UpdateTagCommand.cs"));
        Assert.Contains(".OrderBy(x => x.Id)", solution.Read("src/Shop.Application/Features/Tags/Queries/GetList/GetListTagQuery.cs"));
    }

    [Fact]
    public void Dry_run_writes_nothing_and_force_overwrites()
    {
        using var solution = new TestSolution();
        solution.Write("src/Shop.Domain/Entities/Tag.cs", "namespace Shop.Domain.Entities; public class Tag : Entity<int> { public string Name { get; set; } = \"\"; }");
        SolutionLayout layout = SolutionLayout.Find(solution.Root);

        CrudGenerator.Plan(layout, EntityReader.Read(layout, "Tag")).Apply(dryRun: true);
        Assert.False(solution.Exists("src/Shop.WebAPI/Controllers/TagsController.cs"));
        Assert.DoesNotContain("DbSet<Tag>", solution.Read("src/Shop.Persistence/Contexts/BaseDbContext.cs"));

        CrudGenerator.Plan(layout, EntityReader.Read(layout, "Tag")).Apply();
        solution.Write("src/Shop.WebAPI/Controllers/TagsController.cs", "// değiştirildi");
        IReadOnlyList<ChangeResult> forced = CrudGenerator.Plan(layout, EntityReader.Read(layout, "Tag")).Apply(force: true);

        Assert.Contains(forced, r => r.Kind == ChangeKind.Overwritten && r.Path.EndsWith("TagsController.cs", StringComparison.Ordinal));
        Assert.Contains("class TagsController", solution.Read("src/Shop.WebAPI/Controllers/TagsController.cs"));
    }

    [Fact]
    public void Mechanisms_can_be_turned_off()
    {
        using var solution = new TestSolution();
        solution.Write("src/Shop.Domain/Entities/Brand.cs", "namespace Shop.Domain.Entities; public class Brand : Entity<Guid> { public string Name { get; set; } = \"\"; }");
        SolutionLayout layout = SolutionLayout.Find(solution.Root);

        CrudGenerator.Plan(layout, EntityReader.Read(layout, "Brand"), new CrudOptions { Caching = false, Logging = false, Transaction = false, Secured = false, DynamicQuery = false }).Apply();

        string create = solution.Read(Feature + "Commands/Create/CreateBrandCommand.cs");
        Assert.Contains("public sealed class CreateBrandCommand : IRequest<CreatedBrandResponse>\n", create.ReplaceLineEndings("\n"));
        Assert.DoesNotContain("Permissions", create);
        Assert.Contains("transaction kapalı: handler kendisi kaydeder", create);
        Assert.Contains("await _unitOfWork.SaveChangesAsync(cancellationToken);", solution.Read(Feature + "Commands/Delete/DeleteBrandCommand.cs"));
        Assert.DoesNotContain("ICachableRequest", solution.Read(Feature + "Queries/GetList/GetListBrandQuery.cs"));
        Assert.False(solution.Exists(Feature + "Queries/GetListByDynamic/GetListByDynamicBrandQuery.cs"));
        Assert.DoesNotContain("GetList/ByDynamic", solution.Read("src/Shop.WebAPI/Controllers/BrandsController.cs"));

        // Dinamik sorgu sonradan eklenebilir.
        CrudGenerator.PlanDynamicQuery(layout, EntityReader.Read(layout, "Brand")).Apply();
        Assert.True(solution.Exists(Feature + "Queries/GetListByDynamic/GetListByDynamicBrandQuery.cs"));
        string controller = solution.Read("src/Shop.WebAPI/Controllers/BrandsController.cs");
        Assert.Contains("[HttpPost(\"GetList/ByDynamic\")]", controller);
        Assert.Contains("[AcceptVerbs(\"QUERY\", Route = \"GetList/ByDynamic\")]", controller);
        Assert.Equal(1, controller.Split("public Task<IActionResult> QueryListByDynamic").Length - 1);
        Assert.Contains("using Shop.Application.Features.Brands.Queries.GetListByDynamic;", controller);
        Assert.Contains("CreateMap<Brand, GetListByDynamicBrandListItemDto>();", solution.Read(Feature + "Profiles/MappingProfiles.cs"));
    }

    [Fact]
    public void Adds_command_and_query_to_existing_feature()
    {
        using var solution = new TestSolution();
        solution.Write("src/Shop.Domain/Entities/Brand.cs", "namespace Shop.Domain.Entities; public class Brand : Entity<Guid> { public string Name { get; set; } = \"\"; }");
        SolutionLayout layout = SolutionLayout.Find(solution.Root);
        CrudGenerator.Plan(layout, EntityReader.Read(layout, "Brand")).Apply();

        Assert.Equal(new[] { "Brands" }, FeatureGenerator.ListFeatures(layout));

        IReadOnlyList<ChangeResult> command = FeatureGenerator.PlanCommand(layout, "Brands", "SyncBrandCommand", new RequestOptions { EndpointMethod = "put" }).Apply();
        Assert.DoesNotContain(command, r => r.Kind == ChangeKind.Failed);
        string syncFolder = Feature + "Commands/SyncBrand/";
        Assert.True(solution.Exists(syncFolder + "SyncBrandCommand.cs"));
        Assert.True(solution.Exists(syncFolder + "SyncBrandCommandValidator.cs"));
        Assert.True(solution.Exists(syncFolder + "SyncBrandResponse.cs"));
        string sync = solution.Read(syncFolder + "SyncBrandCommand.cs");
        Assert.Contains("[BrandsOperationClaims.Admin, BrandsOperationClaims.Write, BrandsOperationClaims.SyncBrand]", sync);
        Assert.Contains("ICacheRemoverRequest", sync);
        Assert.Contains("CacheTags.Entity<Brand>()", sync);

        FeatureGenerator.PlanQuery(layout, "Brands", "GetBrandByName").Apply();
        Assert.Contains("ICachableRequest", solution.Read(Feature + "Queries/GetBrandByName/GetBrandByNameQuery.cs"));

        string claims = solution.Read(Feature + "Constants/BrandsOperationClaims.cs");
        Assert.Contains("public const string SyncBrand = \"brands.sync-brand\";", claims);
        Assert.Contains("(GetBrandByName, \"Get brand by name\"),", claims);

        string controller = solution.Read("src/Shop.WebAPI/Controllers/BrandsController.cs").ReplaceLineEndings("\n");
        Assert.Contains("    [HttpPut(\"SyncBrand\")]\n    public async Task<IActionResult> SyncBrand([FromBody] SyncBrandCommand syncBrandCommand, CancellationToken cancellationToken)", controller);
        Assert.Contains("[HttpGet(\"GetBrandByName\")]", controller);
        Assert.Contains("[FromQuery] GetBrandByNameQuery getBrandByNameQuery", controller);
        Assert.Contains("using Shop.Application.Features.Brands.Commands.SyncBrand;", controller);

        Assert.Throws<GeneratorException>(() => FeatureGenerator.PlanCommand(layout, "Nope", "X"));
    }
}
