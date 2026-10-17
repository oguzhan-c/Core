using Can.Core.Application;
using Can.Core.Domain.Results;
using Can.Core.Mediator;
using Can.Core.Persistence.Repositories;
using FluentValidation;
using CanAppTemplate.Application.Common;
using CanAppTemplate.Domain.Identity;

namespace CanAppTemplate.Application.Features.Auth;

/// <summary>Giriş yapmış kullanıcının profili (<c>GET /api/auth/me</c>).</summary>
public sealed record GetProfileQuery : IRequest<Result<UserProfileDto>>, ISecuredRequest;

public sealed class GetProfileQueryHandler : IRequestHandler<GetProfileQuery, Result<UserProfileDto>>
{
    private readonly ICurrentUser _currentUser;
    private readonly IRepository<AppUser, Guid> _users;

    public GetProfileQueryHandler(ICurrentUser currentUser, IRepository<AppUser, Guid> users)
    {
        _currentUser = currentUser;
        _users = users;
    }

    public async Task<Result<UserProfileDto>> Handle(GetProfileQuery request, CancellationToken cancellationToken)
    {
        Result<Guid> userId = _currentUser.UserId();
        if (userId.IsFailure)
            return userId.Errors;

        AppUser? user = await _users.GetByIdAsync(userId.Value, include: q => q.WithRolesAndPermissions(), enableTracking: false, cancellationToken: cancellationToken);
        if (user is null)
            return Error.Unauthorized();

        return UserProfileDto.From(user);
    }
}

/// <summary>Ad, soyad, telefon ve kampanya izni.</summary>
public sealed record UpdateProfileCommand(string FirstName, string LastName, string? PhoneNumber, bool MarketingConsent)
    : IRequest<Result<Success>>, ISecuredRequest, ITransactionalRequest;

public sealed class UpdateProfileCommandValidator : AbstractValidator<UpdateProfileCommand>
{
    public UpdateProfileCommandValidator()
    {
        RuleFor(c => c.FirstName).NotEmpty().MaximumLength(AppUser.NameMaxLength);
        RuleFor(c => c.LastName).NotEmpty().MaximumLength(AppUser.NameMaxLength);
        RuleFor(c => c.PhoneNumber).MaximumLength(AppUser.PhoneMaxLength);
    }
}

public sealed class UpdateProfileCommandHandler : IRequestHandler<UpdateProfileCommand, Result<Success>>
{
    private readonly ICurrentUser _currentUser;
    private readonly IRepository<AppUser, Guid> _users;

    public UpdateProfileCommandHandler(ICurrentUser currentUser, IRepository<AppUser, Guid> users)
    {
        _currentUser = currentUser;
        _users = users;
    }

    public Task<Result<Success>> Handle(UpdateProfileCommand request, CancellationToken cancellationToken) =>
        _currentUser
            .UserId()
            .ThenAsync(id => _users.GetByIdAsync(id, cancellationToken: cancellationToken).ToResult(Error.Unauthorized()))
            .Then(user => user.UpdateProfile(request.FirstName, request.LastName, request.PhoneNumber, request.MarketingConsent));
}
