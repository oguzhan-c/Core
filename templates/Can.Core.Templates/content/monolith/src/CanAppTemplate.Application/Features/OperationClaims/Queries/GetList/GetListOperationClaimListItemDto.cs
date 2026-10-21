namespace CanAppTemplate.Application.Features.OperationClaims.Queries.GetList;

public sealed record GetListOperationClaimListItemDto(IdentityKey Id, string Name, string? Description, bool IsDeprecated);
