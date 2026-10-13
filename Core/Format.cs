namespace DiskTrim.Core;

public static class Format
{
    static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    public static string Bytes(long bytes)
    {
        double v = Math.Abs((double)bytes);
        int i = 0;
        while (v >= 1024 && i < Units.Length - 1) { v /= 1024; i++; }
        string number = i == 0 ? v.ToString("0", CultureInfo.CurrentCulture)
            : v.ToString(v < 10 ? "0.##" : v < 100 ? "0.#" : "0", CultureInfo.CurrentCulture);
        return (bytes < 0 ? "-" : "") + number + " " + Units[i];
    }

    public static string Count(long n) => n.ToString("N0", CultureInfo.CurrentCulture);

    public static string Percent(double fraction) => (fraction * 100).ToString("0.#", CultureInfo.CurrentCulture) + " %";

    public static string Date(DateTime d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string Stamp(DateTime d) => d.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    /// <summary>Parses "100MB", "1.5 GB", "500k" or a plain byte count.</summary>
    public static bool TryParseSize(string? text, out long bytes)
    {
        bytes = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var m = Regex.Match(text.Trim(), @"^(\d+(?:[.,]\d+)?)\s*([KMGT]?)B?$", RegexOptions.IgnoreCase);
        if (!m.Success) return false;
        double v = double.Parse(m.Groups[1].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
        string unit = m.Groups[2].Value.ToUpperInvariant();
        int pow = unit.Length == 0 ? 0 : " KMGT".IndexOf(unit[0]);
        bytes = (long)(v * Math.Pow(1024, pow));
        return true;
    }

    /// <summary>Parses "30d", "2w", "6m", "1y" or a plain number of days.</summary>
    public static bool TryParseAge(string? text, out TimeSpan age)
    {
        age = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var m = Regex.Match(text.Trim(), @"^(\d+)\s*([dwmy]?)$", RegexOptions.IgnoreCase);
        if (!m.Success) return false;
        int n = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        int days = m.Groups[2].Value.ToLowerInvariant() switch { "w" => 7, "m" => 30, "y" => 365, _ => 1 };
        age = TimeSpan.FromDays((double)n * days);
        return true;
    }
}
