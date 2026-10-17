namespace Can.Core.Generator;

/// <summary>
/// Can.Core şablonuyla oluşturulmuş çözümün katmanları: <c>{Kök}.Domain</c>, <c>{Kök}.Application</c>,
/// <c>{Kök}.Persistence</c>, <c>{Kök}.WebAPI</c> (nArchitecture düzeni). Proje adları kök ad alanını da verir.
/// </summary>
public sealed record SolutionLayout(string Root, string RootNamespace, string Domain, string Application, string Persistence, string WebApi)
{
    private static readonly string[] IgnoredDirectories = ["bin", "obj", ".git", ".idea", ".vs", "node_modules"];

    /// <summary>
    /// <paramref name="startPath"/>'ten (dosya ya da klasör) yukarı doğru çözüm dosyasını (.slnx/.sln) arar ve katman
    /// projelerini bulur.
    /// </summary>
    /// <exception cref="GeneratorException">Çözüm ya da katmanlardan biri bulunamazsa.</exception>
    public static SolutionLayout Find(string startPath)
    {
        string full = Path.GetFullPath(startPath);
        string? directory = File.Exists(full) ? Path.GetDirectoryName(full) : full;

        while (directory is not null)
        {
            if (Directory.EnumerateFiles(directory, "*.slnx").Any() || Directory.EnumerateFiles(directory, "*.sln").Any())
                return FromRoot(directory);
            directory = Path.GetDirectoryName(directory);
        }

        throw new GeneratorException($"'{startPath}' ya da üst klasörlerinde çözüm dosyası (.slnx / .sln) bulunamadı.");
    }

    public static SolutionLayout FromRoot(string root)
    {
        List<string> projects = EnumerateFiles(root, "*.csproj").ToList();

        string domain = Single(projects, ".Domain", root);
        string rootNamespace = Path.GetFileNameWithoutExtension(domain)[..^".Domain".Length];

        string Layer(string suffix) =>
            projects.FirstOrDefault(p => string.Equals(Path.GetFileNameWithoutExtension(p), rootNamespace + suffix, StringComparison.OrdinalIgnoreCase)) is { } path
                ? Path.GetDirectoryName(path)!
                : throw new GeneratorException($"'{rootNamespace}{suffix}' projesi bulunamadı ({root}).");

        return new SolutionLayout(root, rootNamespace, Path.GetDirectoryName(domain)!, Layer(".Application"), Layer(".Persistence"), Layer(".WebAPI"));
    }

    /// <summary>Klasördeki .cs dosyaları (bin/obj hariç).</summary>
    public static IEnumerable<string> SourceFiles(string directory) => EnumerateFiles(directory, "*.cs");

    private static string Single(List<string> projects, string suffix, string root)
    {
        List<string> matches = projects.Where(p => Path.GetFileNameWithoutExtension(p).EndsWith(suffix, StringComparison.Ordinal)).ToList();
        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new GeneratorException($"'*{suffix}' projesi bulunamadı ({root}). Çözüm Can.Core şablonuyla mı oluşturuldu?"),
            _ => throw new GeneratorException($"Birden fazla '*{suffix}' projesi var: {string.Join(", ", matches.Select(Path.GetFileName))}."),
        };
    }

    private static IEnumerable<string> EnumerateFiles(string directory, string pattern)
    {
        foreach (string file in Directory.EnumerateFiles(directory, pattern))
            yield return file;

        foreach (string child in Directory.EnumerateDirectories(directory))
        {
            if (IgnoredDirectories.Contains(Path.GetFileName(child), StringComparer.OrdinalIgnoreCase))
                continue;
            foreach (string file in EnumerateFiles(child, pattern))
                yield return file;
        }
    }
}

/// <summary>Kullanıcıya gösterilecek üretim hatası (yanlış klasör, entity yok ...).</summary>
public sealed class GeneratorException : Exception
{
    public GeneratorException(string message)
        : base(message) { }

    public GeneratorException() { }

    public GeneratorException(string message, Exception innerException)
        : base(message, innerException) { }
}
