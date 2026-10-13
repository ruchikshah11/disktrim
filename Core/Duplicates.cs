using System.Security.Cryptography;

namespace DiskTrim.Core;

public enum KeepRule
{
    /// <summary>Oldest copy (the original).</summary>
    Oldest,
    Newest,
    ShortestPath,
}

public sealed class DuplicateGroup(long size, List<FileEntry> files)
{
    public long Size { get; } = size;
    public List<FileEntry> Files { get; } = files;
    public long Wasted => Size * (Files.Count - 1);

    public FileEntry Keeper(KeepRule rule) => rule switch
    {
        KeepRule.Newest => Files.MaxBy(f => f.Modified)!,
        KeepRule.ShortestPath => Files.MinBy(f => f.FullPath.Length)!,
        _ => Files.MinBy(f => f.Modified)!,
    };
}

/// <summary>Finds identical files: same size → same first 64 KB → same SHA-256 of the whole file.</summary>
public static class Duplicates
{
    const int PartialBytes = 64 * 1024;

    public static List<DuplicateGroup> Find(IEnumerable<FileEntry> files, long minSize, ScanProgress progress,
        CancellationToken ct, out int unreadable)
    {
        int bad = 0;
        var bySize = files
            .Where(f => f.Size >= Math.Max(1, minSize) && !f.IsOnlineOnly && (f.Attributes & FileAttributes.ReparsePoint) == 0)
            .GroupBy(f => f.Size)
            .Where(g => g.Skip(1).Any())
            .OrderByDescending(g => g.Key)
            .ToList();

        var found = new ConcurrentBag<DuplicateGroup>();
        Parallel.ForEach(bySize, new ParallelOptions { MaxDegreeOfParallelism = 4 }, (sizeGroup, state) =>
        {
            if (ct.IsCancellationRequested) { state.Stop(); return; }

            // Hard links are the same file under two names: deleting one frees nothing.
            var unique = sizeGroup.GroupBy(f => Native.FileId(f.FullPath) ?? f.FullPath, StringComparer.OrdinalIgnoreCase)
                                  .Select(g => g.First()).ToList();
            Interlocked.Add(ref progress.Files, unique.Count);
            if (unique.Count < 2) return;

            foreach (var candidate in GroupByHash(unique, PartialBytes, ref bad))
            {
                var finals = sizeGroup.Key <= PartialBytes ? [candidate] : GroupByHash(candidate, long.MaxValue, ref bad);
                foreach (var g in finals) found.Add(new DuplicateGroup(sizeGroup.Key, g));
            }
            progress.Current = $"{found.Count} duplicate groups so far";
        });
        unreadable = bad;

        // Never offer to remove copies that Windows or installed programs need.
        return found
            .Select(g => new DuplicateGroup(g.Size, g.Files.Where(f => SafetyRules.Classify(f.FullPath).Level != SafetyLevel.Keep).ToList()))
            .Where(g => g.Files.Count > 1)
            .OrderByDescending(g => g.Wasted)
            .ToList();
    }

    static List<List<FileEntry>> GroupByHash(List<FileEntry> files, long maxBytes, ref int unreadable)
    {
        var map = new Dictionary<string, List<FileEntry>>();
        foreach (var f in files)
        {
            var h = Hash(f.FullPath, maxBytes);
            if (h is null) { Interlocked.Increment(ref unreadable); continue; }
            if (!map.TryGetValue(h, out var list)) map[h] = list = [];
            list.Add(f);
        }
        return map.Values.Where(l => l.Count > 1).ToList();
    }

    static string? Hash(string path, long maxBytes)
    {
        try
        {
            using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                1 << 16, FileOptions.SequentialScan);
            if (maxBytes == long.MaxValue) return Convert.ToHexString(SHA256.HashData(s));

            var buffer = new byte[(int)Math.Min(maxBytes, s.Length)];
            s.ReadExactly(buffer);
            return Convert.ToHexString(SHA256.HashData(buffer));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
