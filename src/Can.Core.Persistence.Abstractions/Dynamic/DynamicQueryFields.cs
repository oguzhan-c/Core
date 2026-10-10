using System.Linq.Expressions;
using System.Text;

namespace Can.Core.Persistence.Dynamic;

/// <summary>
/// İstemciye açılan filtre/sıralama alanlarının izin listesi. İstemciden gelen <see cref="DynamicQuery"/> yalnızca
/// burada adı geçen alanları kullanabilir; diğerleri doğrulama hatasıdır. Takma ad ile iç yapı gizlenebilir
/// (<c>categoryName</c> → <c>Category.Name</c>).
/// </summary>
/// <typeparam name="T">Sorgulanan tip.</typeparam>
/// <example>
/// <code>
/// private static readonly DynamicQueryFields&lt;Product&gt; Fields = new DynamicQueryFields&lt;Product&gt;()
///     .Allow(p =&gt; p.Name, p =&gt; p.UnitPrice, p =&gt; p.UnitsInStock)
///     .Allow("categoryName", p =&gt; p.Category!.Name);
///
/// query = query.ToDynamic(request.Query, Fields);
/// </code>
/// </example>
public sealed class DynamicQueryFields<T>
{
    private readonly Dictionary<string, string> _fields = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// İzin verilen alan adları (istemcinin göndereceği adlar).
    /// </summary>
    public IReadOnlyCollection<string> Names => _fields.Keys;

    /// <summary>
    /// Alanlara izin verir; istemci onları property yolunun camelCase hâliyle kullanır (<c>p =&gt; p.Category.Name</c>
    /// → <c>category.name</c>).
    /// </summary>
    /// <param name="members">Property yolları.</param>
    /// <returns>Zincirleme için aynı liste.</returns>
    public DynamicQueryFields<T> Allow(params Expression<Func<T, object?>>[] members)
    {
        ArgumentNullException.ThrowIfNull(members);
        foreach (Expression<Func<T, object?>> member in members)
        {
            string path = PathOf(member);
            _fields[path] = path;
        }

        return this;
    }

    /// <summary>
    /// Bir property yolunu takma adla açar.
    /// </summary>
    /// <param name="field">İstemcinin kullanacağı ad (ör. <c>categoryName</c>).</param>
    /// <param name="member">Gerçek property yolu (ör. <c>p =&gt; p.Category.Name</c>).</param>
    /// <returns>Zincirleme için aynı liste.</returns>
    public DynamicQueryFields<T> Allow(string field, Expression<Func<T, object?>> member)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(field);
        _fields[field.Trim()] = PathOf(member);
        return this;
    }

    /// <summary>
    /// İstemcinin gönderdiği alan adını gerçek property yoluna çevirir.
    /// </summary>
    /// <param name="field">İstemcinin gönderdiği ad.</param>
    /// <param name="path">Gerçek property yolu.</param>
    /// <returns>Alana izin varsa <see langword="true"/>.</returns>
    public bool TryResolve(string field, out string path) => _fields.TryGetValue(field.Trim(), out path!);

    /// <summary>
    /// <c>p =&gt; p.Category.Name</c> → <c>category.name</c>.
    /// </summary>
    /// <param name="member">Property yolu ifadesi.</param>
    /// <returns>Noktalı, camelCase yol.</returns>
    /// <exception cref="ArgumentException">İfade bir property zinciri değilse.</exception>
    internal static string PathOf(Expression<Func<T, object?>> member)
    {
        ArgumentNullException.ThrowIfNull(member);
        Expression body = member.Body;
        while (body is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert)
            body = convert.Operand;

        var segments = new Stack<string>();
        while (body is MemberExpression access)
        {
            segments.Push(access.Member.Name);
            body = access.Expression!;
        }

        if (body is not ParameterExpression || segments.Count == 0)
            throw new ArgumentException($"'{member}' bir property yolu değil (ör. p => p.Category.Name).", nameof(member));

        var builder = new StringBuilder();
        foreach (string segment in segments)
        {
            if (builder.Length > 0)
                builder.Append('.');
            builder.Append(char.ToLowerInvariant(segment[0])).Append(segment.AsSpan(1));
        }

        return builder.ToString();
    }
}
