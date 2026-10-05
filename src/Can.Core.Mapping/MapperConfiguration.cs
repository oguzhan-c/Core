using System.Collections.Concurrent;
using System.Linq.Expressions;
using Can.Core.Mapping.Internal;

namespace Can.Core.Mapping;

/// <summary>
/// Tüm profillerdeki eşleme tanımlarını tutar. Uygulama boyunca tek örnek (singleton) olmalıdır;
/// derlenmiş eşlemeler burada cache'lenir.
/// </summary>
public sealed class MapperConfiguration
{
    private readonly Dictionary<(Type Source, Type Destination), TypeMapDefinition> _definitions = [];
    private readonly ConcurrentDictionary<(Type Source, Type Destination), TypeMapDefinition?> _definitionLookup = new();
    private readonly ConcurrentDictionary<(Type Source, Type Destination), MapPlan?> _plans = new();

    public MapperConfiguration(params MappingProfile[] profiles)
        : this((IEnumerable<MappingProfile>)profiles) { }

    public MapperConfiguration(IEnumerable<MappingProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);

        foreach (MappingProfile profile in profiles)
        {
            foreach (TypeMapDefinition definition in profile.Definitions)
            {
                if (!_definitions.TryAdd((definition.SourceType, definition.DestinationType), definition))
                {
                    throw new MappingConfigurationException(
                        $"'{definition}' eşlemesi birden fazla kez tanımlanmış (son tanım: {profile.GetType().Name})."
                    );
                }
            }
        }
    }

    public IMapper CreateMapper() => new Mapper(this);

    /// <summary>
    /// Tüm eşlemeleri inşa eder; eşlenemeyen hedef üyeleri ve kullanılamayan constructor'ları
    /// tek bir hatada listeler. Uygulama açılışında ya da bir birim testinde çağırman önerilir.
    /// </summary>
    public void AssertConfigurationIsValid()
    {
        var errors = new SortedSet<string>(StringComparer.Ordinal);

        foreach (TypeMapDefinition definition in _definitions.Values)
        {
            var builder = new MapExpressionBuilder(this, inMemory: false, unmappedMembers: errors);
            try
            {
                builder.BuildNew(definition, Expression.Parameter(definition.SourceType, "source"));
            }
            catch (MappingConfigurationException ex)
            {
                errors.Add(ex.Message);
            }
        }

        if (errors.Count > 0)
        {
            throw new MappingConfigurationException(
                "Eşlenemeyen üyeler var. ForMember(..., o => o.MapFrom(...)) ile kaynak göster ya da "
                    + "o => o.Ignore() ile atla:"
                    + Environment.NewLine
                    + string.Join(Environment.NewLine, errors.Select(e => "  - " + e))
            );
        }
    }

    /// <summary>EF Core / LINQ için projection ifadesi.</summary>
    public Expression<Func<TSource, TDestination>> GetProjection<TSource, TDestination>() =>
        (Expression<Func<TSource, TDestination>>)GetProjection(typeof(TSource), typeof(TDestination));

    /// <summary>Tipleri çalışma anında belli olan projection.</summary>
    public LambdaExpression GetProjection(Type sourceType, Type destinationType) =>
        (FindPlan(sourceType, destinationType) ?? throw MissingMap(sourceType, destinationType)).Projection;

    /// <summary>Kaynak tipi ya da base sınıflarından biri için tanım arar.</summary>
    internal TypeMapDefinition? FindDefinition(Type sourceType, Type destinationType) =>
        _definitionLookup.GetOrAdd(
            (sourceType, destinationType),
            key =>
            {
                for (Type? type = key.Source; type is not null; type = type.BaseType)
                {
                    if (_definitions.TryGetValue((type, key.Destination), out TypeMapDefinition? definition))
                        return definition;
                }

                return null;
            }
        );

    internal MapPlan? FindPlan(Type sourceType, Type destinationType) =>
        _plans.GetOrAdd(
            (sourceType, destinationType),
            key => FindDefinition(key.Source, key.Destination) is { } definition
                ? new MapPlan(this, definition, key.Source)
                : null
        );

    internal static MappingException MissingMap(Type sourceType, Type destinationType) =>
        new(
            $"'{sourceType.Name}' -> '{destinationType.Name}' için eşleme tanımlı değil. "
                + $"Bir MappingProfile içinde CreateMap<{sourceType.Name}, {destinationType.Name}>() ekle."
        );
}
