using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using Can.Core.Mapping.Internal;

namespace Can.Core.Mapping;

/// <summary>Varsayılan <see cref="IMapper"/>. Thread-safe'tir; singleton olarak kullanılır.</summary>
public sealed class Mapper : IMapper
{
    public Mapper(MapperConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        Configuration = configuration;
    }

    public MapperConfiguration Configuration { get; }

    [return: NotNullIfNotNull(nameof(source))]
    public TDestination? Map<TDestination>(object? source)
    {
        if (source is null)
            return default;

        return (TDestination)Map(source, source.GetType(), typeof(TDestination));
    }

    [return: NotNullIfNotNull(nameof(source))]
    public TDestination? Map<TSource, TDestination>(TSource? source)
    {
        if (source is null)
            return default;

        // Derleme anındaki tip için tanım varsa tipli (boxing'siz) delegate kullanılır.
        if (Configuration.FindPlan(typeof(TSource), typeof(TDestination)) is { } plan)
            return ((Func<TSource, TDestination>)plan.Map)(source)!; // source null değilse eşleme her zaman bir nesne üretir

        return (TDestination)Map(source, source.GetType(), typeof(TDestination));
    }

    public TDestination Map<TSource, TDestination>(TSource source, TDestination destination)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);

        MapPlan plan =
            Configuration.FindPlan(typeof(TSource), typeof(TDestination))
            ?? throw MapperConfiguration.MissingMap(typeof(TSource), typeof(TDestination));

        return ((Func<TSource, TDestination, TDestination>)plan.MapInto)(source, destination);
    }

    public object Map(object source, Type sourceType, Type destinationType)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sourceType);
        ArgumentNullException.ThrowIfNull(destinationType);

        if (Configuration.FindPlan(sourceType, destinationType) is { } plan)
            return plan.UntypedMap(source);

        if (TryMapCollection(source, destinationType, out object? collection))
            return collection;

        if (destinationType.IsInstanceOfType(source))
            return source;

        throw MapperConfiguration.MissingMap(sourceType, destinationType);
    }

    public IQueryable<TDestination> ProjectTo<TDestination>(IQueryable source)
    {
        ArgumentNullException.ThrowIfNull(source);

        LambdaExpression projection = Configuration.GetProjection(source.ElementType, typeof(TDestination));

        MethodCallExpression select = Expression.Call(
            typeof(Queryable),
            nameof(Queryable.Select),
            new[] { source.ElementType, typeof(TDestination) },
            source.Expression,
            Expression.Quote(projection)
        );

        return source.Provider.CreateQuery<TDestination>(select);
    }

    /// <summary><c>List&lt;Product&gt;</c> → <c>List&lt;ProductDto&gt;</c> / <c>ProductDto[]</c> / <c>IEnumerable&lt;ProductDto&gt;</c> ...</summary>
    private bool TryMapCollection(object source, Type destinationType, [NotNullWhen(true)] out object? result)
    {
        result = null;

        Type? destinationElement = ReflectionHelper.GetElementType(destinationType);
        if (destinationElement is null || source is string || source is not IEnumerable items)
            return false;

        var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(destinationElement))!;
        foreach (object? item in items)
            list.Add(item is null ? null : Map(item, item.GetType(), destinationElement));

        if (destinationType.IsArray)
        {
            var array = Array.CreateInstance(destinationElement, list.Count);
            list.CopyTo(array, 0);
            result = array;
            return true;
        }

        if (destinationType.IsInstanceOfType(list))
        {
            result = list;
            return true;
        }

        return false;
    }
}
