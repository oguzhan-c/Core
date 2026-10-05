using Can.Core.Mapping.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

namespace Can.Core.Mapping.Tests;

public class MapperTests
{
    private sealed class TestProfile : MappingProfile
    {
        public TestProfile()
        {
            CreateMap<Tag, TagDto>();

            CreateMap<Product, ProductDto>();

            CreateMap<Product, ProductSummary>();

            CreateMap<Customer, CustomerDto>()
                .ForMember(d => d.FullName, o => o.MapFrom(s => s.FirstName + " " + s.LastName))
                .ForMember(d => d.Secret, o => o.Ignore());

            CreateMap<UpdateProductCommand, Product>()
                .ForMember(d => d.Id, o => o.Ignore())
                .ForMember(d => d.Status, o => o.Ignore())
                .ForMember(d => d.Brand, o => o.Ignore())
                .ForMember(d => d.Tags, o => o.Ignore())
                .ForMember(d => d.Stock, o => o.Ignore())
                .ForMember(d => d.InternalNote, o => o.Ignore());

            CreateMap<ProductEditModel, Customer>()
                .ForMember(d => d.FirstName, o => o.MapFrom(s => s.Name))
                .ForMember(d => d.LastName, o => o.Ignore())
                .ForMember(d => d.Address, o => o.Ignore())
                .AfterMap((s, d) => d.LastName = "after-map");
        }
    }

    private static readonly IMapper TestMapper = new MapperConfiguration(new TestProfile()).CreateMapper();

    private static Product SampleProduct() =>
        new()
        {
            Id = 1,
            Name = "Kalem",
            Price = 12.5m,
            Status = ProductStatus.Active,
            Brand = new Brand { Name = "Faber" },
            Tags = [new Tag { Name = "kırtasiye" }, new Tag { Name = "ofis" }],
            Stock = 40,
        };

    [Fact]
    public void Maps_by_convention_flattening_enum_nullable_and_collections()
    {
        ProductDto dto = TestMapper.Map<ProductDto>(SampleProduct());

        Assert.Equal(1, dto.Id);
        Assert.Equal("Kalem", dto.Name);
        Assert.Equal(12.5m, dto.Price);
        Assert.Equal("Active", dto.Status); // enum → string
        Assert.Equal("Faber", dto.BrandName); // Brand.Name → BrandName
        Assert.Equal(40, dto.Stock); // int? → int
        Assert.Equal(new[] { "kırtasiye", "ofis" }, dto.Tags.Select(t => t.Name));
    }

    [Fact]
    public void Null_navigation_does_not_throw_in_memory()
    {
        Product product = SampleProduct();
        product.Brand = null;
        product.Stock = null;

        ProductDto dto = TestMapper.Map<Product, ProductDto>(product);

        Assert.Null(dto.BrandName);
        Assert.Equal(0, dto.Stock);
    }

    [Fact]
    public void Null_source_returns_null()
    {
        Assert.Null(TestMapper.Map<ProductDto>(null));
        Assert.Null(TestMapper.Map<Product, ProductDto>(null));
    }

    [Fact]
    public void Positional_record_is_created_through_constructor()
    {
        ProductSummary summary = TestMapper.Map<ProductSummary>(SampleProduct());

        Assert.Equal(new ProductSummary(1, "Kalem") { BrandName = "Faber" }, summary);
    }

    [Fact]
    public void MapFrom_and_Ignore_are_respected()
    {
        var customer = new Customer
        {
            Id = 3,
            FirstName = "Ada",
            LastName = "Lovelace",
            Address = new Address { City = "Londra" },
        };

        CustomerDto dto = TestMapper.Map<CustomerDto>(customer);

        Assert.Equal("Ada Lovelace", dto.FullName);
        Assert.Equal("Londra", dto.AddressCity);
        Assert.Null(dto.Secret);
    }

    [Fact]
    public void Maps_into_existing_object()
    {
        Product product = SampleProduct();
        var command = new UpdateProductCommand { Id = 999, Name = "Dolma kalem", Price = 99m };

        Product result = TestMapper.Map(command, product);

        Assert.Same(product, result);
        Assert.Equal(1, product.Id); // Ignore edildi
        Assert.Equal("Dolma kalem", product.Name);
        Assert.Equal(99m, product.Price);
        Assert.Equal("Faber", product.Brand!.Name); // dokunulmadı
    }

    [Fact]
    public void AfterMap_runs_after_mapping()
    {
        Customer customer = TestMapper.Map<Customer>(new ProductEditModel { Id = 5, Name = "Grace" });

        Assert.Equal(5, customer.Id);
        Assert.Equal("Grace", customer.FirstName);
        Assert.Equal("after-map", customer.LastName);
    }

    [Fact]
    public void Maps_collections_at_top_level()
    {
        Product[] products = [SampleProduct(), SampleProduct()];

        List<ProductDto> list = TestMapper.Map<List<ProductDto>>(products);
        ProductDto[] array = TestMapper.Map<ProductDto[]>(products.ToList());
        IEnumerable<ProductDto> sequence = TestMapper.Map<IEnumerable<ProductDto>>(products);

        Assert.Equal(2, list.Count);
        Assert.Equal(2, array.Length);
        Assert.Equal(2, sequence.Count());
    }

    [Fact]
    public void ProjectTo_builds_a_select_expression()
    {
        IQueryable<Product> query = new[] { SampleProduct() }.AsQueryable();

        List<ProductDto> result = query.ProjectTo<ProductDto>(TestMapper).ToList();

        ProductDto dto = Assert.Single(result);
        Assert.Equal("Faber", dto.BrandName);
        Assert.Equal(2, dto.Tags.Count);
    }

    [Fact]
    public void Projection_expression_has_no_in_memory_null_guards_for_flattening()
    {
        string projection = TestMapper.Configuration.GetProjection<Product, ProductDto>().ToString();

        Assert.Contains("BrandName = source.Brand.Name", projection);
    }

    [Fact]
    public void Missing_map_throws()
    {
        Assert.Throws<MappingException>(() => TestMapper.Map<Unmappable>(SampleProduct()));
    }

    [Fact]
    public void Valid_configuration_passes_validation()
    {
        TestMapper.Configuration.AssertConfigurationIsValid();
    }

    [Fact]
    public void Validation_reports_unmapped_members()
    {
        var configuration = new MapperConfiguration(new InvalidProfile());

        var ex = Assert.Throws<MappingConfigurationException>(configuration.AssertConfigurationIsValid);

        Assert.Contains("Unmappable.NotInSource", ex.Message);
    }

    [Fact]
    public void Duplicate_maps_are_rejected()
    {
        Assert.Throws<MappingConfigurationException>(() => new MapperConfiguration(new InvalidProfile(), new InvalidProfile()));
    }

    [Fact]
    public void ReverseMap_creates_the_opposite_map()
    {
        var mapper = new MapperConfiguration(new ReverseProfile()).CreateMapper();

        UpdateProductCommand command = mapper.Map<UpdateProductCommand>(new ProductEditModel { Id = 1, Name = "x", Price = 2 });
        ProductEditModel model = mapper.Map<ProductEditModel>(command);

        Assert.Equal("x", model.Name);
        Assert.Equal(2, model.Price);
    }

    [Fact]
    public void Self_referencing_types_do_not_overflow()
    {
        var mapper = new MapperConfiguration(new CategoryProfile()).CreateMapper();
        var category = new Category { Name = "Kalem", Parent = new Category { Name = "Kırtasiye" } };

        CategoryDto dto = mapper.Map<CategoryDto>(category);

        Assert.Equal("Kalem", dto.Name);
        Assert.Null(dto.Parent); // döngüyü kapatan üye atlanır
    }

    [Fact]
    public void AddCanMapping_finds_nested_private_profiles()
    {
        var services = new ServiceCollection();
        services.AddCanMapping(typeof(MapperTests).Assembly);

        using ServiceProvider provider = services.BuildServiceProvider();
        var mapper = provider.GetRequiredService<IMapper>();

        Assert.Same(provider.GetRequiredService<MapperConfiguration>(), mapper.Configuration);
        Assert.Equal("Kalem", mapper.Map<ProductDto>(SampleProduct()).Name);
    }

    private sealed class InvalidProfile : MappingProfile
    {
        public InvalidProfile() => CreateMap<Product, Unmappable>();
    }

    private sealed class ReverseProfile : MappingProfile
    {
        public ReverseProfile() => CreateMap<ProductEditModel, UpdateProductCommand>().ReverseMap();
    }

    private sealed class CategoryProfile : MappingProfile
    {
        public CategoryProfile() => CreateMap<Category, CategoryDto>();
    }
}
