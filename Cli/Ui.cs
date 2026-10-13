using static System.ConsoleColor;

namespace DiskTrim.Cli;

/// <summary>Console output helpers: colours, columns, prompts and a live status line.</summary>
static class Ui
{
    static readonly object Gate = new();
    static CancellationTokenSource? activeCts;

    /// <summary>Ctrl+C stops the running operation (keeping partial results) instead of killing the app.</summary>
    public static void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
    {
        var cts = activeCts;
        if (cts is null) return;
        e.Cancel = true;
        cts.Cancel();
    }

    public static int Width
    {
        get
        {
            try { return Console.IsOutputRedirected ? 160 : Math.Max(60, Console.WindowWidth - 1); }
            catch { return 119; }
        }
    }

    public static void Write(string text, ConsoleColor? color = null)
    {
        lock (Gate)
        {
            if (color is { } c && !Console.IsOutputRedirected)
            {
                var old = Console.ForegroundColor;
                Console.ForegroundColor = c;
                Console.Write(text);
                Console.ForegroundColor = old;
            }
            else Console.Write(text);
        }
    }

    public static void Line(string text = "", ConsoleColor? color = null)
    {
        Write(text.TrimEnd(' '), color);
        Console.WriteLine();
    }

    public static void Title(string text)
    {
        Console.WriteLine();
        Line(text, Cyan);
        Line(new string('─', Math.Min(Width, Math.Max(text.Length, 50))), DarkGray);
    }

    public static void Muted(string text) => Line(text, DarkGray);
    public static void Ok(string text) => Line(text, Green);
    public static void Warn(string text) => Line(text, Yellow);
    public static void Error(string text) => Line(text, Red);

    public static ConsoleColor LevelColor(SafetyLevel level) => level switch
    {
        SafetyLevel.Safe => Green,
        SafetyLevel.Caution => Yellow,
        SafetyLevel.Review => Cyan,
        _ => Red,
    };

    public static string LevelShort(SafetyLevel level) => level switch
    {
        SafetyLevel.Safe => "Safe",
        SafetyLevel.Caution => "Caution",
        SafetyLevel.Review => "Decide",
        _ => "Keep",
    };

    public static void Level(SafetyLevel level) => Write(LevelShort(level).PadRight(8), LevelColor(level));

    public static void Legend() =>
        Muted("Safe = cache/temp, rebuilt automatically · Caution = side effect · Decide = your files, your call · Keep = needed by Windows/programs");

    /// <summary>Pads to a column width. Longer text is never cut — it just pushes the rest of that row to the right.</summary>
    public static string Fit(string text, int width) =>
        text.Length < width ? text.PadRight(width) : text + " ";

    /// <summary>Shortens with … in the middle — only for the live status line, which must stay on one line.</summary>
    static string OneLine(string text, int width)
    {
        if (width <= 1) return "";
        if (text.Length <= width) return text.PadRight(width);
        int tail = (width - 1) * 2 / 3, head = width - 1 - tail;
        return text[..head] + "…" + text[^tail..];
    }

    public static string Bar(double fraction, int width)
    {
        int n = (int)Math.Round(Math.Clamp(fraction, 0, 1) * width);
        return new string('█', n) + new string('░', width - n);
    }

    /// <summary>Word-wraps text with an indent on every line.</summary>
    public static void Wrapped(string text, int indent, ConsoleColor? color = DarkGray)
    {
        int width = Math.Max(30, Width - indent);
        var line = new StringBuilder();
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length > 0 && line.Length + 1 + word.Length > width)
            {
                Line(new string(' ', indent) + line, color);
                line.Clear();
            }
            if (line.Length > 0) line.Append(' ');
            line.Append(word);
        }
        if (line.Length > 0) Line(new string(' ', indent) + line, color);
    }

    public static bool Confirm(string question, bool assumeYes = false)
    {
        if (assumeYes) return true;
        if (Console.IsInputRedirected) { Warn(question + " — no answer possible (input redirected); use --yes."); return false; }
        Write(question + " [y/N] ", Yellow);
        var answer = Console.ReadLine()?.Trim().ToLowerInvariant();
        return answer is "y" or "yes";
    }

    public static string? Ask(string prompt, string? defaultValue = null)
    {
        Write(prompt + (defaultValue is null ? "" : $" [{defaultValue}]") + ": ", White);
        var s = Console.ReadLine();
        if (s is null) return null;
        s = s.Trim();
        return s.Length == 0 ? defaultValue : s;
    }

    /// <summary>Runs work while a spinner line shows <paramref name="status"/>; Ctrl+C cancels the token.</summary>
    public static T WithProgress<T>(Func<string> status, Func<CancellationToken, T> work)
    {
        var cts = new CancellationTokenSource();
        activeCts = cts;
        bool live = !Console.IsOutputRedirected;
        using var stop = new ManualResetEventSlim();
        Thread? spinner = null;
        if (live)
        {
            spinner = new Thread(() =>
            {
                const string frames = "|/-\\";
                int i = 0;
                while (!stop.Wait(200))
                {
                    var text = $" {frames[i++ % 4]} {status()}";
                    lock (Gate) Console.Write("\r" + OneLine(text, Width - 1));
                }
            }) { IsBackground = true };
            spinner.Start();
        }
        try
        {
            return work(cts.Token);
        }
        finally
        {
            stop.Set();
            spinner?.Join();
            if (live) lock (Gate) Console.Write("\r" + new string(' ', Width - 1) + "\r");
            activeCts = null;
            cts.Dispose();
        }
    }

    public static void WithProgress(Func<string> status, Action<CancellationToken> work) =>
        WithProgress(status, ct => { work(ct); return 0; });
}
