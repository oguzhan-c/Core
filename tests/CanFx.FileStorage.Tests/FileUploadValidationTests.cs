using System.Text;
using Microsoft.Extensions.DependencyInjection;

namespace CanFx.FileStorage.Tests;

public sealed class FileUploadValidationTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13];
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0, 16, 0x4A, 0x46];
    private static readonly byte[] Exe = [0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00];

    private static readonly FileUploadValidator Validator = new();

    private static async Task<FileValidationResult> CheckAsync(string name, byte[] content, FileUploadPolicy? policy = null)
    {
        using var stream = new MemoryStream(content);
        return await Validator.ValidateAsync(name, stream, policy, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Accepts_files_whose_content_matches_the_extension()
    {
        Assert.True((await CheckAsync("photo.PNG", Png)).IsValid);
        Assert.True((await CheckAsync("C:\\Users\\ali\\photo.jpeg", Jpeg)).IsValid);              // klasör kısmı atılır
        Assert.True((await CheckAsync("report.pdf", Encoding.ASCII.GetBytes("%PDF-1.7\n"))).IsValid);
        Assert.True((await CheckAsync("notes.txt", Encoding.UTF8.GetBytes("MZ ile başlayan düz metin"))).IsValid);
        Assert.True((await CheckAsync("sheet.xlsx", [0x50, 0x4B, 0x03, 0x04, 20, 0])).IsValid);
    }

    [Fact]
    public async Task Rejects_by_extension_size_and_name()
    {
        Assert.Equal("file_type_not_allowed", (await CheckAsync("archive.zip", [0x50, 0x4B, 0x03, 0x04])).Code);
        Assert.Equal("file_type_not_allowed", (await CheckAsync("noextension", Png)).Code);
        Assert.Equal("file_type_blocked", (await CheckAsync("run.exe", Exe)).Code);
        Assert.Equal("file_type_blocked", (await CheckAsync("invoice.php.jpg", Jpeg)).Code);          // ara uzantı
        Assert.Equal("file_type_blocked", (await CheckAsync("logo.svg", Encoding.UTF8.GetBytes("<svg/>"))).Code);
        Assert.Equal("file_empty", (await CheckAsync("photo.png", [])).Code);
        Assert.Equal("file_name_invalid", (await CheckAsync("  ", Png)).Code);
        Assert.Equal("file_name_invalid", (await CheckAsync("a\0b.png", Png)).Code);

        var small = new FileUploadPolicy { MaxSizeBytes = 4 };
        FileValidationResult large = await CheckAsync("photo.png", Png, small);
        Assert.Equal("file_too_large", large.Code);
        Assert.Contains("KB", large.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rejects_content_that_does_not_match_the_extension()
    {
        Assert.Equal("file_content_mismatch", (await CheckAsync("photo.png", Jpeg)).Code);
        Assert.Equal("file_content_executable", (await CheckAsync("photo.png", Exe)).Code);             // adı değiştirilmiş exe
        Assert.Equal("file_content_executable", (await CheckAsync("notes.txt", Encoding.ASCII.GetBytes("#!/bin/sh\nrm -rf /"))).Code);
        Assert.Equal("file_content_mismatch", (await CheckAsync("data.csv", [1, 2, 0, 4])).Code);       // metin değil
        Assert.Equal("file_content_mismatch", (await CheckAsync("image.webp", Png)).Code);

        byte[] webp = [.. "RIFF"u8, 1, 2, 3, 4, .. "WEBP"u8, .. "VP8 "u8];
        Assert.True((await CheckAsync("image.webp", webp)).IsValid);

        // İçerik denetimi kapatılabilir; uzantı kuralları yine geçerli.
        Assert.True((await CheckAsync("photo.png", Jpeg, new FileUploadPolicy { VerifyContent = false })).IsValid);
    }

    [Fact]
    public async Task Stream_is_rewound_and_must_be_seekable()
    {
        using var stream = new MemoryStream(Png) { Position = 4 };
        Assert.True((await Validator.ValidateAsync("photo.png", stream, cancellationToken: TestContext.Current.CancellationToken)).IsValid);
        Assert.Equal(4, stream.Position);

        await using var deflate = new System.IO.Compression.DeflateStream(new MemoryStream(), System.IO.Compression.CompressionMode.Compress);
        await Assert.ThrowsAsync<ArgumentException>(async () => await Validator.ValidateAsync("photo.png", deflate, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Policy_from_settings_safe_names_and_registration()
    {
        FileUploadPolicy policy = FileUploadPolicy.Create(2, ["PDF", ".Png"]);
        Assert.Equal(2 * 1024 * 1024, policy.MaxSizeBytes);
        Assert.True(policy.AllowedExtensions.SetEquals([".pdf", ".png"]));

        Assert.Equal("teklif_2026__son_.pdf", FileUploadValidator.SafeFileName("../../teklif 2026 (son).pdf"));
        Assert.Equal("file", FileUploadValidator.SafeFileName("..."));
        Assert.Equal(20, FileUploadValidator.SafeFileName(new string('a', 200) + ".pdf", maxLength: 20).Length);
        Assert.EndsWith(".pdf", FileUploadValidator.SafeFileName(new string('a', 200) + ".pdf", maxLength: 20), StringComparison.Ordinal);

        var services = new ServiceCollection();
        services.AddCanLocalFileStorage();
        services.AddCanFileUploadValidation(p => p.AllowedExtensions = new HashSet<string> { ".pdf" });
        using ServiceProvider provider = services.BuildServiceProvider();
        Assert.IsType<FileUploadValidator>(provider.GetRequiredService<IFileUploadValidator>());

        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddCanFileUploadValidation(p => p.AllowedExtensions.Clear()));
    }
}
