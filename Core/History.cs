using System.Text.Json.Serialization;

namespace DiskTrim.Core;

public sealed class Snapshot
{
    public string Root { get; set; } = "";
    public DateTime Time { get; set; }
    public long TotalBytes { get; set; }
    public long Files { get; set; }
    public long FreeBytes { get; set; }
    /// <summary>Folder path → size, for folders big enough to matter.</summary>
    public Dictionary<string, long> Folders { get; set; } = [];

    [JsonIgnore] public string FilePath { get; set; } = "";
}

public sealed record FolderChange(string Path, long Before, long After)
{
    public long Delta => After - Before;
}

/// <summary>Remembers every finished scan so you can see what grew between two scans.</summary>
public static class History
{
    const int MaxDepth = 5;
    const long MinFolderBytes = 10L * 1024 * 1024;

    static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public static void Save(ScanResult scan)
    {
        if (scan.Cancelled) return;
        var snap = new Snapshot
        {
            Root = scan.Root.FullPath,
            Time = scan.Time,
            TotalBytes = scan.TotalBytes,
            Files = scan.Root.FileCount,
            FreeBytes = FreeSpace(scan.Root.FullPath),
        };
        AddFolders(scan.Root, snap.Folders);

        var name = $"{Sanitize(snap.Root)}-{snap.Time:yyyyMMdd-HHmmss-fff}.json";
        File.WriteAllText(Path.Combine(AppPaths.HistoryDir, name), JsonSerializer.Serialize(snap, Json));
    }

    public static List<Snapshot> List(string? root = null)
    {
        var list = new List<Snapshot>();
        foreach (var file in Directory.EnumerateFiles(AppPaths.HistoryDir, "*.json"))
        {
            try
            {
                var s = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(file), Json);
                if (s is null) continue;
                s.FilePath = file;
                if (root is null || s.Root.Equals(root, StringComparison.OrdinalIgnoreCase)) list.Add(s);
            }
            catch { /* skip unreadable snapshot */ }
        }
        return list.OrderByDescending(s => s.Time).ToList();
    }

    /// <summary>
    /// Folders whose size changed by at least <paramref name="threshold"/>, listed where the change actually
    /// happened: a folder is left out when one of its sub-folders explains most of its change.
    /// </summary>
    public static List<FolderChange> Compare(Snapshot before, Snapshot after, long threshold)
    {
        var b = new Dictionary<string, long>(before.Folders, StringComparer.OrdinalIgnoreCase);
        var a = new Dictionary<string, long>(after.Folders, StringComparer.OrdinalIgnoreCase);
        var changes = b.Keys.Union(a.Keys, StringComparer.OrdinalIgnoreCase)
            .Select(k => new FolderChange(k, b.GetValueOrDefault(k), a.GetValueOrDefault(k)))
            .Where(c => Math.Abs(c.Delta) >= threshold)
            .ToList();

        var byParent = changes.ToLookup(c => Path.GetDirectoryName(c.Path) ?? "", StringComparer.OrdinalIgnoreCase);
        return changes
            .Where(c => !byParent[c.Path].Any(child =>
                Math.Sign(child.Delta) == Math.Sign(c.Delta) && Math.Abs(child.Delta) * 2 >= Math.Abs(c.Delta)))
            .OrderByDescending(c => Math.Abs(c.Delta))
            .ToList();
    }

    static void AddFolders(DirNode node, Dictionary<string, long> into)
    {
        into[node.FullPath] = node.Size;
        if (node.Depth >= MaxDepth) return;
        foreach (var c in node.Children)
        {
            if (c.Size < MinFolderBytes) break; // children are sorted by size
            AddFolders(c, into);
        }
    }

    static long FreeSpace(string path)
    {
        try { return new DriveInfo(Path.GetPathRoot(path)!).AvailableFreeSpace; }
        catch { return 0; }
    }

    static string Sanitize(string path) =>
        Regex.Replace(path, @"[\\/:*?""<>|]+", "_").Trim('_');
}
