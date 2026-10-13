using System.Security.Cryptography;

namespace DiskTrim.Core;

public sealed class MoveResult
{
    public int Moved { get; set; }
    public int Failed { get; set; }
    public long Bytes { get; set; }
    /// <summary>Every source file that is gone from its old place (moved and verified).</summary>
    public List<string> MovedFiles { get; } = [];
    /// <summary>Selected folders that were moved completely.</summary>
    public List<string> MovedFolders { get; } = [];
    public List<string> Errors { get; } = [];
}

/// <summary>
/// Moves files to another folder (usually another drive) safely: every file is copied, the copy is verified
/// byte-for-byte with SHA-256, and only then is the original removed. If anything fails, the original is kept.
/// </summary>
public static class FileMover
{
    const int Buffer = 1 << 20;

    static readonly EnumerationOptions Opts = new() { RecurseSubdirectories = false, IgnoreInaccessible = true, AttributesToSkip = 0 };

    /// <summary>Returns why the move can't happen, or null when it can.</summary>
    public static string? Validate(IReadOnlyList<string> sources, string destination, long bytesNeeded)
    {
        if (sources.Count == 0) return "Select one or more files or folders first.";
        if (!Directory.Exists(destination)) return "Folder not found: " + destination;
        var dest = Scanner.NormalizeRoot(destination).TrimEnd('\\') + "\\";
        foreach (var s in sources)
        {
            var src = Scanner.NormalizeRoot(s).TrimEnd('\\') + "\\";
            if (dest.StartsWith(src, StringComparison.OrdinalIgnoreCase))
                return "The destination is inside one of the items being moved.";
            if (Path.GetDirectoryName(s.TrimEnd('\\')) is { } parent &&
                Scanner.NormalizeRoot(parent).TrimEnd('\\').Equals(dest.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                return "The items are already in that folder.";
            if (SafetyRules.Classify(s).Level == SafetyLevel.Keep)
                return $"DiskTrim won't move {s}, because Windows or installed programs need it.";
        }
        long free = FreeSpace(destination);
        if (free >= 0 && free < bytesNeeded)
            return $"{Path.GetPathRoot(destination)} has {Format.Bytes(free)} free but {Format.Bytes(bytesNeeded)} is needed.";
        return null;
    }

    /// <summary>Moving within one drive doesn't free any space.</summary>
    public static bool IsSameDrive(IEnumerable<string> sources, string destination) =>
        sources.Any(s => string.Equals(Path.GetPathRoot(Path.GetFullPath(s)), Path.GetPathRoot(Path.GetFullPath(destination)),
            StringComparison.OrdinalIgnoreCase));

    /// <summary>Total bytes that would be copied (online-only cloud files are skipped, so they don't count).</summary>
    public static long MeasureBytes(IEnumerable<string> sources) =>
        sources.Sum(s => File.Exists(s) ? SizeOf(new FileInfo(s)) : Directory.Exists(s) ? Files(new DirectoryInfo(s)).Sum(SizeOf) : 0);

    public static MoveResult Move(IReadOnlyList<string> sources, string destination, AuditLog log, Action<string>? progress, CancellationToken ct)
    {
        var result = new MoveResult();
        foreach (var source in sources)
        {
            if (ct.IsCancellationRequested) break;
            if (File.Exists(source))
            {
                MoveOne(new FileInfo(source), Unique(Path.Combine(destination, Path.GetFileName(source))), log, result, progress);
            }
            else if (Directory.Exists(source))
            {
                var root = new DirectoryInfo(source);
                var target = Unique(Path.Combine(destination, root.Name));
                int failedBefore = result.Failed;
                foreach (var file in Files(root))
                {
                    if (ct.IsCancellationRequested) break;
                    var rel = Path.GetRelativePath(root.FullName, file.FullName);
                    MoveOne(file, Path.Combine(target, rel), log, result, progress);
                }
                RemoveEmptyDirs(root);
                if (!Directory.Exists(source) && result.Failed == failedBefore) result.MovedFolders.Add(source);
            }
        }
        return result;
    }

    static void MoveOne(FileInfo source, string target, AuditLog log, MoveResult result, Action<string>? progress)
    {
        string what = $"{source.FullName} -> {target}";
        if (Scanner.IsOnlineOnly(source.Attributes))
        {
            Fail("online-only OneDrive file — skipped (moving it would download it first)");
            return;
        }
        // Read everything needed up front: after Delete() the FileInfo no longer knows them.
        long length = source.Length;
        var originalAttributes = source.Attributes;
        var lastWrite = source.LastWriteTimeUtc;
        var created = source.CreationTimeUtc;
        progress?.Invoke($"Moving {source.Name} ({Format.Bytes(length)})");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            byte[] sourceHash;
            using (var input = new FileStream(source.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, Buffer, FileOptions.SequentialScan))
            using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, Buffer))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buf = new byte[Buffer];
                int n;
                while ((n = input.Read(buf, 0, buf.Length)) > 0)
                {
                    hash.AppendData(buf, 0, n);
                    output.Write(buf, 0, n);
                }
                sourceHash = hash.GetHashAndReset();
            }

            byte[] copyHash;
            using (var check = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read, Buffer, FileOptions.SequentialScan))
                copyHash = SHA256.HashData(check);
            if (!copyHash.AsSpan().SequenceEqual(sourceHash))
            {
                TryDelete(target);
                Fail("Verification failed — the copy did not match; the original was kept.");
                return;
            }

            File.SetLastWriteTimeUtc(target, lastWrite);
            File.SetCreationTimeUtc(target, created);

            try
            {
                if ((source.Attributes & FileAttributes.ReadOnly) != 0) source.Attributes &= ~FileAttributes.ReadOnly;
                source.Delete();
                File.SetAttributes(target, originalAttributes & (FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.Archive));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The copy is good; keep both rather than lose anything.
                Fail("Copied and verified, but the original could not be removed: " + ex.Message);
                return;
            }

            result.Moved++;
            result.Bytes += length;
            result.MovedFiles.Add(source.FullName);
            log.Write("move", "move", what, length, "OK (verified)");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // CreateNew failing means a file appeared at the target meanwhile — never overwrite it.
            if (ex is not IOException { HResult: unchecked((int)0x80070050) }) TryDelete(target);
            Fail((ex is IOException && IsInUse(ex) ? "In use — skipped: " : "") + ex.Message);
        }

        void Fail(string reason)
        {
            result.Failed++;
            result.Errors.Add($"{source.FullName}: {reason}");
            log.Write("move", "move", what, 0, reason);
        }
    }

    // ---- helpers ---------------------------------------------------------------------------------

    /// <summary>Files below a folder; links are never followed.</summary>
    static IEnumerable<FileInfo> Files(DirectoryInfo root)
    {
        var stack = new Stack<DirectoryInfo>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            FileInfo[] files;
            DirectoryInfo[] subs;
            try
            {
                files = dir.GetFiles("*", Opts);
                subs = dir.GetDirectories("*", Opts);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            foreach (var f in files)
                if ((f.Attributes & FileAttributes.ReparsePoint) == 0 || Scanner.IsOnlineOnly(f.Attributes)) yield return f;
            foreach (var s in subs)
                if ((s.Attributes & FileAttributes.ReparsePoint) == 0) stack.Push(s);
        }
    }

    static void RemoveEmptyDirs(DirectoryInfo dir)
    {
        DirectoryInfo[] subs;
        try { subs = dir.GetDirectories("*", Opts); }
        catch { return; }
        foreach (var s in subs)
            if ((s.Attributes & FileAttributes.ReparsePoint) == 0) RemoveEmptyDirs(s);
        try { if (!dir.EnumerateFileSystemInfos("*", Opts).Any()) dir.Delete(); }
        catch { /* in use or not empty: leave it */ }
    }

    /// <summary>"photo.jpg" → "photo (2).jpg" when the name is taken. Never overwrites.</summary>
    public static string Unique(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
    }

    static long SizeOf(FileInfo f)
    {
        try { return Scanner.IsOnlineOnly(f.Attributes) ? 0 : f.Length; }
        catch { return 0; }
    }

    static long FreeSpace(string path)
    {
        try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace; }
        catch { return -1; }
    }

    static bool IsInUse(Exception ex) => (ex.HResult & 0xFFFF) is 32 or 33; // sharing / lock violation

    static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch { /* best effort */ }
    }
}
