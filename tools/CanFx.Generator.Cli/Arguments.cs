using CanFx.Generator;

namespace CanFx.Generator.Cli;

/// <summary>
/// Basit argüman ayrıştırıcı (dış paket kullanılmaz). Konumsal argümanlar, değerli seçenekler (<c>--project x</c>,
/// <c>-p x</c>), bayraklar (<c>--force</c>, <c>-c</c>) ve <c>--</c> sonrası aynen aktarılan argümanlar.
/// </summary>
internal sealed class Arguments
{
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        ["-p"] = "project",
        ["-c"] = "caching",
        ["-l"] = "logging",
        ["-t"] = "transaction",
        ["-s"] = "secured",
        ["-d"] = "dynamic-query",
        ["-e"] = "endpoint-method",
        ["-o"] = "output",
        ["-f"] = "force",
        ["-h"] = "help",
        ["-v"] = "version",
    };

    private static readonly HashSet<string> ValueOptions = new(StringComparer.Ordinal)
    {
        "project", "plural", "endpoint-method", "output", "feed", "database",
    };

    private readonly Dictionary<string, string?> _options = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Positionals { get; } = [];

    /// <summary><c>--</c> sonrası (ör. <c>can-gen new Shop -- --MultiTenancy true</c>).</summary>
    public List<string> PassThrough { get; } = [];

    public bool Json => Flag("json");

    public static Arguments Parse(string[] args)
    {
        var result = new Arguments();
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg == "--")
            {
                result.PassThrough.AddRange(args[(i + 1)..]);
                break;
            }

            string? name = arg.StartsWith("--", StringComparison.Ordinal) && arg.Length > 2 ? arg[2..]
                : Aliases.TryGetValue(arg, out string? alias) ? alias
                : null;

            if (name is null)
            {
                result.Positionals.Add(arg);
                continue;
            }

            if (ValueOptions.Contains(name))
            {
                if (i + 1 >= args.Length)
                    throw new GeneratorException($"--{name} için değer gerekli.");
                result._options[name] = args[++i];
            }
            else
            {
                result._options[name] = null;
            }
        }

        return result;
    }

    public string? Positional(int index) => index < Positionals.Count ? Positionals[index] : null;

    public bool Flag(string name) => _options.ContainsKey(name);

    public string? Option(string name) => _options.TryGetValue(name, out string? value) ? value : null;
}
