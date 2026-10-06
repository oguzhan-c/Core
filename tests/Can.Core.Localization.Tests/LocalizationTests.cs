using System.Globalization;
using Can.Core.Domain.Results;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace Can.Core.Localization.Tests;

public class LocalizationTests
{
    private static ServiceProvider Build(Action<CanLocalizationOptions>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddCanLocalization(o =>
        {
            o.ResourcesPath = null;
            o.ResourceAssemblies.Add(typeof(LocalizationTests).Assembly);
            configure?.Invoke(o);
        });
        return services.BuildServiceProvider();
    }

    private static T InCulture<T>(string culture, Func<T> action)
    {
        CultureInfo previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        try
        {
            return action();
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    [Fact]
    public void Reads_nested_keys_for_current_culture_with_fallback_chain()
    {
        using ServiceProvider provider = Build();
        var localizer = provider.GetRequiredService<IStringLocalizer>();

        Assert.Equal("Ürün bulunamadı.", InCulture("tr-TR", () => localizer["product.not_found"].Value));
        Assert.Equal("Product not found.", InCulture("en-GB", () => localizer["product.not_found"].Value));

        // en'de olmayan anahtar varsayılan kültüre (tr) düşer
        Assert.Equal("Yalnızca Türkçe", InCulture("en", () => localizer["only_tr"].Value));

        // hiç olmayan anahtar: kendisi + ResourceNotFound
        LocalizedString missing = InCulture("en", () => localizer["yok.boyle"]);
        Assert.True(missing.ResourceNotFound);
        Assert.Equal("yok.boyle", missing.Value);
    }

    [Fact]
    public void Generic_localizer_and_format_arguments()
    {
        using ServiceProvider provider = Build();
        var localizer = provider.GetRequiredService<IStringLocalizer<LocalizationTests>>();

        Assert.Equal("Hello Ada", InCulture("en", () => localizer["greeting", "Ada"].Value));
    }

    [Fact]
    public void Error_is_described_by_code_with_metadata_placeholders()
    {
        using ServiceProvider provider = Build();
        var localizer = provider.GetRequiredService<IStringLocalizer>();

        Error error = Error.Failure("product.out_of_stock", "Stok yetersiz.").WithMetadata("name", "Chai").WithMetadata("available", 3);
        Error unknown = Error.Failure("order.unknown", "Bilinmeyen sipariş hatası.");

        Assert.Equal("Only 3 left of Chai.", InCulture("en", () => localizer.Describe(error)));
        Assert.Equal("Chai ürününden yalnızca 3 adet kaldı.", InCulture("tr", () => localizer.Describe(error)));
        Assert.Equal("Bilinmeyen sipariş hatası.", InCulture("en", () => localizer.Describe(unknown)));
    }

    [Fact]
    public void Application_folder_overrides_embedded_texts()
    {
        string directory = Path.Combine(Path.GetTempPath(), "can-loc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "en.json"), """{ "product": { "not_found": "No such product." } }""");

        try
        {
            using ServiceProvider provider = Build(o => o.ResourcesPath = directory);
            var localizer = provider.GetRequiredService<IStringLocalizer>();

            Assert.Equal("No such product.", InCulture("en", () => localizer["product.not_found"].Value));
            Assert.Equal("Hello {0}", InCulture("en", () => localizer["greeting"].Value));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("tr.json", "tr")]
    [InlineData("errors.en-US.json", "en-US")]
    [InlineData("Can.Core.WebApi.Localization.core.en.json", "en")]
    [InlineData("errors.json", null)]
    public void Culture_is_taken_from_file_name(string file, string? culture)
    {
        Assert.Equal(culture, JsonLocalizationStore.CultureOf(file));
    }

    [Fact]
    public void Named_format_leaves_unknown_placeholders()
    {
        Assert.Equal("a 1 {b}", LocalizerExtensions.FormatNamed("a {x} {b}", new Dictionary<string, object?> { ["x"] = 1 }));
    }
}
