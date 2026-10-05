using System.Linq.Expressions;

namespace Can.Core.Mapping.Internal;

internal sealed class MappingExpression<TSource, TDestination> : IMappingExpression<TSource, TDestination>
{
    private readonly TypeMapDefinition _definition;
    private readonly List<TypeMapDefinition> _profileDefinitions;

    public MappingExpression(TypeMapDefinition definition, List<TypeMapDefinition> profileDefinitions)
    {
        _definition = definition;
        _profileDefinitions = profileDefinitions;
    }

    public IMappingExpression<TSource, TDestination> ForMember<TMember>(
        Expression<Func<TDestination, TMember>> destinationMember,
        Action<IMemberOptions<TSource>> options)
    {
        ArgumentNullException.ThrowIfNull(destinationMember);
        return ForMember(GetMemberName(destinationMember), options);
    }

    public IMappingExpression<TSource, TDestination> ForMember(
        string destinationMemberName,
        Action<IMemberOptions<TSource>> options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationMemberName);
        ArgumentNullException.ThrowIfNull(options);

        options(new MemberOptions(_definition.GetOrAddMember(destinationMemberName)));
        return this;
    }

    public IMappingExpression<TSource, TDestination> AfterMap(Action<TSource, TDestination> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _definition.AfterMaps.Add(action);
        return this;
    }

    public IMappingExpression<TDestination, TSource> ReverseMap()
    {
        var reverse = new TypeMapDefinition(typeof(TDestination), typeof(TSource));
        _profileDefinitions.Add(reverse);
        return new MappingExpression<TDestination, TSource>(reverse, _profileDefinitions);
    }

    private static string GetMemberName(LambdaExpression expression)
    {
        Expression body = expression.Body;

        // Değer tipi üyeler object'e çevrilmişse: d => (object)d.Age
        if (body is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unary)
            body = unary.Operand;

        if (body is MemberExpression member && member.Expression == expression.Parameters[0])
            return member.Member.Name;

        throw new ArgumentException(
            $"ForMember yalnızca hedefin doğrudan bir üyesini seçebilir (ör. d => d.Name). Verilen: {expression}",
            nameof(expression)
        );
    }

    private sealed class MemberOptions : IMemberOptions<TSource>
    {
        private readonly MemberDefinition _member;

        public MemberOptions(MemberDefinition member) => _member = member;

        public void MapFrom<TSourceMember>(Expression<Func<TSource, TSourceMember>> sourceMember)
        {
            ArgumentNullException.ThrowIfNull(sourceMember);
            _member.SourceExpression = sourceMember;
            _member.Ignored = false;
        }

        public void Ignore()
        {
            _member.Ignored = true;
            _member.SourceExpression = null;
        }
    }
}
