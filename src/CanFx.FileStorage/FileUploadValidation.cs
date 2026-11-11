using System.Text;

namespace CanFx.FileStorage;

/// <summary>Yüklenen dosyalar için kurallar.</summary>
public sealed class FileUploadPolicy
{
    /// <summary>Hiçbir koşulda kabul edilmeyen uzantılar: çalıştırılabilir dosyalar ve tarayıcıda betik çalıştırabilenler.</summary>
    public static readonly IReadOnlyCollection<string> DangerousExtensions =
    [
        ".exe", ".dll", ".com", ".scr", ".msi", ".msp", ".bat", ".cmd", ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse", ".mjs",
        ".wsf", ".wsh", ".hta", ".cpl", ".jar", ".sh", ".bash", ".app", ".dmg", ".pkg", ".deb", ".rpm", ".apk", ".lnk", ".reg",
        ".php", ".phtml", ".asp", ".aspx", ".ashx", ".cshtml", ".jsp", ".cgi", ".py", ".rb", ".pl",
        ".html", ".htm", ".xhtml", ".svg", ".swf", ".config",
    ];

    /// <summary>En büyük dosya boyutu (bayt; varsayılan 10 MB).</summary>
    public long MaxSizeBytes { get; set; } = 10 * 1024 * 1024;

    /// <summary>
    /// Kabul edilen uzantılar (noktalı, küçük harf: <c>".pdf"</c>). Varsayılan: sık kullanılan resim ve belge türleri.
    /// </summary>
    public ISet<string> AllowedExtensions { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".pdf", ".txt", ".csv", ".docx", ".xlsx", ".pptx",
    };

    /// <summary>
    /// İzin listesinde olsa bile reddedilen uzantılar (varsayılan <see cref="DangerousExtensions"/>). Ad içindeki ara
    /// uzantılara da bakılır (<c>fatura.php.jpg</c>).
    /// </summary>
    public ISet<string> BlockedExtensions { get; set; } = new HashSet<string>(DangerousExtensions, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Dosyanın ilk baytları uzantısıyla uyuşmalı (<c>.png</c> gerçekten PNG mi) ve çalıştırılabilir dosya imzası
    /// taşımamalı. İstemcinin gönderdiği içerik türüne (Content-Type) güvenilmez.
    /// </summary>
    public bool VerifyContent { get; set; } = true;

    /// <summary>Megabayt cinsinden sınır ve uzantı listesiyle (ör. ayarlardan).</summary>
    public static FileUploadPolicy Create(int maxSizeMegabytes, IEnumerable<string> allowedExtensions)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxSizeMegabytes, 1);
        ArgumentNullException.ThrowIfNull(allowedExtensions);
        return new FileUploadPolicy
        {
            MaxSizeBytes = maxSizeMegabytes * 1024L * 1024L,
            AllowedExtensions = new HashSet<string>(allowedExtensions.Select(FileUploadValidator.NormalizeExtension), StringComparer.OrdinalIgnoreCase),
        };
    }
}

/// <summary>Doğrulama sonucu. <see cref="Code"/> istemcinin ayırt etmesi için sabittir.</summary>
/// <param name="IsValid">Dosya kabul edilebilir mi.</param>
/// <param name="Code">
/// <c>file_empty</c>, <c>file_too_large</c>, <c>file_name_invalid</c>, <c>file_type_not_allowed</c>,
/// <c>file_type_blocked</c>, <c>file_content_mismatch</c>, <c>file_content_executable</c>.
/// </param>
/// <param name="Message">İnsan okunur açıklama.</param>
public sealed record FileValidationResult(bool IsValid, string? Code = null, string? Message = null)
{
    public static readonly FileValidationResult Valid = new(true);

    public static FileValidationResult Invalid(string code, string message) => new(false, code, message);
}

/// <summary>
/// Yüklenen dosyayı depoya yazmadan önce denetler: boyut, uzantı (izin ve yasak listesi) ve içeriğin uzantıyla uyumu.
/// </summary>
/// <example>
/// <code>
/// await using Stream content = file.OpenReadStream();
/// FileValidationResult check = await validator.ValidateAsync(file.FileName, content, policy, ct);
/// if (!check.IsValid)
///     throw new ValidationException("file", check.Message!) { Code = check.Code };
///
/// string name = FileUploadValidator.SafeFileName(file.FileName);
/// await storage.SaveAsync($"products/{id}/{Guid.NewGuid():N}{Path.GetExtension(name)}", content, cancellationToken: ct);
/// </code>
/// </example>
public interface IFileUploadValidator
{
    /// <param name="fileName">İstemcinin gönderdiği dosya adı.</param>
    /// <param name="content">Dosya; konumlanabilir (seekable) olmalı. Denetimden sonra başa sarılır.</param>
    /// <param name="policy">Kurallar; verilmezse kayıtlı varsayılan.</param>
    /// <param name="cancellationToken">İptal.</param>
    ValueTask<FileValidationResult> ValidateAsync(string fileName, Stream content, FileUploadPolicy? policy = null, CancellationToken cancellationToken = default);
}

public sealed class FileUploadValidator : IFileUploadValidator
{
    private const int HeaderSize = 512;

    private static readonly byte[] Zip = [0x50, 0x4B, 0x03, 0x04];
    private static readonly byte[] EmptyZip = [0x50, 0x4B, 0x05, 0x06];
    private static readonly byte[] Ole = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    /// <summary>Uzantı → dosyanın başlaması gereken imzalardan biri. Listede olmayan uzantıda imza aranmaz.</summary>
    private static readonly Dictionary<string, byte[][]> Signatures = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = [[0xFF, 0xD8, 0xFF]],
        [".jpeg"] = [[0xFF, 0xD8, 0xFF]],
        [".png"] = [[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]],
        [".gif"] = [Encoding.ASCII.GetBytes("GIF87a"), Encoding.ASCII.GetBytes("GIF89a")],
        [".bmp"] = [Encoding.ASCII.GetBytes("BM")],
        [".pdf"] = [Encoding.ASCII.GetBytes("%PDF-")],
        [".zip"] = [Zip, EmptyZip],
        [".docx"] = [Zip],
        [".xlsx"] = [Zip],
        [".pptx"] = [Zip],
        [".doc"] = [Ole],
        [".xls"] = [Ole],
        [".ppt"] = [Ole],
        [".gz"] = [[0x1F, 0x8B]],
        [".mp3"] = [Encoding.ASCII.GetBytes("ID3"), [0xFF, 0xFB], [0xFF, 0xF3], [0xFF, 0xF2]],
    };

    /// <summary>İçeriği metin olması gereken uzantılar: ikili (NUL baytı içeren) içerik reddedilir.</summary>
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase) { ".txt", ".csv", ".json", ".xml", ".md", ".log", ".yml", ".yaml" };

    /// <summary>Uzantısı ne olursa olsun reddedilen çalıştırılabilir dosya imzaları.</summary>
    private static readonly byte[][] ExecutableSignatures =
    [
        [0x7F, 0x45, 0x4C, 0x46],                      // ELF
        [0xFE, 0xED, 0xFA, 0xCE], [0xFE, 0xED, 0xFA, 0xCF], [0xCE, 0xFA, 0xED, 0xFE], [0xCF, 0xFA, 0xED, 0xFE], // Mach-O
        Encoding.ASCII.GetBytes("#!/"),                // betik (shebang)
    ];

    private readonly FileUploadPolicy _defaultPolicy;

    public FileUploadValidator(FileUploadPolicy? defaultPolicy = null) => _defaultPolicy = defaultPolicy ?? new FileUploadPolicy();

    public async ValueTask<FileValidationResult> ValidateAsync(string fileName, Stream content, FileUploadPolicy? policy = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!content.CanSeek)
            throw new ArgumentException("Dosya akışı konumlanabilir (seekable) olmalı; değilse önce belleğe ya da geçici dosyaya al.", nameof(content));

        policy ??= _defaultPolicy;

        if (string.IsNullOrWhiteSpace(fileName) || fileName.Any(char.IsControl) || Path.GetFileName(fileName.Replace('\\', '/')).Trim().Length == 0)
            return FileValidationResult.Invalid("file_name_invalid", "Dosya adı geçersiz.");

        string name = Path.GetFileName(fileName.Replace('\\', '/')).Trim();
        string extension = Path.GetExtension(name).ToLowerInvariant();

        // Ara uzantılar dahil: "fatura.php.jpg" bazı sunucularda php olarak çalıştırılabilir.
        string[] parts = name.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Skip(1).Any(part => policy.BlockedExtensions.Contains("." + part.Trim())))
            return FileValidationResult.Invalid("file_type_blocked", "Bu dosya türü güvenlik nedeniyle kabul edilmiyor.");

        if (extension.Length == 0 || !policy.AllowedExtensions.Contains(extension))
        {
            return FileValidationResult.Invalid(
                "file_type_not_allowed",
                $"Bu dosya türü kabul edilmiyor. İzin verilenler: {string.Join(", ", policy.AllowedExtensions.Order(StringComparer.OrdinalIgnoreCase))}.");
        }

        if (content.Length == 0)
            return FileValidationResult.Invalid("file_empty", "Dosya boş.");

        if (content.Length > policy.MaxSizeBytes)
            return FileValidationResult.Invalid("file_too_large", $"Dosya çok büyük (en fazla {FormatSize(policy.MaxSizeBytes)}).");

        if (!policy.VerifyContent)
            return FileValidationResult.Valid;

        byte[] header = new byte[(int)Math.Min(HeaderSize, content.Length)];
        long position = content.Position;
        content.Position = 0;
        try
        {
            await content.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            content.Position = position;
        }

        return CheckContent(extension, header);
    }

    internal static FileValidationResult CheckContent(string extension, ReadOnlySpan<byte> header)
    {
        // Windows PE (exe, dll): "MZ" ile başlar ve ikilidir ("MZ" ile başlayan düz metin reddedilmesin diye NUL aranır).
        if (header.StartsWith("MZ"u8) && header.Contains((byte)0))
            return FileValidationResult.Invalid("file_content_executable", "Dosya çalıştırılabilir içerik taşıyor.");

        foreach (byte[] signature in ExecutableSignatures)
        {
            if (header.StartsWith(signature))
                return FileValidationResult.Invalid("file_content_executable", "Dosya çalıştırılabilir içerik taşıyor.");
        }

        if (extension.Equals(".webp", StringComparison.OrdinalIgnoreCase))
        {
            bool webp = header.Length >= 12 && header.StartsWith("RIFF"u8) && header.Slice(8, 4).SequenceEqual("WEBP"u8);
            return webp ? FileValidationResult.Valid : Mismatch(extension);
        }

        if (Signatures.TryGetValue(extension, out byte[][]? expected))
        {
            foreach (byte[] signature in expected)
            {
                if (header.StartsWith(signature))
                    return FileValidationResult.Valid;
            }

            return Mismatch(extension);
        }

        if (TextExtensions.Contains(extension) && header.Contains((byte)0))
            return Mismatch(extension);

        return FileValidationResult.Valid;
    }

    /// <summary>
    /// İstemcinin gönderdiği addan güvenli bir dosya adı: klasör kısmı atılır, harf / rakam / <c>. - _</c> dışındakiler
    /// <c>_</c> olur, uzunluk sınırlanır. Depoda benzersizlik için adı kendin üret (Guid), bunu yalnızca gösterimde ya da
    /// uzantıyı almak için kullan.
    /// </summary>
    public static string SafeFileName(string fileName, int maxLength = 100)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, 8);

        string name = Path.GetFileName(fileName.Replace('\\', '/')).Trim();
        var builder = new StringBuilder(name.Length);
        foreach (char c in name)
            builder.Append(char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_');

        string safe = builder.ToString().Trim('.', '_');
        if (safe.Length == 0)
            return "file";
        if (safe.Length <= maxLength)
            return safe;

        string extension = Path.GetExtension(safe);
        return extension.Length < maxLength ? safe[..(maxLength - extension.Length)] + extension : safe[..maxLength];
    }

    /// <summary><c>"PDF"</c>, <c>"pdf"</c>, <c>".Pdf"</c> → <c>".pdf"</c>.</summary>
    public static string NormalizeExtension(string extension)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extension);
        string trimmed = extension.Trim().ToLowerInvariant();
        return trimmed.StartsWith('.') ? trimmed : "." + trimmed;
    }

    private static FileValidationResult Mismatch(string extension) =>
        FileValidationResult.Invalid("file_content_mismatch", $"Dosyanın içeriği uzantısıyla ({extension}) uyuşmuyor.");

    private static string FormatSize(long bytes) =>
        bytes >= 1024 * 1024 ? $"{bytes / (1024d * 1024d):0.#} MB" : $"{Math.Max(1, bytes / 1024)} KB";
}
