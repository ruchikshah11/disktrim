using System.Windows;
using System.Windows.Controls;

namespace DiskTrim.UI;

/// <summary>What you can do with a file or folder anywhere in the window.</summary>
public static class FileActions
{
    public static void ShowInExplorer(string path)
    {
        try { Process.Start("explorer.exe", $"/select,\"{path}\"")?.Dispose(); }
        catch (Exception ex) { Dialogs.Error(ex.Message); }
    }

    public static void Open(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) { Dialogs.Error(ex.Message); }
    }

    public static void CopyPaths(IEnumerable<string> paths)
    {
        try { Clipboard.SetText(string.Join(Environment.NewLine, paths)); }
        catch (Exception ex) { Dialogs.Error(ex.Message); }
    }

    public static async Task CheckInUseAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) { Dialogs.Info("Select one or more files or folders in the list first."); return; }
        using var op = AppState.Current.Begin("Checking running programs…");
        if (op is null) return;
        var procs = await Task.Run(() => ProcessInspector.WhoUses(paths));
        AppState.Current.Status = procs.Count == 0 ? "Not in use" : $"In use by {procs.Count} program(s)";
        if (procs.Count == 0)
            Dialogs.Info("No running program is using this right now.\n\nChecked: programs started from this location, and open files (Windows Restart Manager — the API installers use).",
                "Not in use");
        else
            Dialogs.Info("Close these programs before deleting or moving it:\n\n" +
                         Dialogs.Bullets(procs.Select(p => $"{p.Name} (PID {p.Id}) — {p.Reason}")), "In use");
    }

    /// <summary>Moves files/folders to the Recycle Bin after checking they aren't protected or in use.</summary>
    public static async Task<bool> RecycleAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) { Dialogs.Info("Select one or more files or folders in the list first."); return false; }

        var protectedPaths = paths.Where(p => SafetyRules.Classify(p).Level == SafetyLevel.Keep).ToList();
        if (protectedPaths.Count > 0)
        {
            Dialogs.Info("DiskTrim won't move these, because Windows or installed programs need them:\n\n" + Dialogs.Bullets(protectedPaths), "Protected");
            return false;
        }

        long bytes = paths.Sum(SizeOf);
        string text = $"Move {paths.Count} item(s), {Format.Bytes(bytes)} to the Recycle Bin?\n\n" + Dialogs.Bullets(paths, 8) +
                      "\n\nThe Recycle Bin is on the same drive — the space is freed when you empty it (Cleanup page).";
        if (!Dialogs.Confirm("Move to Recycle Bin?", text)) return false;

        using var op = AppState.Current.Begin("Checking that no running program uses it…");
        if (op is null) return false;

        var inUse = await Task.Run(() => ProcessInspector.WhoUses(paths, 1000));
        if (inUse.Count > 0)
        {
            Dialogs.Info("Nothing was changed. Close these programs and try again:\n\n" +
                         Dialogs.Bullets(inUse.Select(p => $"{p.Name} (PID {p.Id}) — {p.Reason}")), "In use right now");
            AppState.Current.Status = "Nothing was changed — in use.";
            return false;
        }

        AppState.Current.Status = "Moving to Recycle Bin…";
        var (gone, logFile) = await Task.Run(() =>
        {
            using var log = new AuditLog("recycle");
            var moved = new List<string>();
            foreach (var batch in paths.Chunk(50))
            {
                int rc = Native.MoveToRecycleBin(batch);
                foreach (var p in batch)
                {
                    bool ok = !File.Exists(p) && !Directory.Exists(p);
                    if (ok) moved.Add(p);
                    log.Write("explorer", "recycle", p, SizeOf(p), ok ? "Moved to Recycle Bin" : $"Could not move to Recycle Bin (code {rc})");
                }
            }
            return (moved, log.FilePath);
        });

        AppState.Current.Removed(gone);
        int failed = paths.Count - gone.Count;
        AppState.Current.Status = $"Moved {gone.Count} item(s) to the Recycle Bin" +
                                  (failed > 0 ? $" · {failed} item(s) in use or protected were left alone" : "") +
                                  " · logged in Settings > Deletion logs";
        if (failed > 0) Dialogs.Info($"{failed} item(s) in use or protected were left alone. Details: {logFile}");
        return gone.Count > 0;
    }

    /// <summary>
    /// Moves files/folders to a folder the user picks (normally another drive). Each file is copied, verified
    /// byte-for-byte (SHA-256) and only then removed; if anything fails the original is kept.
    /// </summary>
    public static async Task<bool> MoveAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) { Dialogs.Info("Select one or more files or folders in the list first."); return false; }
        var protectedPaths = paths.Where(p => SafetyRules.Classify(p).Level == SafetyLevel.Keep).ToList();
        if (protectedPaths.Count > 0)
        {
            Dialogs.Info("DiskTrim won't move these, because Windows or installed programs need them:\n\n" + Dialogs.Bullets(protectedPaths), "Protected");
            return false;
        }

        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Move to which folder? (pick a folder on another drive to free space)" };
        if (dlg.ShowDialog(Application.Current.MainWindow) != true) return false;
        var destination = dlg.FolderName;

        var state = AppState.Current;
        long bytes;
        using (var measuring = state.Begin("Measuring…"))
        {
            if (measuring is null) return false;
            bytes = await Task.Run(() => FileMover.MeasureBytes(paths));
        }

        if (FileMover.Validate(paths, destination, bytes) is { } problem)
        {
            Dialogs.Info(problem, problem.Contains("free but") ? "Not enough space" : "Can't move there");
            return false;
        }
        if (FileMover.IsSameDrive(paths, destination) &&
            !Dialogs.Confirm("Same drive", $"{destination} is on the same drive ({Path.GetPathRoot(destination)}), so moving won't free any space. Move anyway?", warning: true))
            return false;

        if (!Dialogs.Confirm("Move files?",
                $"Move {paths.Count} item(s), {Format.Bytes(bytes)}, to {destination}?\n\n{Dialogs.Bullets(paths, 8)}\n\n" +
                "Each file is copied, verified byte-for-byte (SHA-256) and only then removed from the original location. If anything fails, the original is kept."))
            return false;

        using var op = state.Begin("Checking that no running program uses it…");
        if (op is null) return false;
        var inUse = await Task.Run(() => ProcessInspector.WhoUses(paths, 1000));
        if (inUse.Count > 0)
        {
            Dialogs.Info("Nothing was changed. Close these programs and try again:\n\n" +
                         Dialogs.Bullets(inUse.Select(p => $"{p.Name} (PID {p.Id}) — {p.Reason}")), "In use right now");
            state.Status = "Nothing was changed — in use.";
            return false;
        }

        var dispatcher = Application.Current.Dispatcher;
        var (result, logFile) = await Task.Run(() =>
        {
            using var log = new AuditLog("move");
            var r = FileMover.Move(paths, destination, log, s => dispatcher.BeginInvoke(() => state.Status = s), CancellationToken.None);
            return (r, log.FilePath);
        });

        // Files first, then whole folders: the tree's sizes stay right either way.
        state.Removed([.. result.MovedFiles, .. result.MovedFolders]);
        state.Status = $"Moved {result.Moved} file(s), {Format.Bytes(result.Bytes)}" +
                       (result.Failed > 0 ? $" · {result.Failed} failed (originals kept)" : "") + " · logged in Settings > Deletion logs";
        if (result.Failed > 0)
            Dialogs.Info($"Moved {result.Moved} file(s). {result.Failed} could not be moved — their originals were kept:\n\n" +
                         Dialogs.Bullets(result.Errors, 8) + $"\n\nDetails: {logFile}", "Move");
        else
            Dialogs.Info($"Moved {result.Moved} file(s), {Format.Bytes(result.Bytes)}, to {destination}. Every copy was verified.", "Move");
        return result.Moved > 0;
    }

    /// <summary>Asks where to save, writes the HTML report of the current scan and opens it.</summary>
    public static async Task SaveReportAsync(Window owner)
    {
        var state = AppState.Current;
        if (state.Scan is not { } scan) { Dialogs.Info("Scan a drive first — the report lists the biggest folders and files of the scan."); return; }
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "HTML report|*.html",
            FileName = $"DiskTrim-{DateTime.Now:yyyyMMdd-HHmm}.html",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        };
        if (dlg.ShowDialog(owner) != true) return;

        using var op = state.Begin("Writing report…");
        if (op is null) return;
        var items = state.CleanupItems ?? await state.MeasureAsync();
        await Task.Run(() => ReportWriter.Write(dlg.FileName, scan, items));
        state.Status = "Report saved: " + dlg.FileName;
        Open(dlg.FileName);
    }

    static long SizeOf(string path)
    {
        if (AppState.Current.Scan is { } scan)
        {
            if (scan.Find(path) is { } dir) return dir.Size;
            var parent = scan.Find(Path.GetDirectoryName(path) ?? "");
            var name = Path.GetFileName(path);
            if (parent?.Files.FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } f) return f.Size;
        }
        try { return File.Exists(path) ? new FileInfo(path).Length : 0; }
        catch { return 0; }
    }
}

/// <summary>The right-click menu used for every list of files and folders.</summary>
public static class ItemMenu
{
    public static ContextMenu Build(Func<IReadOnlyList<string>> selection, bool includeExplorerView = true, bool includeTreemap = true)
    {
        var menu = new ContextMenu();
        Add(menu, "Open in File Explorer", s => FileActions.ShowInExplorer(s[0]));
        if (includeExplorerView) Add(menu, "Show in DiskTrim Explorer", s => AppState.Current.Navigate("explorer", s[0]));
        if (includeTreemap) Add(menu, "Show in Treemap", s => AppState.Current.Navigate("treemap", s[0]));
        Add(menu, "Copy path", FileActions.CopyPaths);
        Add(menu, "Is it in use?", s => _ = FileActions.CheckInUseAsync(s));
        menu.Items.Add(new Separator());
        Add(menu, "Move to another drive…", s => _ = FileActions.MoveAsync(s));
        Add(menu, "Move to Recycle Bin", s => _ = FileActions.RecycleAsync(s));
        return menu;

        void Add(ContextMenu m, string header, Action<IReadOnlyList<string>> action)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) =>
            {
                var s = selection();
                if (s.Count == 0) Dialogs.Info("Select one or more files or folders in the list first.");
                else action(s);
            };
            m.Items.Add(item);
        }
    }
}

/// <summary>Confirms and runs cleanup items (Home and Cleanup pages).</summary>
public static class CleanupRunner
{
    public static async Task<bool> RunAsync(IReadOnlyList<CleanupItem> selected)
    {
        var runnable = selected.Where(i => !i.IsBlocked).ToList();
        if (runnable.Count == 0)
        {
            Dialogs.Info("Tick at least one item that isn't blocked (blocked items need administrator rights or an app to be closed).");
            return false;
        }

        var text = $"Clean {runnable.Count} item(s), about {Format.Bytes(runnable.Sum(i => i.Bytes))}?\n\n" +
                   string.Join("\n", runnable.Select(i => $"• {i.Title}\n   If you clean it: {i.Impact}")) +
                   "\n\nFiles in use are skipped automatically. Every deleted file is logged.";
        if (!Dialogs.Confirm("Clean now?", text)) return false;

        using var op = AppState.Current.Begin("Cleaning…");
        if (op is null) return false;

        var state = AppState.Current;
        var (freed, failures, logFile) = await Task.Run(() =>
        {
            using var log = new AuditLog();
            long total = 0;
            var failed = new List<string>();
            foreach (var item in runnable)
            {
                void Report(string s) => Application.Current.Dispatcher.BeginInvoke(() => state.Status = $"{item.Title}: {s}");
                Report("Running…");
                var r = CleanupEngine.Clean(item, log, Report, CancellationToken.None);
                total += r.FreedBytes;
                if (r.Failed) failed.Add($"{item.Title}: {r.Message}");
            }
            return (total, failed, log.HasEntries ? log.FilePath : null);
        });

        state.Status = $"Cleanup finished — freed {Format.Bytes(freed)}." + (logFile is null ? "" : $" Every deleted file is listed in {logFile}");
        var message = $"Cleanup finished — freed {Format.Bytes(freed)}.";
        if (failures.Count > 0) message += "\n\nNot done:\n" + Dialogs.Bullets(failures);
        Dialogs.Info(message, "Cleanup");
        op.Dispose();
        await state.MeasureAsync();
        return true;
    }
}
