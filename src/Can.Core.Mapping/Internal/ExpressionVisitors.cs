using System.Linq.Expressions;

namespace Can.Core.Mapping.Internal;

/// <summary>Bir lambda parametresini başka bir ifadeyle değiştirir.</summary>
internal sealed class ParameterReplacer : ExpressionVisitor
{
    private readonly ParameterExpression _parameter;
    private readonly Expression _replacement;

    private ParameterReplacer(ParameterExpression parameter, Expression replacement)
    {
        _parameter = parameter;
        _replacement = replacement;
    }

    public static Expression Replace(Expression expression, ParameterExpression parameter, Expression replacement) =>
        new ParameterReplacer(parameter, replacement).Visit(expression);

    protected override Expression VisitParameter(ParameterExpression node) =>
        node == _parameter ? _replacement : base.VisitParameter(node);
}

/// <summary>
/// Bellekte yapılan eşlemelerde <c>s.Customer.Name</c> gibi zincirleri
/// <c>s.Customer == null ? default : s.Customer.Name</c> şekline çevirir; böylece
/// <see cref="NullReferenceException"/> yerine default değer gelir.
/// (EF Core projection'larında kullanılmaz; veritabanı null'ları zaten kendisi yönetir.)
/// </summary>
internal sealed class NullSafeVisitor : ExpressionVisitor
{
    private readonly ParameterExpression _root;

    private NullSafeVisitor(ParameterExpression root) => _root = root;

    public static Expression Apply(Expression expression, ParameterExpression root) =>
        new NullSafeVisitor(root).Visit(expression);

    protected override Expression VisitMember(MemberExpression node)
    {
        Expression? inner = Visit(node.Expression);
        if (inner is null)
            return node; // static üye

        Expression updated = node.Update(inner);
        return NeedsGuard(inner) ? Guard(inner, updated) : updated;
    }

    protected override Expression VisitMethodCall(MethodCallExpression node)
    {
        Expression? instance = Visit(node.Object);
        IEnumerable<Expression> arguments = Visit(node.Arguments);
        Expression updated = node.Update(instance, arguments);

        if (instance is null || node.Type == typeof(void) || !NeedsGuard(instance))
            return updated;

        return Guard(instance, updated);
    }

    // Kök parametre (kaynak nesnenin kendisi) en üstte zaten kontrol edilir.
    // Nullable<T> üyelerine (HasValue, Value) dokunulmaz; yalnızca referans tipler korunur.
    private bool NeedsGuard(Expression instance) => instance != _root && !instance.Type.IsValueType;

    private static ConditionalExpression Guard(Expression instance, Expression access) =>
        Expression.Condition(
            Expression.Equal(instance, Expression.Constant(null, instance.Type)),
            Expression.Default(access.Type),
            access
        );
}
