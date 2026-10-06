using Can.Core.Domain.Results;
using Can.Core.Application;
using Can.Core.BackgroundJobs;
using Can.Core.Mediator;
using Can.Core.Persistence.Paging;
using Can.Core.Persistence.Repositories;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Northwind.Application.Common;
using Northwind.Application.Features.Products;
using Northwind.Domain.Identity;
using AppRoles = Northwind.Domain.Identity.Roles;

namespace Northwind.Application.Features.Admin;

// ---------------------------------------------------------------- kullanıcılar

public sealed record UserListItemDto(
    Guid Id,
    string Email,
    string FirstName,
    string LastName,
    bool EmailConfirmed,
    DateTimeOffset? LockoutEnd,
    DateTimeOffset CreatedAt,
    IReadOnlyList<string> Roles);

/// <summary>Mağazanın kullanıcıları (personel ve siteden kayıt olan müşteriler).</summary>
public sealed record GetUsersQuery(PageRequest Page, string? Search = null, string? Role = null) : IRequest<Result<IPaginate<UserListItemDto>>>, ISecuredRequest
{
    public IReadOnlyCollection<string> Roles => [AppRoles.Admin];
}

public sealed class GetUsersQueryValidator : AbstractValidator<GetUsersQuery>
{
    public GetUsersQueryValidator()
    {
        RuleFor(q => q.Page).ValidPage();
        RuleFor(q => q.Search).MaximumLength(100);
    }
}

public sealed class GetUsersQueryHandler : IRequestHandler<GetUsersQuery, Result<IPaginate<UserListItemDto>>>
{
    private readonly IRepository<AppUser, Guid> _users;

    public GetUsersQueryHandler(IRepository<AppUser, Guid> users) => _users = users;

    public async Task<Result<IPaginate<UserListItemDto>>> Handle(GetUsersQuery request, CancellationToken cancellationToken)
    {
        IQueryable<AppUser> query = _users.Query(enableTracking: false);

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            string pattern = $"%{request.Search.Trim().ToUpperInvariant()}%";
            query = query.Where(u =>
                EF.Functions.Like(u.NormalizedEmail, pattern)
                || EF.Functions.Like(u.FirstName.ToUpper(), pattern)
                || EF.Functions.Like(u.LastName.ToUpper(), pattern)
            );
        }

        if (!string.IsNullOrWhiteSpace(request.Role))
            query = query.Where(u => u.UserRoles.Any(r => r.Role!.Name == request.Role));

        return Result.Ok(await query
            .OrderBy(u => u.Email)
            .Select(u => new UserListItemDto(
                u.Id,
                u.Email,
                u.FirstName,
                u.LastName,
                u.EmailConfirmed,
                u.LockoutEnd,
                u.CreatedAt,
                u.UserRoles.Select(r => r.Role!.Name).OrderBy(n => n).ToList()
            ))
            .ToPaginateAsync(request.Page.Index, request.Page.Size, cancellationToken: cancellationToken));
    }
}

// ---------------------------------------------------------------- arka plan işleri

/// <summary>"Yeniden sipariş" raporunu beklemeden kuyruğa atar; iş arka planda bu mağaza adına çalışır.</summary>
public sealed record RunReorderReportCommand : IRequest<Result<Success>>, ISecuredRequest
{
    public IReadOnlyCollection<string> Roles => [AppRoles.Admin];
}

public sealed class RunReorderReportCommandHandler : IRequestHandler<RunReorderReportCommand, Result<Success>>
{
    private readonly IBackgroundJobQueue _queue;

    public RunReorderReportCommandHandler(IBackgroundJobQueue queue) => _queue = queue;

    public async Task<Result<Success>> Handle(RunReorderReportCommand request, CancellationToken cancellationToken)
    {
        await _queue.EnqueueAsync<ReorderReportJob>(cancellationToken);
        return Result.Success;
    }
}
