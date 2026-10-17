using FluentValidation;

namespace CanAppTemplate.Application.Common;

/// <summary>Sayfa numaralı istek: <see cref="Index"/> 0'dan başlar.</summary>
public sealed record PageRequest(int Index = 0, int Size = 20)
{
    public const int MaxSize = 100;
}

internal static class ValidationExtensions
{
    public static IRuleBuilderOptions<T, PageRequest> ValidPage<T>(this IRuleBuilder<T, PageRequest> rule) =>
        rule.NotNull()
            .Must(p => p.Index >= 0 && p.Size is > 0 and <= PageRequest.MaxSize)
            .WithMessage($"Sayfa numarası 0 ya da büyük, sayfa boyutu 1-{PageRequest.MaxSize} arası olmalı.");

    public static IRuleBuilderOptions<T, string> StrongPassword<T>(this IRuleBuilder<T, string> rule) =>
        rule.NotEmpty()
            .MinimumLength(8)
            .MaximumLength(128)
            .Matches("[A-Za-zÇĞİÖŞÜçğıöşü]").WithMessage("Şifre en az bir harf içermeli.")
            .Matches("[0-9]").WithMessage("Şifre en az bir rakam içermeli.");

    public static IRuleBuilderOptions<T, string> SixDigitCode<T>(this IRuleBuilder<T, string> rule) =>
        rule.NotEmpty().Matches("^[0-9]{6}$").WithMessage("Kod 6 haneli olmalı.");
}
