namespace DiskTrim.Cli;

/// <summary>Minimal parser: "command positional… --option value --flag".</summary>
sealed class Args
{
    static readonly HashSet<string> Flags = new(StringComparer.OrdinalIgnoreCase)
    {
        "yes", "y", "recycle", "compare", "clean-safe", "help", "h", "?", "all",
    };

    readonly Dictionary<string, string?> options = new(StringComparer.OrdinalIgnoreCase);

    public string? Command { get; private set; }
    public List<string> Positional { get; } = [];

    public static Args Parse(string[] argv)
    {
        var a = new Args();
        for (int i = 0; i < argv.Length; i++)
        {
            var t = argv[i];
            if (t.StartsWith("--", StringComparison.Ordinal) || (t.Length == 2 && t[0] is '-' or '/' && char.IsLetter(t[1])) || t == "/?")
            {
                var key = t.TrimStart('-', '/');
                if (key.Length == 0) key = "?";
                string? value = null;
                if (!Flags.Contains(key) && i + 1 < argv.Length && !argv[i + 1].StartsWith("--", StringComparison.Ordinal))
                    value = argv[++i];
                a.options[key] = value;
            }
            else if (a.Command is null && a.Positional.Count == 0)
                a.Command = t.ToLowerInvariant();
            else
                a.Positional.Add(t);
        }

        // Switches understood by DiskTrim's GUI build: --scan [path], --clean-safe, --report [file], --path <folder>.
        if (a.Command is null)
        {
            if (a.Has("clean-safe")) a.Command = "clean-safe";
            else if (a.Has("scan")) { a.Command = "scan"; if (a.Get("scan") is { } p) a.Positional.Add(p); }
            else if (a.Has("report")) { a.Command = "report"; if (a.Get("report") is { } f) a.options["out"] = f; }
        }
        return a;
    }

    public bool Has(string key) => options.ContainsKey(key);

    public string? Get(string key) => options.TryGetValue(key, out var v) ? v : null;

    public bool AssumeYes => Has("yes") || Has("y");

    public bool WantsHelp => Has("help") || Has("h") || Has("?");

    public string? Arg(int index) => index < Positional.Count ? Positional[index] : null;

    public int Int(string key, int fallback) =>
        int.TryParse(Get(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : fallback;

    public long Size(string key, long fallback)
    {
        var raw = Get(key);
        if (raw is null) return fallback;
        if (Format.TryParseSize(raw, out var v)) return v;
        throw new ArgumentException($"--{key}: '{raw}' is not a size (examples: 500KB, 100MB, 1.5GB).");
    }

    public TimeSpan? Age(string key)
    {
        var raw = Get(key);
        if (raw is null) return null;
        if (Format.TryParseAge(raw, out var v)) return v;
        throw new ArgumentException($"--{key}: '{raw}' is not an age (examples: 30d, 6m, 1y).");
    }
}
