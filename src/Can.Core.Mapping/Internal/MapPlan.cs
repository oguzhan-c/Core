using System.Linq.Expressions;

namespace Can.Core.Mapping.Internal;

/// <summary>
/// Belirli bir (kaynak tipi, hedef tipi) çifti için derlenmiş eşleme. Her parça ilk kullanımda
/// üretilir ve sonra cache'ten gelir.
/// </summary>
internal sealed class MapPlan
{
    private readonly Lazy<LambdaExpression> _projection;
    private readonly Lazy<Delegate> _map;
    private readonly Lazy<Func<object, object>> _untypedMap;
    private readonly Lazy<Delegate> _mapInto;

    public MapPlan(MapperConfiguration configuration, TypeMapDefinition definition, Type sourceType)
    {
        SourceType = sourceType;
        DestinationType = definition.DestinationType;

        _projection = new Lazy<LambdaExpression>(() =>
            new MapExpressionBuilder(configuration, inMemory: false).BuildMapLambda(definition, sourceType)
        );

        _map = new Lazy<Delegate>(() =>
            new MapExpressionBuilder(configuration, inMemory: true).BuildMapLambda(definition, sourceType).Compile()
        );

        _untypedMap = new Lazy<Func<object, object>>(() => CompileUntyped(_map.Value, sourceType));

        _mapInto = new Lazy<Delegate>(() =>
            new MapExpressionBuilder(configuration, inMemory: true).BuildMapIntoLambda(definition, sourceType).Compile()
        );
    }

    public Type SourceType { get; }

    public Type DestinationType { get; }

    /// <summary>EF Core için <c>Expression&lt;Func&lt;TSource, TDestination&gt;&gt;</c>.</summary>
    public LambdaExpression Projection => _projection.Value;

    /// <summary><c>Func&lt;TSource, TDestination&gt;</c></summary>
    public Delegate Map => _map.Value;

    /// <summary><c>Func&lt;object, object&gt;</c></summary>
    public Func<object, object> UntypedMap => _untypedMap.Value;

    /// <summary><c>Func&lt;TSource, TDestination, TDestination&gt;</c></summary>
    public Delegate MapInto => _mapInto.Value;

    private static Func<object, object> CompileUntyped(Delegate typedMap, Type sourceType)
    {
        ParameterExpression source = Expression.Parameter(typeof(object), "source");

        Expression call = Expression.Invoke(Expression.Constant(typedMap), Expression.Convert(source, sourceType));

        return Expression
            .Lambda<Func<object, object>>(Expression.Convert(call, typeof(object)), source)
            .Compile();
    }
}
