using Can.Core.Mapping.Internal;

namespace Can.Core.Mapping;

/// <summary>
/// Eşleme tanımlarının yazıldığı sınıf. <c>AddCanMapping(assembly)</c> bu sınıftan türeyen
/// tüm tipleri (iç içe private sınıflar dahil) bulur.
/// </summary>
/// <example>
/// <code>
/// public sealed class ProductProfile : MappingProfile
/// {
///     public ProductProfile()
///     {
///         CreateMap&lt;Product, ProductDto&gt;()
///             .ForMember(d => d.BrandName, o => o.MapFrom(s => s.Brand.Name));
///
///         CreateMap&lt;CreateProductCommand, Product&gt;();
///     }
/// }
/// </code>
/// </example>
public abstract class MappingProfile
{
    private readonly List<TypeMapDefinition> _definitions = [];

    internal IReadOnlyList<TypeMapDefinition> Definitions => _definitions;

    protected IMappingExpression<TSource, TDestination> CreateMap<TSource, TDestination>()
    {
        var definition = new TypeMapDefinition(typeof(TSource), typeof(TDestination));
        _definitions.Add(definition);
        return new MappingExpression<TSource, TDestination>(definition, _definitions);
    }
}
