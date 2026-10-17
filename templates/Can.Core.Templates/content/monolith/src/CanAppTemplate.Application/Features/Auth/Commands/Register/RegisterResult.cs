using Can.Core.Application;
using Can.Core.Domain.Results;
using Can.Core.Mailing;
using Can.Core.Mediator;
using Can.Core.Persistence.Repositories;
using Can.Core.Security.Entities;
using Can.Core.Security.Events;
using Can.Core.Security.Hashing;
using Can.Core.Security.Sessions;
using Can.Core.Security.VerificationCodes;
using FluentValidation;
using CanAppTemplate.Domain.Constants;
using CanAppTemplate.Domain.Entities;

namespace CanAppTemplate.Application.Features.Auth.Commands.Register;

public sealed record RegisterResult(string Email, DateTimeOffset CodeExpiresAt);
