using System.Linq.Expressions;

namespace Can.Core.Mapping.Internal;

/// <summary>Bir <c>CreateMap&lt;TSource, TDestination&gt;()</c> çağrısının topladığı kurallar.</summary>
internal sealed class TypeMapDefinition
{
    public TypeMapDefinition(Type sourceType, Type destinationType)
    {
        SourceType = sourceType;
        DestinationType = destinationType;
    }

    public Type SourceType { get; }

    public Type DestinationType { get; }

    /// <summary>Hedef üye adı → kural. Büyük/küçük harf duyarsız (constructor parametreleri için).</summary>
    public Dictionary<string, MemberDefinition> Members { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary><c>Action&lt;TSource, TDestination&gt;</c> delegate'leri.</summary>
    public List<Delegate> AfterMaps { get; } = [];

    public bool IsIgnored(string memberName) =>
        Members.TryGetValue(memberName, out MemberDefinition? member) && member.Ignored;

    public MemberDefinition GetOrAddMember(string memberName)
    {
        if (!Members.TryGetValue(memberName, out MemberDefinition? member))
        {
            member = new MemberDefinition();
            Members[memberName] = member;
        }

        return member;
    }

    public override string ToString() => $"{SourceType.Name} -> {DestinationType.Name}";
}

internal sealed class MemberDefinition
{
    public bool Ignored { get; set; }

    /// <summary><c>Func&lt;TSource, TSourceMember&gt;</c> ifadesi.</summary>
    public LambdaExpression? SourceExpression { get; set; }
}
