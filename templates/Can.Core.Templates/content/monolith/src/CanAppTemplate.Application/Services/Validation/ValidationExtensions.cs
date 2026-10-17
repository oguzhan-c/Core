using Can.Core.Application.Requests;
using FluentValidation;

namespace CanAppTemplate.Application.Services.Validation;

/// <summary>Validator'larda tekrar eden kurallar.</summary>
public static class ValidationExtensions
{
    public static IRuleBuilderOptions<T, PageRequest> ValidPage<T>(this IRuleBuilder<T, PageRequest> rule) =>
        rule.NotNull()
            .Must(p => p.Page >= 0 && p.PageSize is > 0 and <= PageRequest.MaxPageSize)
            .WithMessage($"Sayfa numarası 0 ya da büyük, sayfa boyutu 1-{PageRequest.MaxPageSize} arası olmalı.");

    public static IRuleBuilderOptions<T, string> StrongPassword<T>(this IRuleBuilder<T, string> rule) =>
        rule.NotEmpty()
            .MinimumLength(8)
            .MaximumLength(128)
            .Matches("[A-Za-zÇĞİÖŞÜçğıöşü]").WithMessage("Şifre en az bir harf içermeli.")
            .Matches("[0-9]").WithMessage("Şifre en az bir rakam içermeli.");

    public static IRuleBuilderOptions<T, string> SixDigitCode<T>(this IRuleBuilder<T, string> rule) =>
        rule.NotEmpty().Matches("^[0-9]{6}$").WithMessage("Kod 6 haneli olmalı.");
}
