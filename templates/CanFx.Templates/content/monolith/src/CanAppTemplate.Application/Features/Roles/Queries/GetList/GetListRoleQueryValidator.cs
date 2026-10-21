using FluentValidation;
using CanAppTemplate.Application.Services.Validation;

namespace CanAppTemplate.Application.Features.Roles.Queries.GetList;

public sealed class GetListRoleQueryValidator : AbstractValidator<GetListRoleQuery>
{
    public GetListRoleQueryValidator() => RuleFor(x => x.PageRequest).ValidPage();
}
