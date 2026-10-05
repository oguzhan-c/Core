using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Can.Core.Mapping.DependencyInjection;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Verilen assembly'lerdeki tüm <see cref="MappingProfile"/>'ları bulur (iç içe private
    /// sınıflar dahil), <see cref="MapperConfiguration"/> ve <see cref="IMapper"/>'ı singleton kaydeder.
    /// </summary>
    /// <example>
    /// <code>services.AddCanMapping(typeof(ProductProfile).Assembly);</code>
    /// </example>
    public static IServiceCollection AddCanMapping(this IServiceCollection services, params Assembly[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(assemblies);

        IEnumerable<MappingProfile> profiles = assemblies
            .Distinct()
            .SelectMany(GetLoadableTypes)
            .Where(type =>
                type is { IsClass: true, IsAbstract: false, IsGenericTypeDefinition: false }
                && typeof(MappingProfile).IsAssignableFrom(type)
            )
            .Select(type => (MappingProfile)Activator.CreateInstance(type, nonPublic: true)!);

        var configuration = new MapperConfiguration(profiles);

        services.TryAddSingleton(configuration);
        services.TryAddSingleton<IMapper>(new Mapper(configuration));

        return services;
    }

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.OfType<Type>();
        }
    }
}
