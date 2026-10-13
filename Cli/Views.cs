using static System.ConsoleColor;

namespace DiskTrim.Cli;

/// <summary>What each "page" prints. Shared by the command line and the interactive menu.</summary>
static class Views
{
    // ---- Home ------------------------------------------------------------------------------------

    public static void Drives()
    {
        Ui.Title("Drives");
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady || d.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
                long total = d.TotalSize, free = d.AvailableFreeSpace, used = total - free;
                double f = total == 0 ? 0 : (double)used / total;
                string label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? (d.DriveType == DriveType.Removable ? "Removable" : "Local disk") : d.VolumeLabel;
                Ui.Write($"  {d.Name,-4} {Ui.Fit(label, 14)} ");
                Ui.Write(Ui.Bar(f, 30), f > 0.9 ? Red : f > 0.75 ? Yellow : Cyan);
                Ui.Line($"  {Format.Percent(f),7} used · {Format.Bytes(free)} free of {Format.Bytes(total)}");
            }
            catch (IOException) { /* drive went away */ }
        }
    }

    public static void Recommendations(IReadOnlyList<Recommendation> recs)
    {
        Ui.Title("Recommendations — biggest wins first");
        if (recs.Count == 0) { Ui.Ok("Nothing to recommend — your drive looks tidy."); return; }
        int n = 1;
        foreach (var r in recs)
        {
            Ui.Write($"  {n++,2}. ");
            Ui.Level(r.Level);
            Ui.Write(Ui.Fit(r.Title, Math.Max(20, Ui.Width - 30)), White);
            Ui.Line(r.Bytes > 0 ? Format.Bytes(r.Bytes).PadLeft(10) : "");
            Ui.Wrapped(r.Detail, 16);
            if (r.Path is not null) Ui.Muted(new string(' ', 16) + r.Path);
        }
        if (Session.LastScan is null)
            Ui.Muted("Scan the drive for more recommendations (big folders, photos to move, old files, duplicates).");
    }

    // ---- Explorer / tree -------------------------------------------------------------------------

    /// <summary>Biggest sub-folders (and files) as an indented tree.</summary>
    public static void Tree(ScanResult scan, DirNode node, int depth, int top)
    {
        Ui.Title($"{node.FullPath}  ·  {Format.Bytes(node.Size)}  ·  {Format.Count(node.FileCount)} files  ·  {Format.Count(node.FolderCount)} folders");
        Ui.Muted($"  {"Size",10}  {"Share",-21}  {"Safety",-8}Name");
        PrintChildren(node, node.Size, 0, depth, top);
        if (scan.Cancelled) Ui.Warn("Scan was stopped — results are partial.");
        Ui.Legend();
    }

    static void PrintChildren(DirNode node, long total, int level, int maxDepth, int top)
    {
        foreach (var e in Entries(node, top))
        {
            PrintEntry(e, total, level);
            if (e.Dir is { } d && level + 1 < maxDepth && !d.IsLink && d.Size > 0)
                PrintChildren(d, total, level + 1, maxDepth, Math.Max(3, top / 2));
        }
    }

    public readonly record struct Entry(DirNode? Dir, FileEntry? File)
    {
        public long Size => Dir?.Size ?? File!.Size;
        public string Path => Dir?.FullPath ?? File!.FullPath;
        public string Name => Dir is not null ? Dir.Name + "\\" : File!.Name;
    }

    /// <summary>Folders and files of one folder, biggest first.</summary>
    public static List<Entry> Entries(DirNode node, int top) =>
        node.Children.Take(top).Select(d => new Entry(d, null))
            .Concat(node.Files.Take(top).Select(f => new Entry(null, f)))
            .OrderByDescending(e => e.Size)
            .Take(top)
            .ToList();

    public static void PrintEntry(Entry e, long total, int level, int? number = null)
    {
        double share = total == 0 ? 0 : (double)e.Size / total;
        var verdict = SafetyRules.Classify(e.Path);
        string indent = new(' ', level * 2);
        string prefix = number is { } n ? $"{n,4} " : "";

        Ui.Write($"{prefix}  {Format.Bytes(e.Size),10}  ");
        Ui.Write(Ui.Bar(share, 12), DarkCyan);
        Ui.Write($" {Format.Percent(share),7}  ");
        Ui.Level(verdict.Level);

        string name = indent + e.Name;
        string note = "";
        if (e.Dir is { IsLink: true }) note = "  (link — not followed)";
        else if (e.Dir is { Unreadable: true }) note = "  (no access)";
        else if (e.File is { IsOnlineOnly: true }) note = "  (online-only, uses no space)";
        if (verdict != SafetyRules.Unrecognised && e.Dir is not null) note += $"  · {verdict.Title}";

        int room = Math.Max(20, Ui.Width - prefix.Length - 46);
        if (name.Length + note.Length <= room)
        {
            Ui.Write(name, e.Dir is not null ? White : Gray);
            Ui.Line(note, DarkGray);
        }
        else Ui.Line(Ui.Fit(name + note, room), e.Dir is not null ? White : Gray);
    }

    public static void Details(string path)
    {
        var v = SafetyRules.Classify(path);
        Ui.Line();
        Ui.Line(path, White);
        Ui.Write("  ");
        Ui.Write(SafetyVerdict.LevelText(v.Level), Ui.LevelColor(v.Level));
        Ui.Line($"  ·  {v.Title}");
        Ui.Wrapped(v.Reason, 2, Gray);
        Ui.Wrapped("If you delete it: " + v.Impact, 2);
    }

    // ---- Large & old files -----------------------------------------------------------------------

    public static void Files(ScanResult scan, long minSize, TimeSpan? olderThan, string? category, int top, bool hideKeep)
    {
        var cutoff = olderThan is { } age ? DateTime.Now - age : DateTime.MaxValue;
        var matches = scan.Files
            .Where(f => f.Size >= minSize && f.Modified <= cutoff)
            .Where(f => category is null || FileCategories.Of(f.Extension).Equals(category, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f.Size)
            .ToList();

        var title = "Large & old files";
        if (minSize > 0) title += $" · ≥ {Format.Bytes(minSize)}";
        if (olderThan is { } o) title += $" · not modified for {o.TotalDays:0} days";
        if (category is not null) title += $" · {category}";
        Ui.Title(title);

        if (matches.Count == 0) { Ui.Muted("No files match these filters."); return; }

        Ui.Muted($"  {"Size",10}  {"Modified",-10}  {"Safety",-8}{"Type",-18}Path");
        int shown = 0;
        foreach (var f in matches)
        {
            var v = SafetyRules.Classify(f.FullPath);
            if (hideKeep && v.Level == SafetyLevel.Keep) continue;
            Ui.Write($"  {Format.Bytes(f.Size),10}  {Format.Date(f.Modified),-10}  ");
            Ui.Level(v.Level);
            Ui.Write(Ui.Fit(FileCategories.Of(f.Extension), 17) + " ", DarkGray);
            Ui.Line(Ui.Fit(f.FullPath, Math.Max(20, Ui.Width - 54)) + (f.IsOnlineOnly ? "  (online-only)" : ""));
            if (++shown >= top) break;
        }
        Ui.Muted($"{Format.Count(matches.Count)} files match · {Format.Bytes(matches.Sum(f => f.Size))} · showing the largest {shown}");
    }

    // ---- File types ------------------------------------------------------------------------------

    public static void Types(ScanResult scan, int topExtensions)
    {
        var byCat = scan.Files.GroupBy(f => FileCategories.Of(f.Extension))
            .Select(g => (Name: g.Key, Count: g.LongCount(), Bytes: g.Sum(f => f.Size)))
            .OrderByDescending(c => c.Bytes).ToList();
        long total = Math.Max(1, scan.TotalBytes);

        Ui.Title($"File types in {scan.Root.FullPath}");
        foreach (var c in byCat)
        {
            double share = (double)c.Bytes / total;
            Ui.Write($"  {Ui.Fit(c.Name, 18)} {Format.Bytes(c.Bytes),10}  ");
            Ui.Write(Ui.Bar(share, 25), Cyan);
            Ui.Line($" {Format.Percent(share),7}  {Format.Count(c.Count),10} files", Gray);
        }

        var byExt = scan.Files.GroupBy(f => f.Extension)
            .Select(g => (Ext: g.Key, Count: g.LongCount(), Bytes: g.Sum(f => f.Size)))
            .OrderByDescending(e => e.Bytes).ToList();
        Ui.Title($"Top {Math.Min(topExtensions, byExt.Count)} of {Format.Count(byExt.Count)} extensions");
        foreach (var e in byExt.Take(topExtensions))
        {
            double share = (double)e.Bytes / total;
            string ext = e.Ext.Length == 0 ? "(none)" : "." + e.Ext;
            Ui.Line($"  {Ui.Fit(ext, 14)} {Ui.Fit(FileCategories.Of(e.Ext), 18)} {Format.Bytes(e.Bytes),10}  {Format.Percent(share),7}  {Format.Count(e.Count),10} files");
        }
    }

    // ---- Duplicates ------------------------------------------------------------------------------

    public static void Duplicates(IReadOnlyList<DuplicateGroup> groups, KeepRule keep, int top)
    {
        Ui.Title("Duplicates");
        if (groups.Count == 0) { Ui.Ok("No duplicate files found."); return; }
        Ui.Line($"Found {Format.Count(groups.Count)} groups of duplicates — {Format.Bytes(groups.Sum(g => g.Wasted))} could be freed.", White);
        Ui.Muted($"One copy of each file is always kept ({KeepText(keep)}).");

        int n = 1;
        foreach (var g in groups.Take(top))
        {
            Ui.Line();
            Ui.Line($"  {n++,3}. {Format.Bytes(g.Size)} × {g.Files.Count} copies  ·  {Format.Bytes(g.Wasted)} wasted", Yellow);
            var keeper = g.Keeper(keep);
            foreach (var f in g.Files.OrderBy(f => f != keeper).ThenBy(f => f.FullPath))
            {
                Ui.Write(f == keeper ? "       keep  " : "       copy  ", f == keeper ? Green : DarkGray);
                Ui.Line($"{Format.Date(f.Modified)}  {Ui.Fit(f.FullPath, Math.Max(20, Ui.Width - 26))}");
            }
        }
        if (groups.Count > top) Ui.Muted($"… and {groups.Count - top} more groups (use --top to show more)");
    }

    public static string KeepText(KeepRule rule) => rule switch
    {
        KeepRule.Newest => "newest copy",
        KeepRule.ShortestPath => "copy with the shortest path",
        _ => "oldest copy, the original",
    };

    // ---- Cleanup ---------------------------------------------------------------------------------

    public static void Cleanup(IReadOnlyList<CleanupItem> items)
    {
        Ui.Title("Cleanup — temporary files & caches");
        int n = 1;
        foreach (var group in items.GroupBy(i => i.Group))
        {
            Ui.Line();
            Ui.Line("  " + group.Key, Cyan);
            foreach (var i in group)
            {
                Ui.Write($"  {n++,3}  ");
                Ui.Level(i.Level);
                Ui.Write(Ui.Fit(i.Title, 46), White);
                Ui.Write($" {i.SizeHint ?? Format.Bytes(i.Bytes),19}  ");
                Ui.Line(i.Status + (i.NeedsAdmin && AppPaths.IsAdmin ? " · administrator" : ""),
                    i.IsBlocked ? Yellow : i.Status == "Ready" ? Green : DarkGray);
                Ui.Muted($"{"",15}{i.Id}");
            }
        }

        long safe = items.Where(i => i.Level == SafetyLevel.Safe && !i.IsBlocked).Sum(i => i.Bytes);
        Ui.Line();
        Ui.Line($"Safe to free right now: {Format.Bytes(safe)}", Green);
        if (items.Any(i => i.BlockedReason == "Needs administrator"))
            Ui.Muted("Some items need administrator rights — run DiskTrim as administrator to include them.");
        if (items.Any(i => i.BlockedReason?.StartsWith("Close ", StringComparison.Ordinal) == true))
            Ui.Muted("Blocked items need an app to be closed first — measure again after closing it.");
        Ui.Legend();
    }

    // ---- Installed apps --------------------------------------------------------------------------

    public static void Apps(IReadOnlyList<InstalledApp> apps, int top)
    {
        Ui.Title($"Installed apps — {apps.Count} programs · {Format.Bytes(apps.Sum(a => a.SizeBytes))} in total (as reported by the programs)");
        Ui.Muted($"  {"#",4}  {"Size",10}  {"Installed",-10}  {"Name",-40} {"Version",-16} Publisher");
        int n = 1;
        foreach (var a in apps.Take(top))
        {
            Ui.Write($"  {n++,4}  {(a.SizeBytes > 0 ? Format.Bytes(a.SizeBytes) : "—"),10}  {(a.InstalledOn is { } d ? Format.Date(d) : ""),-10}  ");
            Ui.Write(Ui.Fit(a.Name, 40) + " ", White);
            Ui.Line($"{Ui.Fit(a.Version, 16)} {Ui.Fit(a.Publisher, Math.Max(10, Ui.Width - 92))}", Gray);
        }
        if (apps.Count > top) Ui.Muted($"… and {apps.Count - top} more (use --top)");
        Ui.Muted("Uninstalling with the program's own uninstaller is the only safe way to remove a program.");
    }

    // ---- History ---------------------------------------------------------------------------------

    public static void Snapshots(IReadOnlyList<Snapshot> snaps)
    {
        Ui.Title("History — saved scans");
        if (snaps.Count == 0) { Ui.Muted("No scans saved yet. Scan a drive — each finished scan is remembered."); return; }
        foreach (var s in snaps)
            Ui.Line($"  {Format.Stamp(s.Time)}  {Ui.Fit(s.Root, 30)} {Format.Bytes(s.TotalBytes),10} scanned · {Format.Count(s.Files),12} files · {Format.Bytes(s.FreeBytes),10} free on drive");
    }

    public static void Changes(Snapshot before, Snapshot after, IReadOnlyList<FolderChange> changes, int top)
    {
        long delta = after.TotalBytes - before.TotalBytes;
        Ui.Title($"What changed in {after.Root}: {Format.Stamp(before.Time)} → {Format.Stamp(after.Time)}");
        Ui.Line($"Total {(delta >= 0 ? "grew" : "shrank")} by {Format.Bytes(Math.Abs(delta))}  ({Format.Bytes(before.TotalBytes)} → {Format.Bytes(after.TotalBytes)})",
            delta > 0 ? Yellow : Green);
        if (changes.Count == 0) { Ui.Muted("No folder changed by more than the threshold."); return; }
        Ui.Muted("Folders are listed where the change actually happened.");
        Ui.Muted($"  {"Change",11}  {"Before",10}  {"After",10}  Folder");
        foreach (var c in changes.Take(top))
        {
            Ui.Write($"  {(c.Delta > 0 ? "+" : "-") + Format.Bytes(Math.Abs(c.Delta)),11}", c.Delta > 0 ? Yellow : Green);
            Ui.Line($"  {Format.Bytes(c.Before),10}  {Format.Bytes(c.After),10}  {Ui.Fit(c.Path, Math.Max(20, Ui.Width - 40))}");
        }
    }

    // ---- In use ----------------------------------------------------------------------------------

    public static void InUse(string path, IReadOnlyList<RunningProcess> procs)
    {
        Ui.Title($"Is it in use?  {path}");
        if (procs.Count == 0)
        {
            Ui.Ok("Not in use — no running program is using this right now.");
            Ui.Muted("Checked: programs started from this location, and open files (Windows Restart Manager — the API installers use).");
            return;
        }
        Ui.Warn("In use — close these programs before deleting or moving it:");
        foreach (var p in procs) Ui.Line($"  • {p.Name} (PID {p.Id}) — {p.Reason}");
    }
}
