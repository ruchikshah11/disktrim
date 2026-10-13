namespace DiskTrim.Core;

public enum RecAction { CleanSafe, OpenCleanup, RestartAdmin, EmptyRecycleBin, TurnOffHibernation, ShowFile, ShowFolder, ShowFiles, FindDuplicates }

public sealed record Recommendation(string Title, string Detail, long Bytes, SafetyLevel Level, RecAction Action, string? Path = null);

/// <summary>The Home page: what would free the most space, biggest wins first.</summary>
public static class Recommendations
{
    const long GB = 1024L * 1024 * 1024;
    const long MB = 1024L * 1024;

    public static List<Recommendation> Build(IReadOnlyList<CleanupItem> items, ScanResult? scan)
    {
        var recs = new List<Recommendation>();

        var safe = items.Where(i => i.Level == SafetyLevel.Safe && !i.IsBlocked && i.Bytes > 0
                                    && i.Kind is CleanupKind.Files or CleanupKind.Command).ToList();
        if (safe.Count > 0)
            recs.Add(new("Clean temporary files and caches",
                $"{safe.Count} safe item(s): {string.Join(", ", safe.Select(i => i.Title))}. Rebuilt automatically — your files are not touched.",
                safe.Sum(i => i.Bytes), SafetyLevel.Safe, RecAction.CleanSafe));

        foreach (var i in items.Where(i => i.BlockedReason?.StartsWith("Close ", StringComparison.Ordinal) == true && i.Bytes >= 50 * MB))
            recs.Add(new($"Close {i.AppName} to clean its cache",
                $"{i.Title} can't be cleaned while {i.AppName} is running. Close it, then run the cleanup.",
                i.Bytes, i.Level, RecAction.OpenCleanup));

        var admin = items.Where(i => i.BlockedReason == "Needs administrator").ToList();
        if (admin.Count > 0)
            recs.Add(new("Restart as administrator for system cleanup",
                $"{admin.Count} item(s) need administrator rights: {string.Join(", ", admin.Select(i => i.Title))}.",
                admin.Sum(i => i.Bytes), SafetyLevel.Safe, RecAction.RestartAdmin));

        if (items.FirstOrDefault(i => i.Kind == CleanupKind.RecycleBin) is { Bytes: >= 100 * MB } bin)
            recs.Add(new("Empty the Recycle Bin",
                "Files you already deleted still use space until the Recycle Bin is emptied.",
                bin.Bytes, SafetyLevel.Caution, RecAction.EmptyRecycleBin));

        if (items.FirstOrDefault(i => i.Id == "hiberfil") is { Bytes: > 0 } hib)
            recs.Add(new("Turn off hibernation",
                "Removes hiberfil.sys. Sleep keeps working; Hibernate and Fast Startup stop.",
                hib.Bytes, SafetyLevel.Caution, RecAction.TurnOffHibernation));

        if (scan is not null) AddFromScan(scan, recs);

        return recs.OrderByDescending(r => r.Bytes).ToList();
    }

    static void AddFromScan(ScanResult scan, List<Recommendation> recs)
    {
        foreach (var ost in scan.Files.Where(f => f.Extension == "ost" && f.Size >= GB))
            recs.Add(new("Shrink Outlook's offline mailbox",
                $"{ost.Name} is a local copy of mail that's on the server. In Outlook > Account Settings set 'Download email for the past' to 6–12 months, close Outlook and delete the .ost — Outlook rebuilds a much smaller one.",
                ost.Size, SafetyLevel.Caution, RecAction.ShowFile, ost.FullPath));

        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var name in new[] { "Pictures", "Videos", "Desktop", "Documents", "Downloads" })
        {
            var folder = Path.Combine(profile, name);
            var node = scan.Find(folder);
            if (node is null) continue;
            long media = 0;
            SumMedia(node, ref media);
            if (media >= 5 * GB)
                recs.Add(new($"Move photos & videos in \"{name}\" to another drive",
                    $"{name} holds {Format.Bytes(media)} of photos/videos. Move them to an external drive or to OneDrive with 'Free up space'.",
                    media, SafetyLevel.Review, RecAction.ShowFolder, folder));
        }

        var yearAgo = DateTime.Now.AddYears(-1);
        var old = scan.Files.Where(f => f.Size >= 100 * MB && f.Modified < yearAgo)
                            .Where(f => SafetyRules.Classify(f.FullPath).Level == SafetyLevel.Review).ToList();
        long oldBytes = old.Sum(f => f.Size);
        if (oldBytes >= GB)
            recs.Add(new("Review large files you haven't touched in a year",
                $"{old.Count} big personal files not changed for over a year — old installers, exports, archives, videos.",
                oldBytes, SafetyLevel.Review, RecAction.ShowFiles));

        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var (title, path) in new[]
        {
            ("Android emulator", Path.Combine(profile, @".android\avd")),
            ("Android SDK", Path.Combine(local, @"Android\Sdk")),
            ("Docker data", Path.Combine(local, "Docker")),
            ("Downloads", Path.Combine(profile, "Downloads")),
        })
        {
            if (scan.Find(path) is { Size: >= 5 * GB } node)
            {
                var v = SafetyRules.Classify(path);
                recs.Add(new($"{title} uses {Format.Bytes(node.Size)}", v.Impact, node.Size, v.Level, RecAction.ShowFolder, path));
            }
        }

        recs.Add(new("Find duplicate files",
            "Identical copies of the same file (same content, any name). Keep one, recycle the rest.",
            0, SafetyLevel.Review, RecAction.FindDuplicates));
    }

    static void SumMedia(DirNode node, ref long total)
    {
        foreach (var f in node.Files)
            if (FileCategories.IsMedia(FileCategories.Of(f.Extension))) total += f.Size;
        foreach (var c in node.Children) SumMedia(c, ref total);
    }
}
