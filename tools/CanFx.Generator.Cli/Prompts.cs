using CanFx.Generator;

namespace CanFx.Generator.Cli;

/// <summary>
/// Etkileşimli sorular (dış paket kullanılmaz): ok tuşlarıyla seçim, boşlukla çoklu seçim, metin. Girdi yönlendirilmişse
/// (betik, IDE eklentisi) ya da <c>--json</c> verilmişse soru sorulmaz; eksik bilgi hata olur.
/// </summary>
internal static class Prompts
{
    private const string Reset = "\u001b[0m";
    private const string Blue = "\u001b[34m";
    private const string Green = "\u001b[32m";
    private const string Cyan = "\u001b[36m";
    private const string Gray = "\u001b[90m";
    private const string Yellow = "\u001b[33m";

    public static bool Interactive { get; set; } = !Console.IsInputRedirected && !Console.IsOutputRedirected;

    public static bool Color => !Console.IsOutputRedirected && Environment.GetEnvironmentVariable("NO_COLOR") is null;

    public static string Paint(string text, string color) => Color ? color + text + Reset : text;

    public static string B(string text) => Paint(text, Blue);

    public static string G(string text) => Paint(text, Green);

    public static string Dim(string text) => Paint(text, Gray);

    public static string Warn(string text) => Paint(text, Yellow);

    public static void Banner()
    {
        if (!Interactive)
            return;

        string[] lines =
        [
            "  ___ __ _ _ __         __ _  ___ _ __  ",
            " / __/ _` | '_ \\ _____ / _` |/ _ \\ '_ \\ ",
            "| (_| (_| | | | |_____| (_| |  __/ | | |",
            " \\___\\__,_|_| |_|      \\__, |\\___|_| |_|",
            "                       |___/            ",
        ];
        foreach (string line in lines)
            Console.WriteLine(Paint(line, Cyan));
        Console.WriteLine();
    }

    /// <summary>Tek seçim: ↑/↓ ile gezilir, Enter ile seçilir.</summary>
    public static string Select(string title, IReadOnlyList<string> options, int selected = 0)
    {
        EnsureInteractive(title);
        if (options.Count == 0)
            throw new GeneratorException($"{title}: seçenek yok.");

        Console.WriteLine(title);
        int index = Math.Clamp(selected, 0, options.Count - 1);
        int visible = Math.Min(options.Count, 12);
        Draw(first: true);

        while (true)
        {
            ConsoleKey key = Console.ReadKey(intercept: true).Key;
            switch (key)
            {
                case ConsoleKey.UpArrow:
                    index = (index - 1 + options.Count) % options.Count;
                    break;
                case ConsoleKey.DownArrow:
                    index = (index + 1) % options.Count;
                    break;
                case ConsoleKey.Enter:
                    Clear(visible);
                    Console.WriteLine($"  {B(options[index])}");
                    return options[index];
                case ConsoleKey.Escape:
                    throw new GeneratorException("İptal edildi.");
            }

            Draw(first: false);
        }

        void Draw(bool first)
        {
            if (!first)
                Console.Write($"\u001b[{visible}A");
            int start = Math.Clamp(index - visible / 2, 0, options.Count - visible);
            for (int i = start; i < start + visible; i++)
            {
                string line = i == index ? $"{B(">")} {B(options[i])}" : $"  {options[i]}";
                Console.Write("\r\u001b[2K" + line + "\n");
            }
        }
    }

    /// <summary>Çoklu seçim: ↑/↓ ile gezilir, Boşluk ile işaretlenir, Enter ile onaylanır.</summary>
    public static IReadOnlyList<string> MultiSelect(string title, IReadOnlyList<(string Option, bool Checked)> options)
    {
        EnsureInteractive(title);
        Console.WriteLine(title);
        bool[] state = options.Select(o => o.Checked).ToArray();
        int index = 0;
        int lines = options.Count + 1;
        Draw(first: true);

        while (true)
        {
            ConsoleKey key = Console.ReadKey(intercept: true).Key;
            switch (key)
            {
                case ConsoleKey.UpArrow:
                    index = (index - 1 + options.Count) % options.Count;
                    break;
                case ConsoleKey.DownArrow:
                    index = (index + 1) % options.Count;
                    break;
                case ConsoleKey.Spacebar:
                    state[index] = !state[index];
                    break;
                case ConsoleKey.A:
                    bool all = !state.All(s => s);
                    Array.Fill(state, all);
                    break;
                case ConsoleKey.Enter:
                    Clear(lines);
                    List<string> chosen = options.Where((_, i) => state[i]).Select(o => o.Option).ToList();
                    Console.WriteLine($"  {B(chosen.Count == 0 ? "(hiçbiri)" : string.Join(", ", chosen))}");
                    return chosen;
                case ConsoleKey.Escape:
                    throw new GeneratorException("İptal edildi.");
            }

            Draw(first: false);
        }

        void Draw(bool first)
        {
            if (!first)
                Console.Write($"\u001b[{lines}A");
            for (int i = 0; i < options.Count; i++)
            {
                string box = state[i] ? B("[X]") : "[ ]";
                string text = i == index ? B(options[i].Option) : options[i].Option;
                Console.Write($"\r\u001b[2K{(i == index ? B(">") : " ")} {box} {text}\n");
            }

            Console.Write("\r\u001b[2K" + Dim("(Boşluk: işaretle, A: hepsi, Enter: onayla)") + "\n");
        }
    }

    /// <summary>Metin sorusu; boş bırakılırsa <paramref name="defaultValue"/>.</summary>
    public static string Text(string title, string? defaultValue = null)
    {
        EnsureInteractive(title);
        Console.Write(defaultValue is { Length: > 0 } ? $"{title} {Dim($"({defaultValue})")}: " : $"{title}: ");
        string? value = Console.ReadLine()?.Trim();
        return string.IsNullOrEmpty(value) ? defaultValue ?? string.Empty : value;
    }

    private static void Clear(int lines)
    {
        Console.Write($"\u001b[{lines}A");
        for (int i = 0; i < lines; i++)
            Console.Write("\r\u001b[2K\n");
        Console.Write($"\u001b[{lines}A");
    }

    private static void EnsureInteractive(string question)
    {
        if (!Interactive)
            throw new GeneratorException($"Eksik bilgi: \"{question.Trim()}\" (etkileşimsiz çalışmada argüman olarak ver; can-gen --help).");
    }
}
