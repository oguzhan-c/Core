using System.Linq.Expressions;
using System.Reflection;

namespace Can.Core.Mapping.Internal;

/// <summary>
/// Bir <see cref="TypeMapDefinition"/>'dan expression tree üretir.
/// </summary>
/// <remarks>
/// <para>
/// İki modda çalışır:
/// <list type="bullet">
/// <item><b>Projection</b> (<c>inMemory = false</c>): EF Core'un SQL'e çevirebileceği saf bir
/// <c>new Dto { ... }</c> ifadesi. Null kontrolleri, AfterMap gibi bellek işlemleri eklenmez.</item>
/// <item><b>Bellek</b> (<c>inMemory = true</c>): derlenip çalıştırılacak ifade. Zincirlerde null
/// koruması ve AfterMap çağrıları eklenir.</item>
/// </list>
/// </para>
/// <para>
/// İç içe map'ler inline edilir. Döngüsel tipler (Order → Customer → Orders) sonsuz döngüye
/// girmesin diye o an inşa edilen map'ler takip edilir; döngüyü kapatan üye atlanır.
/// </para>
/// </remarks>
internal sealed class MapExpressionBuilder
{
    private readonly MapperConfiguration _configuration;
    private readonly bool _inMemory;
    private readonly ICollection<string>? _unmappedMembers;
    private readonly HashSet<(Type Source, Type Destination)> _building = [];

    public MapExpressionBuilder(
        MapperConfiguration configuration,
        bool inMemory,
        ICollection<string>? unmappedMembers = null)
    {
        _configuration = configuration;
        _inMemory = inMemory;
        _unmappedMembers = unmappedMembers;
    }

    // ---------------------------------------------------------------- Lambda'lar

    /// <summary><c>source =&gt; new TDestination { ... }</c> (+ bellek modunda AfterMap'ler).</summary>
    public LambdaExpression BuildMapLambda(TypeMapDefinition definition, Type sourceType)
    {
        ParameterExpression source = Expression.Parameter(sourceType, "source");
        Expression body = BuildNew(definition, source);

        if (_inMemory && definition.AfterMaps.Count > 0)
        {
            ParameterExpression destination = Expression.Variable(definition.DestinationType, "destination");
            var expressions = new List<Expression> { Expression.Assign(destination, body) };
            expressions.AddRange(AfterMapCalls(definition, source, destination));
            expressions.Add(destination);
            body = Expression.Block(new[] { destination }, expressions);
        }

        return Expression.Lambda(body, source);
    }

    /// <summary><c>(source, destination) =&gt; { destination.X = ...; return destination; }</c></summary>
    public LambdaExpression BuildMapIntoLambda(TypeMapDefinition definition, Type sourceType)
    {
        ParameterExpression source = Expression.Parameter(sourceType, "source");
        ParameterExpression destination = Expression.Parameter(definition.DestinationType, "destination");

        var expressions = new List<Expression>();

        _building.Add((definition.SourceType, definition.DestinationType));
        try
        {
            // init-only üyeler mevcut nesnede değiştirilmez (değişmezlik korunur).
            foreach (PropertyInfo property in ReflectionHelper.GetWritableProperties(definition.DestinationType, includeInitOnly: false))
            {
                if (definition.IsIgnored(property.Name))
                    continue;

                Expression? value = ResolveMember(definition, property.Name, property.PropertyType, source);
                if (value is null)
                {
                    ReportUnmapped(definition, property.Name);
                    continue;
                }

                expressions.Add(Expression.Assign(Expression.Property(destination, property), value));
            }
        }
        finally
        {
            _building.Remove((definition.SourceType, definition.DestinationType));
        }

        expressions.AddRange(AfterMapCalls(definition, source, destination));
        expressions.Add(destination);

        return Expression.Lambda(Expression.Block(expressions), source, destination);
    }

    private static IEnumerable<Expression> AfterMapCalls(
        TypeMapDefinition definition,
        Expression source,
        Expression destination) =>
        definition.AfterMaps.Select(action => (Expression)Expression.Invoke(Expression.Constant(action), source, destination));

    // ---------------------------------------------------------------- Nesne oluşturma

    /// <summary><c>new TDestination(ctorArgs) { Prop = ..., ... }</c></summary>
    public Expression BuildNew(TypeMapDefinition definition, Expression source)
    {
        _building.Add((definition.SourceType, definition.DestinationType));
        try
        {
            (NewExpression newExpression, HashSet<string> constructorMembers) = BuildConstructor(definition, source);

            var bindings = new List<MemberBinding>();
            foreach (PropertyInfo property in ReflectionHelper.GetWritableProperties(definition.DestinationType, includeInitOnly: true))
            {
                if (constructorMembers.Contains(property.Name) || definition.IsIgnored(property.Name))
                    continue;

                Expression? value = ResolveMember(definition, property.Name, property.PropertyType, source);
                if (value is null)
                {
                    ReportUnmapped(definition, property.Name);
                    continue;
                }

                bindings.Add(Expression.Bind(property, value));
            }

            return bindings.Count == 0 ? newExpression : Expression.MemberInit(newExpression, bindings);
        }
        finally
        {
            _building.Remove((definition.SourceType, definition.DestinationType));
        }
    }

    /// <summary>
    /// Parametresiz constructor varsa onu, yoksa (ör. positional record) tüm parametreleri
    /// kaynaktan doldurulabilen en geniş constructor'ı kullanır.
    /// </summary>
    private (NewExpression Constructor, HashSet<string> Members) BuildConstructor(
        TypeMapDefinition definition,
        Expression source)
    {
        Type destinationType = definition.DestinationType;
        var noMembers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (destinationType.IsValueType)
            return (Expression.New(destinationType), noMembers);

        ConstructorInfo? parameterless = destinationType.GetConstructor(Type.EmptyTypes);
        if (parameterless is not null)
            return (Expression.New(parameterless), noMembers);

        IEnumerable<ConstructorInfo> constructors = destinationType
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .OrderByDescending(c => c.GetParameters().Length);

        foreach (ConstructorInfo constructor in constructors)
        {
            ParameterInfo[] parameters = constructor.GetParameters();
            var arguments = new Expression[parameters.Length];
            bool resolved = true;

            for (int i = 0; i < parameters.Length; i++)
            {
                string? name = parameters[i].Name;
                Expression? argument = name is null
                    ? null
                    : ResolveMember(definition, name, parameters[i].ParameterType, source);

                if (argument is null)
                {
                    resolved = false;
                    break;
                }

                arguments[i] = argument;
            }

            if (resolved)
            {
                var members = new HashSet<string>(parameters.Select(p => p.Name!), StringComparer.OrdinalIgnoreCase);
                return (Expression.New(constructor, arguments), members);
            }
        }

        throw new MappingConfigurationException(
            $"{definition}: '{destinationType.Name}' için kullanılabilir constructor yok. Parametresiz bir constructor ekle "
                + "ya da constructor parametrelerinin hepsinin kaynakta karşılığı olsun (gerekirse ForMember(\"param\", o => o.MapFrom(...)))."
        );
    }

    // ---------------------------------------------------------------- Üye çözümleme

    /// <summary>
    /// Hedef üyenin değerini üretir: önce ForMember kuralı, yoksa konvansiyon
    /// (aynı isim, ardından flattening: <c>CustomerName</c> → <c>Customer.Name</c>).
    /// Eşlenemiyorsa <see langword="null"/>.
    /// </summary>
    private Expression? ResolveMember(TypeMapDefinition definition, string memberName, Type memberType, Expression source)
    {
        if (definition.Members.TryGetValue(memberName, out MemberDefinition? member))
        {
            if (member.Ignored)
                return null;

            if (member.SourceExpression is { } lambda)
            {
                ParameterExpression lambdaParameter = lambda.Parameters[0];
                Expression body = _inMemory ? NullSafeVisitor.Apply(lambda.Body, lambdaParameter) : lambda.Body;
                body = ParameterReplacer.Replace(body, lambdaParameter, source);
                return ConvertValue(body, memberType);
            }
        }

        List<MemberInfo>? path = FindSourcePath(source.Type, memberName, depth: 0);
        return path is null ? null : ConvertValue(BuildMemberChain(source, path), memberType);
    }

    private static List<MemberInfo>? FindSourcePath(Type type, string name, int depth)
    {
        MemberInfo? direct = ReflectionHelper.FindReadableMember(type, name);
        if (direct is not null)
            return [direct];

        if (depth >= 4)
            return null;

        // Flattening: en uzun önek önce denenir (CustomerAddressCity → CustomerAddress.City > Customer.Address.City)
        IEnumerable<MemberInfo> candidates = ReflectionHelper
            .GetReadableMembers(type)
            .Where(m => !ReflectionHelper.IsLeafType(ReflectionHelper.GetMemberType(m)))
            .OrderByDescending(m => m.Name.Length);

        foreach (MemberInfo candidate in candidates)
        {
            int length = candidate.Name.Length;
            if (name.Length <= length
                || !name.StartsWith(candidate.Name, StringComparison.OrdinalIgnoreCase)
                || !char.IsUpper(name[length]))
            {
                continue;
            }

            List<MemberInfo>? rest = FindSourcePath(ReflectionHelper.GetMemberType(candidate), name[length..], depth + 1);
            if (rest is not null)
            {
                rest.Insert(0, candidate);
                return rest;
            }
        }

        return null;
    }

    /// <summary><c>source.A.B.C</c>; bellek modunda ara adımlar için null koruması eklenir.</summary>
    private Expression BuildMemberChain(Expression source, List<MemberInfo> path)
    {
        Expression current = source;
        var nullChecks = new List<Expression>();

        for (int i = 0; i < path.Count; i++)
        {
            if (i > 0 && ReflectionHelper.CanBeNull(current.Type))
                nullChecks.Add(current);

            current = Expression.MakeMemberAccess(current, path[i]);
        }

        if (!_inMemory || nullChecks.Count == 0)
            return current;

        Expression anyNull = nullChecks
            .Select(e => (Expression)Expression.Equal(e, Expression.Constant(null, e.Type)))
            .Aggregate(Expression.OrElse);

        return Expression.Condition(anyNull, Expression.Default(current.Type), current);
    }

    // ---------------------------------------------------------------- Tip dönüşümleri

    /// <summary>
    /// <paramref name="value"/>'yu tam olarak <paramref name="destinationType"/> tipinde bir ifadeye
    /// çevirir. Desteklenmeyen dönüşümde <see langword="null"/>.
    /// </summary>
    private Expression? ConvertValue(Expression value, Type destinationType)
    {
        Type sourceType = value.Type;

        if (sourceType == destinationType)
            return value;

        // 1) Tanımlı bir map varsa iç içe nesne olarak inşa et
        TypeMapDefinition? nested = _configuration.FindDefinition(sourceType, destinationType);
        if (nested is not null && nested.DestinationType == destinationType)
            return BuildNested(nested, value);

        // 2) Doğrudan atanabiliyorsa (türetilmiş → base, T → T?)
        if (destinationType.IsAssignableFrom(sourceType))
            return Expression.Convert(value, destinationType);

        // 3) Koleksiyonlar
        if (ReflectionHelper.GetElementType(destinationType) is not null && ReflectionHelper.GetElementType(sourceType) is not null)
            return BuildCollection(value, destinationType);

        Type? sourceUnderlying = Nullable.GetUnderlyingType(sourceType);
        Type? destinationUnderlying = Nullable.GetUnderlyingType(destinationType);

        // 4) T? → T (null ise default)
        if (sourceUnderlying == destinationType)
            return Expression.Coalesce(value, Expression.Default(destinationType));

        // 5) enum → string
        if (destinationType == typeof(string) && sourceType.IsEnum)
            return Expression.Call(value, sourceType.GetMethod(nameof(ToString), Type.EmptyTypes)!);

        // 6) Sayısal tipler ve enum'lar arası (int → long, decimal → double, enum ↔ int ...)
        Type plainSource = sourceUnderlying ?? sourceType;
        Type plainDestination = destinationUnderlying ?? destinationType;
        if (ReflectionHelper.IsNumericOrEnum(plainSource) && ReflectionHelper.IsNumericOrEnum(plainDestination))
        {
            Expression converted = value;
            if (sourceUnderlying is not null && destinationUnderlying is null)
                converted = Expression.Coalesce(converted, Expression.Default(sourceUnderlying));

            return Expression.Convert(converted, destinationType);
        }

        return null;
    }

    private Expression? BuildNested(TypeMapDefinition definition, Expression value)
    {
        // Döngü: bu map zaten inşa ediliyor → üyeyi atla
        if (_building.Contains((definition.SourceType, definition.DestinationType)))
            return null;

        Expression created = BuildNew(definition, value);

        if (!ReflectionHelper.CanBeNull(value.Type))
            return created;

        // Projection'da da gerekli: EF, "x.Customer == null ? null : new CustomerDto { ... }" ifadesini çevirir.
        return Expression.Condition(
            Expression.Equal(value, Expression.Constant(null, value.Type)),
            Expression.Default(definition.DestinationType),
            created
        );
    }

    private Expression? BuildCollection(Expression value, Type destinationType)
    {
        Type sourceElement = ReflectionHelper.GetElementType(value.Type)!;
        Type destinationElement = ReflectionHelper.GetElementType(destinationType)!;

        ParameterExpression item = Expression.Parameter(sourceElement, "item");
        Expression? itemBody = ConvertValue(item, destinationElement);
        if (itemBody is null)
            return null;

        Expression sequence = itemBody == item
            ? value
            : Expression.Call(
                typeof(Enumerable),
                nameof(Enumerable.Select),
                new[] { sourceElement, destinationElement },
                value,
                Expression.Lambda(itemBody, item)
            );

        Expression materialized;
        if (destinationType.IsArray)
        {
            materialized = Expression.Call(typeof(Enumerable), nameof(Enumerable.ToArray), new[] { destinationElement }, sequence);
        }
        else if (destinationType.IsAssignableFrom(typeof(List<>).MakeGenericType(destinationElement)))
        {
            materialized = Expression.Call(typeof(Enumerable), nameof(Enumerable.ToList), new[] { destinationElement }, sequence);
        }
        else if (destinationType.IsGenericType && destinationType.GetGenericTypeDefinition() == typeof(HashSet<>))
        {
            materialized = Expression.Call(typeof(Enumerable), nameof(Enumerable.ToHashSet), new[] { destinationElement }, sequence);
        }
        else
        {
            return null;
        }

        if (materialized.Type != destinationType)
            materialized = Expression.Convert(materialized, destinationType);

        if (!_inMemory || !ReflectionHelper.CanBeNull(value.Type))
            return materialized;

        return Expression.Condition(
            Expression.Equal(value, Expression.Constant(null, value.Type)),
            Expression.Default(destinationType),
            materialized
        );
    }

    private void ReportUnmapped(TypeMapDefinition definition, string memberName) =>
        _unmappedMembers?.Add($"{definition.SourceType.Name} -> {definition.DestinationType.Name}.{memberName}");
}
