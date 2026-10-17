using System.Text;

namespace Can.Core.Generator;

/// <summary>Üretimin sonucu: yazılacak dosyalar ve var olan dosyalara yapılacak eklemeler. <see cref="Apply"/> diske yazar.</summary>
public sealed class GenerationPlan
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly List<GeneratedFile> _files = [];
    private readonly List<Injection> _injections = [];

    public IReadOnlyList<GeneratedFile> Files => _files;

    /// <summary>Var olan dosyalara yapılacak değişiklikler (dosya + açıklama).</summary>
    public IReadOnlyList<(string File, string Description)> Injections => _injections.Select(i => (i.File, i.Description)).ToList();

    public void AddFile(string path, string content) => _files.Add(new GeneratedFile(path, content.ReplaceLineEndings("\n")));

    /// <summary>
    /// <paramref name="file"/>'da <c>// {marker}</c> satırının ÜSTÜNE <paramref name="line"/>'ı (işaretin girintisiyle)
    /// ekler; gerekirse <paramref name="usings"/>'i dosyanın başına yazar. Satır zaten varsa bir şey yapılmaz.
    /// </summary>
    public void Inject(string file, string marker, string line, params string[] usings) =>
        _injections.Add(new Injection(file, line.Trim(), text => SourceEditor.Inject(text, marker, line, usings)));

    /// <summary>Dosyada özel bir değişiklik (ör. JSON'a anahtar ekleme).</summary>
    internal void Edit(string file, string description, Func<string, InjectResult> edit) =>
        _injections.Add(new Injection(file, description, edit));

    /// <summary>Planı uygular. <paramref name="force"/> yoksa var olan dosyaların üzerine yazılmaz.</summary>
    public IReadOnlyList<ChangeResult> Apply(bool force = false, bool dryRun = false)
    {
        var results = new List<ChangeResult>();

        foreach (GeneratedFile file in _files)
        {
            bool exists = File.Exists(file.Path);
            if (exists && !force)
            {
                results.Add(new ChangeResult(file.Path, ChangeKind.Skipped, "dosya var (üzerine yazmak için --force)"));
                continue;
            }

            if (!dryRun)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file.Path)!);
                File.WriteAllText(file.Path, file.Content, Utf8);
            }

            results.Add(new ChangeResult(file.Path, exists ? ChangeKind.Overwritten : ChangeKind.Created, null));
        }

        foreach (IGrouping<string, Injection> group in _injections.GroupBy(i => i.File))
        {
            if (!File.Exists(group.Key))
            {
                results.Add(new ChangeResult(group.Key, ChangeKind.Failed, "dosya bulunamadı"));
                continue;
            }

            string original = File.ReadAllText(group.Key);
            string text = original;
            var notes = new List<string>();

            foreach (Injection injection in group)
            {
                InjectResult result = injection.Apply(text);
                text = result.Text;
                if (result.Note is not null)
                    notes.Add(result.Note);
            }

            ChangeKind kind = notes.Count > 0 && text == original ? ChangeKind.Failed : text == original ? ChangeKind.Unchanged : ChangeKind.Updated;
            if (kind == ChangeKind.Updated && !dryRun)
                File.WriteAllText(group.Key, text, Utf8);

            results.Add(new ChangeResult(group.Key, kind, notes.Count > 0 ? string.Join("; ", notes) : null));
        }

        return results;
    }
}

public sealed record GeneratedFile(string Path, string Content);

internal sealed record Injection(string File, string Description, Func<string, InjectResult> Apply);

public enum ChangeKind
{
    Created,
    Overwritten,
    Updated,
    Unchanged,
    Skipped,
    Failed,
}

public sealed record ChangeResult(string Path, ChangeKind Kind, string? Note);

internal sealed record InjectResult(string Text, string? Note);

/// <summary>İşaretli satırlara (<c>// can-gen:...</c>) ekleme ve using ekleme.</summary>
internal static class SourceEditor
{
    /// <summary>
    /// JSON nesnesinin köküne <c>"{group}": { "{key}": "{value}" }</c> ekler (grup varsa dokunmaz). Yorumlu JSON'u bozmamak
    /// için metin olarak düzenlenir.
    /// </summary>
    public static InjectResult AddJsonGroup(string text, string group, string key, string value)
    {
        if (text.Contains($"\"{group}\"", StringComparison.Ordinal))
            return new InjectResult(text, null);

        int end = text.LastIndexOf('}');
        if (end < 0)
            return new InjectResult(text, "JSON nesnesi bulunamadı");

        int previous = end - 1;
        while (previous >= 0 && char.IsWhiteSpace(text[previous]))
            previous--;

        string separator = previous >= 0 && text[previous] != '{' ? "," : string.Empty;
        string entry = $"{separator}\n  \"{group}\": {{ \"{key}\": \"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\" }}\n";
        return new InjectResult(text[..(previous + 1)] + entry + text[end..], null);
    }

    public static InjectResult Inject(string text, string marker, string line, IReadOnlyList<string> usings)
    {
        string newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        List<string> lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();

        string markerText = "// " + marker;
        int index = lines.FindIndex(l => l.Trim() == markerText || l.TrimEnd().EndsWith(markerText, StringComparison.Ordinal) && !l.TrimStart().StartsWith("///", StringComparison.Ordinal) && l.Trim().StartsWith("//", StringComparison.Ordinal));
        if (index < 0)
            return new InjectResult(text, $"'{markerText}' işareti bulunamadı; şu satırı elle ekle: {line.Trim()}");

        string trimmed = line.Trim();
        if (!lines.Any(l => l.Trim() == trimmed))
        {
            string indent = lines[index][..(lines[index].Length - lines[index].TrimStart().Length)];
            lines.Insert(index, indent + trimmed);
        }

        foreach (string @using in usings)
            AddUsing(lines, @using);

        return new InjectResult(string.Join(newline, lines), null);
    }

    private static void AddUsing(List<string> lines, string @namespace)
    {
        string statement = $"using {@namespace};";
        if (lines.Any(l => l.Trim() == statement))
            return;

        // Dosya kapsamlı namespace'ten önceki son using satırının altına (yoksa en üste).
        int namespaceLine = lines.FindIndex(l => l.TrimStart().StartsWith("namespace ", StringComparison.Ordinal));
        int limit = namespaceLine < 0 ? lines.Count : namespaceLine;
        int last = lines.FindLastIndex(limit - 1 < 0 ? 0 : limit - 1, limit, l => l.TrimStart().StartsWith("using ", StringComparison.Ordinal) && l.TrimEnd().EndsWith(';'));
        lines.Insert(last + 1, statement);
    }
}
