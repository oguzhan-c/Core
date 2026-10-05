using FluentValidation;

namespace Northwind.Application.Common;

internal static class ValidationExtensions
{
    public static IRuleBuilderOptions<T, PageRequest> ValidPage<T>(this IRuleBuilder<T, PageRequest> rule) =>
        rule.NotNull()
            .Must(p => p.Index >= 0 && p.Size is > 0 and <= PageRequest.MaxSize)
            .WithMessage($"Sayfa numarası 0 ya da büyük, sayfa boyutu 1-{PageRequest.MaxSize} arası olmalı.");

    public const string AddressMessage = "Adres için sokak, şehir ve ülke zorunlu.";

    /// <summary>Boş olabilir; doluysa sokak, şehir ve ülke zorunlu.</summary>
    public static IRuleBuilderOptions<T, AddressDto?> ValidAddress<T>(this IRuleBuilder<T, AddressDto?> rule) =>
        rule.Must(a => a is null || IsComplete(a)).WithMessage(AddressMessage);

    public static bool IsComplete(AddressDto? address) =>
        address is not null
        && !string.IsNullOrWhiteSpace(address.Street)
        && !string.IsNullOrWhiteSpace(address.City)
        && !string.IsNullOrWhiteSpace(address.Country);
}
