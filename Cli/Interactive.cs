using static System.ConsoleColor;

namespace DiskTrim.Cli;

/// <summary>Menu-driven mode, used when DiskTrim starts without a command (or from the right-click menu).</summary>
static class Interactive
{
    const long MB = 1024L * 1024;

    static readonly (string Key, string Text)[] Menu =
    [
        ("1", "Home — drives & recommendations"),
        ("2", "Cleanup — temporary files & caches"),
        ("3", "Scan a drive or folder"),
        ("4", "Explorer — browse folders by size"),
        ("5", "Large & old files"),
        ("6", "Duplicates"),
        ("7", "File types"),
        ("8", "Installed apps"),
        ("9", "History — what changed"),
        ("10", "Report — save an HTML report"),
        ("11", "Is it in use? — check a file or folder"),
        ("12", "Settings — exclusions, schedule, right-click menu, logs"),
        ("A", "Restart as administrator"),
        ("Q", "Quit"),
    ];

    public static int Run(string? startPath)
    {
        if (Console.IsInputRedirected)
        {
            Ui.Error("Interactive mode needs a console. Run 'DiskTrim help' for the command-line usage.");
            return 1;
        }

        if (startPath is not null)
        {
            Header();
            if (Guard(() => Session.Scan(startPath))) Browse(Session.LastScan!.Root);
        }

        while (true)
        {
            Header();
            foreach (var (key, text) in Menu)
            {
                if (key == "A" && AppPaths.IsAdmin) continue;
                Ui.Write($"  {key,3}  ", Cyan);
                Ui.Line(text);
            }
            var choice = Ui.Ask("\nChoose")?.ToUpperInvariant();
            if (choice is null or "Q" or "0" or "EXIT") return 0;

            Guard(() =>
            {
                switch (choice)
                {
                    case "1": Home(); break;
                    case "2": Cleanup(); break;
                    case "3": ScanPrompt(); break;
                    case "4": Browse(EnsureScan().Root); break;
                    case "5": Files(); break;
                    case "6": Duplicates(); break;
                    case "7": Views.Types(EnsureScan(), 25); break;
                    case "8": Apps(); break;
                    case "9": History(); break;
                    case "10": Report(); break;
                    case "11": InUse(); break;
                    case "12": Settings(); break;
                    case "A":
                        if (Commands.RestartAsAdmin(Session.LastScan?.Root.FullPath)) Environment.Exit(0);
                        break;
                    default: Ui.Warn("Pick one of the numbers or letters above."); break;
                }
            });
            Pause();
        }
    }

    static void Header()
    {
        Console.WriteLine();
        Ui.Write("DiskTrim ", Cyan);
        Ui.Line($"{AppPaths.Version} · console menu" + (AppPaths.IsAdmin ? " (Administrator)" : ""), DarkGray);
        if (Session.LastScan is { } s)
            Ui.Muted($"Current scan: {s.Root.FullPath} · {Format.Bytes(s.TotalBytes)} · {Format.Stamp(s.Time)}{(s.Cancelled ? " (partial)" : "")}");
        else
            Ui.Muted("Nothing scanned yet — pages that need a scan will scan the system drive first.");
    }

    /// <summary>Runs a page, showing errors instead of leaving the menu.</summary>
    static bool Guard(Action action)
    {
        try { action(); return true; }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Ui.Error(ex.Message);
            return false;
        }
        catch (Exception ex)
        {
            Ui.Error("Something went wrong: " + ex.Message);
            AppPaths.LogError(ex);
            return false;
        }
    }

    static void Pause()
    {
        Ui.Write("\nPress Enter to return to the menu…", DarkGray);
        Console.ReadLine();
    }

    static ScanResult EnsureScan() => Session.LastScan ?? Session.Scan(AppPaths.SystemRoot);

    // ---- pages -----------------------------------------------------------------------------------

    static void Home()
    {
        Views.Drives();
        var items = Session.MeasureCatalog();
        var recs = Recommendations.Build(items, Session.LastScan);
        Views.Recommendations(recs);
        if (recs.Count == 0) return;

        var pick = Ui.Ask("\nAct on a recommendation (number, Enter to skip)");
        if (!int.TryParse(pick, out int n) || n < 1 || n > recs.Count) return;
        var r = recs[n - 1];
        switch (r.Action)
        {
            case RecAction.CleanSafe:
                Commands.RunCleanup(Commands.Select(items, "safe"), assumeYes: false); break;
            case RecAction.EmptyRecycleBin:
                Commands.RunCleanup(Commands.Select(items, "recycle"), assumeYes: false); break;
            case RecAction.TurnOffHibernation:
                Commands.RunCleanup(Commands.Select(items, "hiberfil"), assumeYes: false); break;
            case RecAction.OpenCleanup:
                Cleanup(); break;
            case RecAction.RestartAdmin:
                if (Commands.RestartAsAdmin(Session.LastScan?.Root.FullPath)) Environment.Exit(0);
                break;
            case RecAction.ShowFile when r.Path is not null:
                ShowInExplorer(r.Path); break;
            case RecAction.ShowFolder when r.Path is not null:
                if (Session.LastScan?.Find(r.Path) is { } node) Browse(node); break;
            case RecAction.ShowFiles:
                Views.Files(EnsureScan(), 100 * MB, TimeSpan.FromDays(365), null, 50, hideKeep: true); break;
            case RecAction.FindDuplicates:
                Duplicates(); break;
        }
    }

    static void Cleanup()
    {
        var items = Session.MeasureCatalog();
        Views.Cleanup(items);
        Ui.Line();
        var sel = Ui.Ask("Clean which items? ('safe' = all Safe items, or numbers/ids like 1,4,edge; Enter = none)");
        if (string.IsNullOrWhiteSpace(sel)) return;
        Commands.RunCleanup(Commands.Select(items, sel), assumeYes: false);
    }

    static void ScanPrompt()
    {
        var path = Ui.Ask("Folder to analyse", Session.LastScan?.Root.FullPath ?? AppPaths.SystemRoot);
        if (path is null) return;
        var scan = Session.Scan(path);
        Views.Tree(scan, scan.Root, 1, 20);
    }

    static void Files()
    {
        var scan = EnsureScan();
        var min = Ui.Ask("Minimum size (e.g. 100MB, 1GB)", "100MB");
        var age = Ui.Ask("Not modified for at least (e.g. 6m, 1y; Enter = any time)", "");
        var type = Ui.Ask($"Type ({string.Join(", ", FileCategories.Names)}; Enter = all types)", "");

        if (!Format.TryParseSize(min, out long minBytes)) throw new ArgumentException($"'{min}' is not a size.");
        TimeSpan? older = null;
        if (!string.IsNullOrEmpty(age))
            older = Format.TryParseAge(age, out var a) ? a : throw new ArgumentException($"'{age}' is not an age.");
        string? category = string.IsNullOrEmpty(type) ? null
            : FileCategories.Names.FirstOrDefault(n => n.Equals(type, StringComparison.OrdinalIgnoreCase))
              ?? throw new ArgumentException($"Unknown type '{type}'.");

        Views.Files(scan, minBytes, older, category, 50, hideKeep: true);
    }

    static void Duplicates()
    {
        var scan = EnsureScan();
        var min = Ui.Ask("Ignore files smaller than", "1MB");
        if (!Format.TryParseSize(min, out long minBytes)) throw new ArgumentException($"'{min}' is not a size.");
        var keepText = Ui.Ask("Which copy to keep — oldest, newest or shortest path", "oldest")!.ToLowerInvariant();
        var keep = keepText.StartsWith('n') ? KeepRule.Newest : keepText.StartsWith('s') ? KeepRule.ShortestPath : KeepRule.Oldest;

        var groups = Commands.FindDuplicates(scan, minBytes);
        Views.Duplicates(groups, keep, 25);
        if (groups.Count > 0 && Ui.Confirm("\nMove every copy except the kept one to the Recycle Bin?"))
            Commands.RecycleDuplicates(groups, keep, assumeYes: true);
    }

    static void Apps()
    {
        var apps = Ui.WithProgress(() => "Reading installed programs…", _ => InstalledApps.Read());
        Views.Apps(apps, 60);
        var pick = Ui.Ask("\nUninstall a program (number, Enter to skip)");
        if (int.TryParse(pick, out int n) && n >= 1 && n <= Math.Min(60, apps.Count))
            Commands.Uninstall(apps[n - 1], assumeYes: false);
    }

    static void History()
    {
        var snaps = Core.History.List();
        Views.Snapshots(snaps);
        if (snaps.Count < 2) return;
        var root = Ui.Ask("\nCompare the last two scans of which folder", snaps[0].Root);
        if (root is not null) Commands.CompareLatest(snaps, root, 100 * MB, 30);
    }

    static void Report()
    {
        var scan = EnsureScan();
        var file = Ui.Ask("Save report as", Commands.DefaultReportPath());
        if (file is null) return;
        Commands.WriteReport(scan, file);
        if (Ui.Confirm("Open it now?"))
            Process.Start(new ProcessStartInfo(Path.GetFullPath(file)) { UseShellExecute = true })?.Dispose();
    }

    static void InUse()
    {
        var path = Ui.Ask("File or folder to check");
        if (!string.IsNullOrWhiteSpace(path)) Commands.CheckInUse(path);
    }

    static void Settings()
    {
        Commands.ShowSettings();
        Ui.Line();
        Ui.Line("  1  Add an excluded folder        2  Remove an excluded folder");
        Ui.Line("  3  Schedule automatic cleanup    4  Turn automatic cleanup off");
        Ui.Line("  5  Add right-click menu          6  Remove right-click menu");
        Ui.Line("  7  Show deletion logs");
        switch (Ui.Ask("Choose (Enter = back)"))
        {
            case "1": if (Ui.Ask("Folder") is { } add) Commands.Exclude("add", add); break;
            case "2": if (Ui.Ask("Folder") is { } rem) Commands.Exclude("remove", rem); break;
            case "3":
                var freq = Ui.Ask("How often — daily, weekly or monthly", "weekly");
                var time = Ui.Ask("At what time (HH:mm)", "12:30");
                if (freq is not null && time is not null && Ui.Confirm($"Schedule automatic cleanup {freq} at {time}?"))
                    Commands.Schedule(freq, time);
                break;
            case "4": Commands.Schedule("off", null); break;
            case "5": Commands.Shell("install"); break;
            case "6": Commands.Shell("uninstall"); break;
            case "7": Commands.ShowLogs(); break;
        }
    }

    // ---- explorer --------------------------------------------------------------------------------

    /// <summary>Folder browser: numbers open a folder, ".." goes up, "i N" explains, "u N" checks in-use, "o N" opens Explorer.</summary>
    static void Browse(DirNode start)
    {
        const int top = 30;
        var node = start;
        while (true)
        {
            var entries = Views.Entries(node, top);
            Ui.Title($"{node.FullPath}  ·  {Format.Bytes(node.Size)}  ·  {Format.Count(node.FileCount)} files" +
                     (node.LastChange > DateTime.MinValue ? $"  ·  last change {Format.Date(node.LastChange)}" : ""));
            if (entries.Count == 0) Ui.Muted("  (empty or not readable)");
            for (int i = 0; i < entries.Count; i++) Views.PrintEntry(entries[i], node.Size, 0, i + 1);
            int hidden = node.Children.Count + node.Files.Count - entries.Count;
            if (hidden > 0) Ui.Muted($"       … and {Format.Count(hidden)} smaller items");

            Ui.Muted("  N open · .. up · i N details · u N in use? · o N show in Explorer · q back");
            var cmd = Ui.Ask(">")?.Trim();
            if (cmd is null or "q" or "Q" or "") return;
            if (cmd == "..")
            {
                if (node.Parent is not null) node = node.Parent;
                continue;
            }

            var parts = cmd.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            string verb = parts.Length == 2 ? parts[0].ToLowerInvariant() : "";
            string numText = parts.Length == 2 ? parts[1] : parts[0];
            if (!int.TryParse(numText, out int n) || n < 1 || n > entries.Count)
            {
                Ui.Warn("Type a number from the list.");
                continue;
            }
            var e = entries[n - 1];
            switch (verb)
            {
                case "i": Views.Details(e.Path); Pause(); break;
                case "u": Commands.CheckInUse(e.Path); Pause(); break;
                case "o": ShowInExplorer(e.Path); break;
                default:
                    if (e.Dir is { IsLink: false } d) node = d;
                    else { Views.Details(e.Path); Pause(); }
                    break;
            }
        }
    }

    static void ShowInExplorer(string path) =>
        Process.Start("explorer.exe", $"/select,\"{path}\"")?.Dispose();
}
