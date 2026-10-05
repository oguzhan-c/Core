using System.Reflection;
using System.Runtime.CompilerServices;

namespace Can.Core.Mapping.Internal;

internal static class ReflectionHelper
{
    private const BindingFlags PublicInstance = BindingFlags.Public | BindingFlags.Instance;

    /// <summary>Hedefte değer atanabilen public property'ler (aynı isimde gizlenmiş üyelerde en türetilmiş olan).</summary>
    public static IEnumerable<PropertyInfo> GetWritableProperties(Type type, bool includeInitOnly) =>
        type.GetProperties(PublicInstance)
            .Where(p =>
                p.SetMethod is { IsPublic: true }
                && p.GetIndexParameters().Length == 0
                && (includeInitOnly || !IsInitOnly(p))
            )
            .GroupBy(p => p.Name, StringComparer.Ordinal)
            .Select(g => g.OrderByDescending(p => InheritanceDepth(p.DeclaringType)).First());

    /// <summary>Kaynakta okunabilen public property ve field'lar.</summary>
    public static IEnumerable<MemberInfo> GetReadableMembers(Type type)
    {
        IEnumerable<MemberInfo> properties = type.GetProperties(PublicInstance)
            .Where(p => p.GetMethod is { IsPublic: true } && p.GetIndexParameters().Length == 0);

        IEnumerable<MemberInfo> fields = type.GetFields(PublicInstance);

        return properties.Concat(fields);
    }

    /// <summary>Önce birebir isim, yoksa büyük/küçük harf duyarsız eşleşme.</summary>
    public static MemberInfo? FindReadableMember(Type type, string name)
    {
        MemberInfo[] members = GetReadableMembers(type).ToArray();

        return members.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.Ordinal))
            ?? members.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    public static Type GetMemberType(MemberInfo member) =>
        member switch
        {
            PropertyInfo property => property.PropertyType,
            FieldInfo field => field.FieldType,
            _ => throw new NotSupportedException(member.GetType().Name),
        };

    public static bool IsInitOnly(PropertyInfo property) =>
        property.SetMethod is { } setter
        && Array.IndexOf(setter.ReturnParameter.GetRequiredCustomModifiers(), typeof(IsExternalInit)) >= 0;

    public static bool CanBeNull(Type type) => !type.IsValueType || Nullable.GetUnderlyingType(type) is not null;

    /// <summary>Koleksiyonun eleman tipi; koleksiyon değilse (string dahil) <see langword="null"/>.</summary>
    public static Type? GetElementType(Type type)
    {
        if (type == typeof(string))
            return null;

        if (type.IsArray)
            return type.GetElementType();

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            return type.GetGenericArguments()[0];

        return type.GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            ?.GetGenericArguments()[0];
    }

    /// <summary>Sayısal tipler ve enum'lar (Expression.Convert ile birbirine dönüştürülebilenler).</summary>
    public static bool IsNumericOrEnum(Type type)
    {
        if (type.IsEnum)
            return true;

        return Type.GetTypeCode(type) switch
        {
            TypeCode.Byte
            or TypeCode.SByte
            or TypeCode.Int16
            or TypeCode.UInt16
            or TypeCode.Int32
            or TypeCode.UInt32
            or TypeCode.Int64
            or TypeCode.UInt64
            or TypeCode.Single
            or TypeCode.Double
            or TypeCode.Decimal => true,
            _ => false,
        };
    }

    /// <summary>Flattening'de içine girilmeyecek "yaprak" tipler.</summary>
    public static bool IsLeafType(Type type)
    {
        Type t = Nullable.GetUnderlyingType(type) ?? type;
        return t.IsPrimitive
            || t.IsEnum
            || t == typeof(string)
            || t == typeof(decimal)
            || t == typeof(DateTime)
            || t == typeof(DateTimeOffset)
            || t == typeof(DateOnly)
            || t == typeof(TimeOnly)
            || t == typeof(TimeSpan)
            || t == typeof(Guid);
    }

    private static int InheritanceDepth(Type? type)
    {
        int depth = 0;
        for (Type? t = type; t is not null; t = t.BaseType)
            depth++;
        return depth;
    }
}
