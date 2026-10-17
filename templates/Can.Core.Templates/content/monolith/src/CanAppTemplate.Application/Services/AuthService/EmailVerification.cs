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

namespace CanAppTemplate.Application.Services.AuthService;

public enum VerificationPurpose
{
    ConfirmEmail = 0,
    SignIn = 1,
    ResetPassword = 2,
}

/// <summary>
/// E-postaya gönderilen 6 haneli kodlar (doğrulama, iki adımlı giriş, şifre sıfırlama). Kod HMAC'lenmiş olarak
/// <see cref="EmailAuthenticator{TId}"/>'da saklanır, 10 dakika geçerlidir ve en fazla 5 kez denenebilir.
/// </summary>
public sealed class EmailVerification
{
    private readonly IRepository<EmailAuthenticator<Guid>, Guid> _authenticators;
    private readonly IVerificationCodeService _codes;
    private readonly IEmailSender _emailSender;
    private readonly TimeProvider _timeProvider;

    public EmailVerification(
        IRepository<EmailAuthenticator<Guid>, Guid> authenticators,
        IVerificationCodeService codes,
        IEmailSender emailSender,
        TimeProvider timeProvider)
    {
        _authenticators = authenticators;
        _codes = codes;
        _emailSender = emailSender;
        _timeProvider = timeProvider;
    }

    public async Task<DateTimeOffset> SendCodeAsync(AppUser user, VerificationPurpose purpose, CancellationToken cancellationToken)
    {
        EmailAuthenticator<Guid> authenticator = await GetOrCreateAsync(user, cancellationToken);
        VerificationCode code = _codes.Generate();
        authenticator.SetCode(code);

        (string subject, string reason) = purpose switch
        {
            VerificationPurpose.SignIn => ($"Giriş kodun: {code.Code}", $"{AppInfo.Name} hesabına giriş yapmak için"),
            VerificationPurpose.ResetPassword => ($"Şifre sıfırlama kodun: {code.Code}", "şifreni sıfırlamak için"),
            _ => ($"Doğrulama kodun: {code.Code}", $"{AppInfo.Name} hesabını doğrulamak için"),
        };

        string warning = purpose == VerificationPurpose.ConfirmEmail
            ? "Bu isteği sen yapmadıysan bu e-postayı yok sayabilirsin."
            : "Bu isteği sen yapmadıysan şifreni değiştirmeni öneririz.";

        var message = new EmailMessage(subject)
        {
            TextBody = $"Merhaba {user.FirstName},\n\n{reason} kodun: {code.Code}\n\nKod 10 dakika geçerlidir. {warning}",
            HtmlBody =
                $"<p>Merhaba {System.Net.WebUtility.HtmlEncode(user.FirstName)},</p>"
                + $"<p>{System.Net.WebUtility.HtmlEncode(reason)} kodun:</p>"
                + $"<p style=\"font-size:28px;font-weight:bold;letter-spacing:6px\">{code.Code}</p>"
                + $"<p>Kod 10 dakika geçerlidir. {System.Net.WebUtility.HtmlEncode(warning)}</p>",
        };
        message.To.Add(new EmailAddress(user.Email, user.FullName));
        await _emailSender.SendAsync(message, cancellationToken);

        return code.ExpiresAt;
    }

    public async Task<bool> VerifyAsync(AppUser user, string code, CancellationToken cancellationToken)
    {
        EmailAuthenticator<Guid>? authenticator = await _authenticators.GetAsync(a => a.UserId == user.Id, cancellationToken: cancellationToken);
        if (authenticator is null || !authenticator.CanAttempt(_timeProvider.GetUtcNow()))
            return false;

        if (!_codes.Verify(code, authenticator.CodeHash!))
        {
            authenticator.RegisterFailedAttempt();
            return false;
        }

        authenticator.MarkVerified();
        return true;
    }

    private async Task<EmailAuthenticator<Guid>> GetOrCreateAsync(AppUser user, CancellationToken cancellationToken)
    {
        EmailAuthenticator<Guid>? existing = await _authenticators.GetAsync(a => a.UserId == user.Id, cancellationToken: cancellationToken);
        if (existing is not null)
            return existing;

        var created = new EmailAuthenticator<Guid>(user.Id);
        await _authenticators.AddAsync(created, cancellationToken);
        return created;
    }
}
