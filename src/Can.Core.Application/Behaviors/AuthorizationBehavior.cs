using Can.Core.Application.Exceptions;
using Can.Core.Domain.Results;
using Can.Core.Mediator;

namespace Can.Core.Application.Behaviors;

/// <summary>
/// <see cref="ISecuredRequest"/> istekleri için: giriş yapılmamışsa <see cref="UnauthorizedException"/>,
/// gerekli rollerden ve yetkilerden hiçbiri yoksa <see cref="ForbiddenException"/> (Result dönen isteklerde
/// exception yerine <c>Error.Unauthorized</c> / <c>Error.Forbidden</c>).
/// </summary>
public sealed class AuthorizationBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : ISecuredRequest
{
    private readonly ICurrentUser _currentUser;
    private readonly CanApplicationOptions _options;

    public AuthorizationBehavior(ICurrentUser currentUser, CanApplicationOptions options)
    {
        _currentUser = currentUser;
        _options = options;
    }

    public Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        if (!_currentUser.IsAuthenticated)
        {
            return ResultTypes.TryCreateFailure([Error.Unauthorized()], out TResponse unauthorized)
                ? Task.FromResult(unauthorized)
                : throw new UnauthorizedException();
        }

        IReadOnlyCollection<string> requiredRoles = request.Roles;
        IReadOnlyCollection<string> requiredPermissions = request.Permissions;

        bool allowed =
            (requiredRoles.Count == 0 && requiredPermissions.Count == 0)
            || (_options.AdminRole is { Length: > 0 } adminRole && _currentUser.IsInRole(adminRole))
            || requiredRoles.Any(_currentUser.IsInRole)
            || requiredPermissions.Any(_currentUser.HasPermission);

        if (!allowed)
        {
            return ResultTypes.TryCreateFailure([Error.Forbidden()], out TResponse forbidden)
                ? Task.FromResult(forbidden)
                : throw new ForbiddenException();
        }

        return next();
    }
}
