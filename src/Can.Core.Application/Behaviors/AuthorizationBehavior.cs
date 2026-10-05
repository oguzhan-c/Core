using Can.Core.Application.Exceptions;
using Can.Core.Mediator;

namespace Can.Core.Application.Behaviors;

/// <summary>
/// <see cref="ISecuredRequest"/> istekleri için: giriş yapılmamışsa <see cref="UnauthorizedException"/>,
/// gerekli rollerden hiçbiri yoksa <see cref="ForbiddenException"/>.
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
            throw new UnauthorizedException();

        IReadOnlyCollection<string> requiredRoles = request.Roles;

        bool allowed =
            requiredRoles.Count == 0
            || (_options.AdminRole is { Length: > 0 } adminRole && _currentUser.IsInRole(adminRole))
            || requiredRoles.Any(_currentUser.IsInRole);

        if (!allowed)
            throw new ForbiddenException();

        return next();
    }
}
