namespace DiskTrim.Core;

public sealed class FileEntry(DirNode dir, string name, long size, DateTime modified, FileAttributes attributes)
{
    public DirNode Dir { get; } = dir;
    public string Name { get; } = name;
    /// <summary>Bytes on disk; 0 for online-only cloud files.</summary>
    public long Size { get; } = size;
    public DateTime Modified { get; } = modified;
    public FileAttributes Attributes { get; } = attributes;

    public string FullPath => Path.Combine(Dir.FullPath, Name);

    public string Extension
    {
        get
        {
            var e = Path.GetExtension(Name);
            return e.Length > 1 ? e[1..].ToLowerInvariant() : "";
        }
    }

    public bool IsOnlineOnly => Scanner.IsOnlineOnly(Attributes);
}

public sealed class DirNode(string fullPath, string name, DirNode? parent)
{
    public string FullPath { get; } = fullPath;
    public string Name { get; } = name;
    public DirNode? Parent { get; } = parent;
    public List<DirNode> Children { get; } = [];
    public List<FileEntry> Files { get; } = [];

    public long Size { get; internal set; }
    public long FileCount { get; internal set; }
    public long FolderCount { get; internal set; }
    public DateTime LastChange { get; internal set; }
    public bool Unreadable { get; internal set; }
    /// <summary>Junction or symbolic link — shown but never followed.</summary>
    public bool IsLink { get; internal set; }

    public int Depth => Parent is null ? 0 : Parent.Depth + 1;
}

/// <summary>Live counters a UI can poll while a scan runs.</summary>
public sealed class ScanProgress
{
    public long Files;
    public long Folders;
    public long Bytes;
    public volatile string Current = "";
}

public sealed class ScanResult
{
    public required DirNode Root { get; init; }
    public required List<FileEntry> Files { get; init; }
    public int UnreadableFolders { get; init; }
    public bool Cancelled { get; init; }
    public DateTime Time { get; init; }
    public TimeSpan Duration { get; init; }

    public long TotalBytes => Root.Size;

    /// <summary>Finds the scanned folder for a path, or null when it is outside this scan.</summary>
    public DirNode? Find(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { path = Scanner.NormalizeRoot(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        var root = Root.FullPath;
        if (path.Equals(root, StringComparison.OrdinalIgnoreCase)) return Root;
        var prefix = root.EndsWith('\\') ? root : root + "\\";
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;

        var node = Root;
        foreach (var part in path[prefix.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            node = node.Children.FirstOrDefault(c => c.Name.Equals(part, StringComparison.OrdinalIgnoreCase));
            if (node is null) return null;
        }
        return node;
    }

    /// <summary>Takes deleted or moved files/folders out of the results and corrects every parent's size.</summary>
    public void RemovePaths(IEnumerable<string> paths)
    {
        var removed = new HashSet<FileEntry>();
        foreach (var path in paths)
        {
            var parent = Find(Path.GetDirectoryName(path) ?? "");
            if (parent is null) continue;
            var name = Path.GetFileName(path);

            long bytes, files, folders = 0;
            if (parent.Files.FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } file)
            {
                parent.Files.Remove(file);
                removed.Add(file);
                (bytes, files) = (file.Size, 1);
            }
            else if (parent.Children.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } dir)
            {
                parent.Children.Remove(dir);
                CollectFiles(dir, removed);
                (bytes, files, folders) = (dir.Size, dir.FileCount, dir.FolderCount + 1);
            }
            else continue;

            for (var n = parent; n is not null; n = n.Parent)
            {
                n.Size -= bytes;
                n.FileCount -= files;
                n.FolderCount -= folders;
            }
        }
        if (removed.Count > 0) Files.RemoveAll(removed.Contains);
    }

    static void CollectFiles(DirNode node, HashSet<FileEntry> into)
    {
        foreach (var f in node.Files) into.Add(f);
        foreach (var c in node.Children) CollectFiles(c, into);
    }
}

public static class Scanner
{
    const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;
    const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;

    static readonly EnumerationOptions Options = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = false,
        AttributesToSkip = 0,
        ReturnSpecialDirectories = false,
    };

    public static bool IsOnlineOnly(FileAttributes a) =>
        (a & (RecallOnDataAccess | RecallOnOpen | FileAttributes.Offline)) != 0;

    /// <summary>Turns user input ("c:", "C:\", "\"C:\\\"", "folder\..") into a clean full path.</summary>
    public static string NormalizeRoot(string path)
    {
        // Explorer passes a drive as "C:\" which the command line turns into C:" — repair that.
        path = path.Trim().Trim('"');
        if (path.Length == 2 && path[1] == ':') path += "\\";
        var full = Path.GetFullPath(path);
        return full.Length > 3 ? full.TrimEnd('\\') : full;
    }

    public static ScanResult Scan(string root, IReadOnlyList<string> exclusions, ScanProgress progress, CancellationToken ct)
    {
        root = NormalizeRoot(root);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("Folder not found: " + root);

        var sw = Stopwatch.StartNew();
        var ctx = new Context(exclusions.Select(NormalizeRoot).ToArray(), progress, ct);
        var rootNode = new DirNode(root, root, null);
        ScanDir(rootNode, 0, ctx);

        var files = new List<FileEntry>((int)Math.Min(rootNode.FileCount, int.MaxValue));
        Collect(rootNode, files);

        return new ScanResult
        {
            Root = rootNode,
            Files = files,
            UnreadableFolders = ctx.Unreadable,
            Cancelled = ct.IsCancellationRequested,
            Time = DateTime.Now,
            Duration = sw.Elapsed,
        };
    }

    sealed class Context(string[] exclusions, ScanProgress progress, CancellationToken ct)
    {
        public readonly ScanProgress Progress = progress;
        public readonly CancellationToken Ct = ct;
        public int Unreadable;

        public bool IsExcluded(string path) => exclusions.Any(e =>
            path.Equals(e, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(e.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));
    }

    static void ScanDir(DirNode node, int depth, Context ctx)
    {
        if (ctx.Ct.IsCancellationRequested) return;
        ctx.Progress.Current = node.FullPath;

        var subdirs = new List<DirNode>();
        long bytes = 0;
        DateTime last = DateTime.MinValue;

        try
        {
            foreach (var info in new DirectoryInfo(node.FullPath).EnumerateFileSystemInfos("*", Options))
            {
                if (info is DirectoryInfo)
                {
                    var child = new DirNode(info.FullName, info.Name, node);
                    if (ctx.IsExcluded(child.FullPath)) continue;
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0) child.IsLink = true;
                    subdirs.Add(child);
                }
                else if (info is FileInfo fi)
                {
                    long size = IsOnlineOnly(fi.Attributes) ? 0 : fi.Length;
                    var entry = new FileEntry(node, fi.Name, size, fi.LastWriteTime, fi.Attributes);
                    node.Files.Add(entry);
                    bytes += size;
                    if (entry.Modified > last) last = entry.Modified;
                    Interlocked.Increment(ref ctx.Progress.Files);
                    Interlocked.Add(ref ctx.Progress.Bytes, size);
                }
            }
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            node.Unreadable = true;
            Interlocked.Increment(ref ctx.Unreadable);
        }

        Interlocked.Add(ref ctx.Progress.Folders, subdirs.Count);
        node.Children.AddRange(subdirs);

        // Links (junctions, symlinks) are listed but never followed: they would double-count or loop.
        var toScan = subdirs.Where(d => !d.IsLink).ToList();
        if (depth < 3 && toScan.Count > 1)
            Parallel.ForEach(toScan, c => ScanDir(c, depth + 1, ctx));
        else
            foreach (var c in toScan) ScanDir(c, depth + 1, ctx);

        long fileCount = node.Files.Count, folderCount = node.Children.Count;
        foreach (var c in node.Children)
        {
            bytes += c.Size;
            fileCount += c.FileCount;
            folderCount += c.FolderCount;
            if (c.LastChange > last) last = c.LastChange;
        }
        node.Size = bytes;
        node.FileCount = fileCount;
        node.FolderCount = folderCount;
        node.LastChange = last;
        node.Children.Sort((a, b) => b.Size.CompareTo(a.Size));
        node.Files.Sort((a, b) => b.Size.CompareTo(a.Size));
    }

    static void Collect(DirNode node, List<FileEntry> into)
    {
        into.AddRange(node.Files);
        foreach (var c in node.Children) Collect(c, into);
    }
}
