using static System.ConsoleColor;

namespace DiskTrim.Cli;

/// <summary>One method per command-line verb, plus the actions the interactive menu reuses.</summary>
static class Commands
{
    const long MB = 1024L * 1024;

    // ---- home ------------------------------------------------------------------------------------

    public static int Home(Args a)
    {
        Views.Drives();
        var items = Session.MeasureCatalog();
        Views.Recommendations(Recommendations.Build(items, Session.LastScan));
        return 0;
    }

    // ---- scan / tree -----------------------------------------------------------------------------

    public static int Scan(Args a)
    {
        var scan = Session.GetOrScan(a.Arg(0));
        Views.Tree(scan, scan.Root, a.Int("depth", 1), a.Int("top", 20));
        return 0;
    }

    // ---- files -----------------------------------------------------------------------------------

    public static int Files(Args a)
    {
        string? category = a.Get("type");
        if (category is not null && !FileCategories.Names.Contains(category, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException($"--type must be one of: {string.Join(", ", FileCategories.Names)}");

        // Validate options before a scan that can take minutes.
        long minSize = a.Size("min-size", 100 * MB);
        var olderThan = a.Age("older-than");
        var scan = Session.GetOrScan(a.Arg(0));
        Views.Files(scan, minSize, olderThan, category, a.Int("top", 50), hideKeep: !a.Has("all"));
        return 0;
    }

    // ---- types -----------------------------------------------------------------------------------

    public static int Types(Args a)
    {
        var scan = Session.GetOrScan(a.Arg(0));
        Views.Types(scan, a.Int("top", 25));
        return 0;
    }

    // ---- duplicates ------------------------------------------------------------------------------

    public static int Duplicates(Args a)
    {
        var keep = (a.Get("keep") ?? "oldest").ToLowerInvariant() switch
        {
            "oldest" => KeepRule.Oldest,
            "newest" => KeepRule.Newest,
            "shortest" or "shortest-path" => KeepRule.ShortestPath,
            var k => throw new ArgumentException($"--keep '{k}': use oldest, newest or shortest."),
        };
        long minSize = a.Size("min-size", 1 * MB);
        var scan = Session.GetOrScan(a.Arg(0));
        var groups = FindDuplicates(scan, minSize);
        Views.Duplicates(groups, keep, a.Int("top", 25));
        if (a.Has("recycle")) RecycleDuplicates(groups, keep, a.AssumeYes);
        else if (groups.Count > 0) Ui.Muted("Add --recycle to move every copy except the kept one to the Recycle Bin.");
        return 0;
    }

    public static List<DuplicateGroup> FindDuplicates(ScanResult scan, long minSize)
    {
        var progress = new ScanProgress();
        int unreadable = 0;
        var groups = Ui.WithProgress(
            () => $"Comparing files… {Format.Count(progress.Files)} compared · {progress.Current}",
            ct => Core.Duplicates.Find(scan.Files, minSize, progress, ct, out unreadable));
        if (unreadable > 0) Ui.Muted($"{Format.Count(unreadable)} file(s) could not be read (in use or no access) and were skipped.");
        return groups;
    }

    public static void RecycleDuplicates(IReadOnlyList<DuplicateGroup> groups, KeepRule keep, bool assumeYes)
    {
        var marked = groups.SelectMany(g => g.Files.Where(f => f != g.Keeper(keep))).ToList();
        if (marked.Count == 0) return;
        long bytes = marked.Sum(f => f.Size);
        Ui.Line();
        Ui.Line($"{Format.Count(marked.Count)} copies ticked · {Format.Bytes(bytes)} would be freed (after emptying the Recycle Bin).", White);
        if (!Ui.Confirm($"Move {Format.Count(marked.Count)} duplicate copies to the Recycle Bin?", assumeYes)) { Ui.Muted("Nothing was changed."); return; }

        using var log = new AuditLog("duplicates");
        int ok = 0, failed = 0;
        long moved = 0;
        Ui.WithProgress(() => $"Moving to Recycle Bin… {ok + failed}/{marked.Count}", ct =>
        {
            foreach (var batch in marked.Chunk(50))
            {
                if (ct.IsCancellationRequested) break;
                Native.MoveToRecycleBin(batch.Select(f => f.FullPath).ToArray());
                foreach (var f in batch)
                {
                    bool gone = !File.Exists(f.FullPath);
                    if (gone) { ok++; moved += f.Size; } else failed++;
                    log.Write("duplicates", "recycle", f.FullPath, f.Size, gone ? "Moved to Recycle Bin" : "In use or protected — kept");
                }
            }
        });
        Ui.Ok($"Moved {Format.Count(ok)} copies ({Format.Bytes(moved)}) to the Recycle Bin.");
        if (failed > 0) Ui.Warn($"{failed} item(s) in use or protected were left alone.");
        Ui.Muted("The space is freed when you empty the Recycle Bin (cleanup --run recycle). Log: " + log.FilePath);
    }

    // ---- cleanup ---------------------------------------------------------------------------------

    public static int Cleanup(Args a)
    {
        var items = Session.MeasureCatalog();
        Views.Cleanup(items);

        var run = a.Get("run");
        if (run is null)
        {
            Ui.Line();
            Ui.Muted("Clean with:  DiskTrim cleanup --run safe        (all 'Safe' items that are ready)");
            Ui.Muted("             DiskTrim cleanup --run 1,4,edge    (numbers or ids from the list)");
            return 0;
        }
        var selected = Select(items, run);
        return RunCleanup(selected, a.AssumeYes) ? 0 : 1;
    }

    /// <summary>"safe" = every Safe item that is ready; otherwise list numbers and/or ids.</summary>
    public static List<CleanupItem> Select(IReadOnlyList<CleanupItem> items, string selection)
    {
        if (selection.Trim().Equals("safe", StringComparison.OrdinalIgnoreCase))
            return SafeItems(items);

        var picked = new List<CleanupItem>();
        foreach (var token in selection.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries))
        {
            var item = int.TryParse(token, out int n) && n >= 1 && n <= items.Count
                ? items[n - 1]
                : items.FirstOrDefault(i => i.Id.Equals(token, StringComparison.OrdinalIgnoreCase));
            if (item is null) Ui.Warn($"Unknown item '{token}' — ignored.");
            else if (!picked.Contains(item)) picked.Add(item);
        }
        return picked;
    }

    static List<CleanupItem> SafeItems(IEnumerable<CleanupItem> items) =>
        items.Where(i => i.Level == SafetyLevel.Safe && !i.IsBlocked && i.Bytes > 0
                         && i.Kind is CleanupKind.Files or CleanupKind.Command).ToList();

    /// <summary>Shows what will happen, asks, cleans, and reports. Returns false if nothing ran.</summary>
    public static bool RunCleanup(IReadOnlyList<CleanupItem> selected, bool assumeYes)
    {
        foreach (var b in selected.Where(i => i.IsBlocked))
            Ui.Warn($"Skipped '{b.Title}': {b.BlockedReason}.");
        var runnable = selected.Where(i => !i.IsBlocked).ToList();
        if (runnable.Count == 0)
        {
            Ui.Warn("Tick at least one item that isn't blocked (blocked items need administrator rights or an app to be closed).");
            return false;
        }

        Ui.Title($"Clean {runnable.Count} item(s), about {Format.Bytes(runnable.Sum(i => i.Bytes))}?");
        foreach (var i in runnable)
        {
            Ui.Write("  • ");
            Ui.Level(i.Level);
            Ui.Line(i.Title, White);
            Ui.Wrapped("If you clean it: " + i.Impact, 14);
        }
        Ui.Muted("Files in use are skipped automatically. Every deleted file is logged.");
        if (!Ui.Confirm("Clean now?", assumeYes)) { Ui.Muted("Nothing was changed."); return false; }

        using var log = new AuditLog();
        long freed = 0;
        foreach (var item in runnable)
        {
            string status = item.Title;
            var result = Ui.WithProgress(() => $"Cleaning {item.Title}… {status}", ct =>
                CleanupEngine.Clean(item, log, s => status = s, ct));
            freed += result.FreedBytes;
            Ui.Write($"  {Ui.Fit(item.Title, 46)} ");
            Ui.Line(result.Message, result.Failed ? Red : Green);
        }
        Ui.Line();
        Ui.Ok($"Cleanup finished — freed {Format.Bytes(freed)}.");
        if (log.HasEntries) Ui.Muted("Every deleted file is listed in " + log.FilePath);
        return true;
    }

    /// <summary>Unattended run for Task Scheduler: cleans every 'Safe' item that is ready. Never prompts.</summary>
    public static int CleanSafe(Args a)
    {
        var items = CleanupCatalog.Build();
        foreach (var i in items) CleanupEngine.Measure(i);
        var safe = SafeItems(items);
        using var log = new AuditLog("scheduled");
        long freed = 0;
        foreach (var item in safe)
        {
            var r = CleanupEngine.Clean(item, log, null, CancellationToken.None);
            freed += r.FreedBytes;
            Console.WriteLine($"{item.Id}: {r.Message}");
        }
        Console.WriteLine($"Done — freed {Format.Bytes(freed)}");
        return 0;
    }

    // ---- apps ------------------------------------------------------------------------------------

    public static int Apps(Args a)
    {
        var apps = Ui.WithProgress(() => "Reading installed programs…", _ => InstalledApps.Read());
        var filter = a.Arg(0);
        if (filter is not null)
            apps = apps.Where(x => x.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                                || x.Publisher.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        Views.Apps(apps, a.Int("top", 40));

        if (a.Get("uninstall") is { } name)
        {
            var hits = apps.Where(x => x.Name.Contains(name, StringComparison.OrdinalIgnoreCase)).ToList();
            var exact = hits.Where(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (exact.Count == 1) hits = exact;
            if (hits.Count != 1)
            {
                Ui.Warn(hits.Count == 0 ? $"No program matches '{name}'." : $"{hits.Count} programs match '{name}' — be more specific.");
                return 1;
            }
            Uninstall(hits[0], a.AssumeYes);
        }
        return 0;
    }

    public static void Uninstall(InstalledApp app, bool assumeYes)
    {
        Ui.Title("Uninstall program?");
        Ui.Line($"  {app.Name}", White);
        Ui.Muted($"  Publisher: {app.Publisher}   Version: {app.Version}   Size: {(app.SizeBytes > 0 ? Format.Bytes(app.SizeBytes) : "unknown")}");
        Ui.Muted("  The program's own uninstaller opens and you finish there. DiskTrim never deletes program folders itself.");
        if (!Ui.Confirm($"Start the uninstaller of {app.Name}?", assumeYes)) return;
        try
        {
            InstalledApps.StartUninstaller(app);
            Ui.Ok($"Started the uninstaller of {app.Name}.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Ui.Error("Couldn't start the uninstaller: " + ex.Message);
        }
    }

    // ---- history ---------------------------------------------------------------------------------

    public static int History(Args a)
    {
        string? root = a.Arg(0) is { } p ? Scanner.NormalizeRoot(p) : null;
        var snaps = Core.History.List(root);
        if (!a.Has("compare")) { Views.Snapshots(snaps); return 0; }
        return CompareLatest(snaps, root ?? AppPaths.SystemRoot, a.Size("min-change", 100 * MB), a.Int("top", 30));
    }

    public static int CompareLatest(IReadOnlyList<Snapshot> all, string root, long threshold, int top)
    {
        var snaps = all.Where(s => s.Root.Equals(Scanner.NormalizeRoot(root), StringComparison.OrdinalIgnoreCase)).ToList();
        if (snaps.Count < 2)
        {
            Ui.Muted(snaps.Count == 0 ? $"No scans of {root} saved yet." : "Only one scan so far. Scan again later to see what changed.");
            return 1;
        }
        var (after, before) = (snaps[0], snaps[1]);
        Views.Changes(before, after, Core.History.Compare(before, after, threshold), top);
        return 0;
    }

    // ---- report ----------------------------------------------------------------------------------

    public static int Report(Args a)
    {
        var scan = Session.GetOrScan(a.Arg(0));
        var file = a.Get("out") ?? DefaultReportPath();
        WriteReport(scan, file);
        return 0;
    }

    public static string DefaultReportPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        $"DiskTrim-{DateTime.Now:yyyyMMdd-HHmm}.html");

    public static void WriteReport(ScanResult scan, string file)
    {
        var items = Session.MeasureCatalog();
        file = Path.GetFullPath(file);
        Ui.WithProgress(() => "Writing report…", _ => ReportWriter.Write(file, scan, items));
        Ui.Ok("Report saved: " + file);
    }

    // ---- search ----------------------------------------------------------------------------------

    public static int Search(Args a)
    {
        var query = a.Arg(0) ?? throw new ArgumentException("Usage: DiskTrim search <name or *.ext> [path] [--top 50]");
        int top = a.Int("top", 50);
        var scan = Session.GetOrScan(a.Arg(1));
        var hits = ScanSearch.Find(scan, query, top, out int total);

        Ui.Title($"Search \"{query}\" in {scan.Root.FullPath}");
        if (total == 0) { Ui.Muted($"Nothing named \"{query}\" in this scan."); return 1; }
        Ui.Muted($"  {"Size",10}  {"Safety",-8}Path");
        foreach (var h in hits)
        {
            Ui.Write($"  {Format.Bytes(h.Size),10}  ");
            Ui.Level(SafetyRules.Classify(h.Path).Level);
            Ui.Line(h.Path + (h.IsFolder ? "\\" : ""), h.IsFolder ? ConsoleColor.White : ConsoleColor.Gray);
        }
        Ui.Muted($"{Format.Count(total)} match(es)" + (total > hits.Count ? $" · showing the biggest {hits.Count} (use --top)" : ""));
        return 0;
    }

    // ---- move ------------------------------------------------------------------------------------

    public static int Move(Args a)
    {
        var to = a.Get("to");
        if (a.Positional.Count == 0 || to is null)
            throw new ArgumentException("Usage: DiskTrim move <file or folder> [more…] --to <folder on another drive> [--yes]");
        var sources = a.Positional.Select(Scanner.NormalizeRoot).ToList();
        var missing = sources.Where(s => !File.Exists(s) && !Directory.Exists(s)).ToList();
        if (missing.Count > 0) throw new ArgumentException("Not found: " + string.Join(", ", missing));
        var destination = Scanner.NormalizeRoot(to);

        long bytes = Ui.WithProgress(() => "Measuring…", _ => FileMover.MeasureBytes(sources));
        if (FileMover.Validate(sources, destination, bytes) is { } problem) { Ui.Error(problem); return 2; }

        Ui.Title($"Move {sources.Count} item(s), {Format.Bytes(bytes)}, to {destination}");
        foreach (var s in sources) Ui.Line("  • " + s);
        Ui.Muted("Each file is copied, verified byte-for-byte (SHA-256) and only then removed from the original location. If anything fails, the original is kept.");
        if (FileMover.IsSameDrive(sources, destination))
            Ui.Warn($"{destination} is on the same drive, so moving won't free any space.");
        if (!Ui.Confirm("Move files?", a.AssumeYes)) { Ui.Muted("Nothing was changed."); return 1; }

        var inUse = Ui.WithProgress(() => "Checking that no running program uses it…", _ => ProcessInspector.WhoUses(sources, 1000));
        if (inUse.Count > 0)
        {
            Ui.Warn("In use right now — nothing was changed. Close these programs and try again:");
            foreach (var p in inUse) Ui.Line($"  • {p.Name} (PID {p.Id}) — {p.Reason}");
            return 1;
        }

        using var log = new AuditLog("move");
        string status = "";
        var result = Ui.WithProgress(() => status, ct => FileMover.Move(sources, destination, log, s => status = s, ct));
        Ui.Ok($"Moved {Format.Count(result.Moved)} file(s), {Format.Bytes(result.Bytes)} — every copy verified.");
        if (result.Failed > 0)
        {
            Ui.Warn($"{result.Failed} file(s) failed (originals kept):");
            foreach (var e in result.Errors.Take(20)) Ui.Line("  • " + e);
        }
        Ui.Muted("Log: " + log.FilePath);
        return result.Failed > 0 ? 1 : 0;
    }

    // ---- in use ----------------------------------------------------------------------------------

    public static int InUse(Args a)
    {
        var path = a.Arg(0) ?? throw new ArgumentException("Usage: DiskTrim inuse <file or folder>");
        CheckInUse(path);
        return 0;
    }

    public static void CheckInUse(string path)
    {
        path = Scanner.NormalizeRoot(path);
        if (!File.Exists(path) && !Directory.Exists(path)) { Ui.Error("Not found: " + path); return; }
        var procs = Ui.WithProgress(() => "Checking that no running program uses it…", _ => ProcessInspector.WhoUses(path));
        Views.InUse(path, procs);
    }

    // ---- settings --------------------------------------------------------------------------------

    public static int Settings(Args a)
    {
        switch (a.Arg(0)?.ToLowerInvariant())
        {
            case "exclude":
                return Exclude(a.Arg(1), a.Arg(2));
            case "schedule":
                return Schedule(a.Arg(1), a.Arg(2));
            case "shell":
                return Shell(a.Arg(1));
            case "logs":
                ShowLogs();
                return 0;
            default:
                ShowSettings();
                return 0;
        }
    }

    public static void ShowSettings()
    {
        var s = AppSettings.Load();
        Ui.Title("Settings");
        Ui.Line($"  DiskTrim {AppPaths.Version} · .NET {Environment.Version} · running as {(AppPaths.IsAdmin ? "administrator" : "standard user")}");
        Ui.Line($"  Data folder:        {AppPaths.DataDir}");
        Ui.Line($"  Excluded folders:   {(s.Exclusions.Count == 0 ? "none" : string.Join("; ", s.Exclusions))}");
        Ui.Line($"  Automatic cleanup:  {(ScheduledCleanup.IsScheduled() ? $"ON (Task Scheduler › {ScheduledCleanup.TaskName})" : "off")}");
        Ui.Line($"  Right-click menu:   {(ShellIntegration.IsInstalled() ? "installed" : "not installed")}");
        Ui.Muted("  Change with: settings exclude add|remove|list <folder> · settings schedule daily|weekly|monthly HH:mm|off · settings shell install|uninstall · settings logs");
    }

    public static int Exclude(string? action, string? folder)
    {
        var s = AppSettings.Load();
        switch (action?.ToLowerInvariant())
        {
            case "add" when folder is not null:
                var full = Scanner.NormalizeRoot(folder);
                if (!s.Exclusions.Contains(full, StringComparer.OrdinalIgnoreCase)) s.Exclusions.Add(full);
                s.Save();
                Ui.Ok($"Saved {s.Exclusions.Count} exclusion(s). They apply to the next scan.");
                return 0;
            case "remove" when folder is not null:
                var norm = Scanner.NormalizeRoot(folder);
                int removed = s.Exclusions.RemoveAll(e => e.Equals(norm, StringComparison.OrdinalIgnoreCase));
                s.Save();
                Ui.Line(removed > 0 ? "Removed." : "That folder wasn't excluded.");
                return 0;
            default:
                Ui.Title("Excluded folders");
                if (s.Exclusions.Count == 0) Ui.Muted("  none");
                foreach (var e in s.Exclusions) Ui.Line("  " + e);
                return 0;
        }
    }

    public static int Schedule(string? frequency, string? time)
    {
        frequency = frequency?.ToLowerInvariant();
        if (frequency is null or "status")
        {
            Ui.Line(ScheduledCleanup.IsScheduled()
                ? $"Automatic cleanup is ON (Task Scheduler › {ScheduledCleanup.TaskName})."
                : "Automatic cleanup is off.");
            return 0;
        }
        if (frequency == "off")
        {
            if (!ScheduledCleanup.IsScheduled()) { Ui.Muted("Nothing to turn off — automatic cleanup wasn't scheduled."); return 0; }
            var (code, output) = ScheduledCleanup.Delete();
            if (code == 0) Ui.Ok("Scheduled cleanup removed."); else Ui.Error(output);
            return code;
        }
        if (frequency is not ("daily" or "weekly" or "monthly"))
            throw new ArgumentException("Usage: settings schedule daily|weekly|monthly HH:mm  |  off  |  status");
        time ??= "12:30";
        if (!TimeOnly.TryParseExact(time, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            throw new ArgumentException("Enter the time as HH:mm, for example 12:30.");

        Ui.Muted(AppPaths.IsAdmin
            ? "Runs with administrator rights (includes Windows temp, update cache, crash dumps)."
            : "Runs as you (only your own temp files and caches). Run DiskTrim as administrator to include system items.");
        Ui.Muted($"Program: {AppPaths.ExePath} — keep DiskTrim in this folder, the task points at this exact file.");
        var (rc, outText) = ScheduledCleanup.Create(frequency, time);
        if (rc == 0) Ui.Ok($"Scheduled: 'Safe to delete' items are cleaned {frequency} at {time}.");
        else Ui.Error(outText);
        return rc;
    }

    public static int Shell(string? action)
    {
        try
        {
            switch (action?.ToLowerInvariant())
            {
                case "install":
                    ShellIntegration.Install();
                    Ui.Ok("Right-click any folder or drive → 'Analyze with DiskTrim (console)' (on Windows 11 it's under 'Show more options').");
                    return 0;
                case "uninstall" or "remove":
                    ShellIntegration.Uninstall();
                    Ui.Ok("Removed from the right-click menu.");
                    return 0;
                default:
                    Ui.Line(ShellIntegration.IsInstalled() ? "Right-click menu is installed." : "Right-click menu is not installed.");
                    return 0;
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            Ui.Error("Couldn't change the right-click menu: " + ex.Message);
            return 1;
        }
    }

    public static void ShowLogs()
    {
        Ui.Title("Deletion logs");
        var logs = AuditLog.List().Take(20).ToList();
        if (logs.Count == 0) Ui.Muted("  No cleanups yet.");
        foreach (var f in logs) Ui.Line($"  {Format.Stamp(f.LastWriteTime)}  {Format.Bytes(f.Length),9}  {f.FullName}");
    }

    // ---- elevation -------------------------------------------------------------------------------

    public static bool RestartAsAdmin(string? path)
    {
        try
        {
            var psi = new ProcessStartInfo(AppPaths.ExePath) { UseShellExecute = true, Verb = "runas" };
            psi.ArgumentList.Add("menu");
            if (path is not null) { psi.ArgumentList.Add("--path"); psi.ArgumentList.Add(path); }
            Process.Start(psi)?.Dispose();
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Ui.Warn("Administrator rights were not granted.");
            return false;
        }
    }
}
