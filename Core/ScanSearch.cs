namespace DiskTrim.Core;

public sealed record SearchHit(DirNode? Dir, FileEntry? File)
{
    public string Name => Dir?.Name ?? File!.Name;
    public string Path => Dir?.FullPath ?? File!.FullPath;
    public long Size => Dir?.Size ?? File!.Size;
    public bool IsFolder => Dir is not null;
}

/// <summary>Finds files and folders in a finished scan by name — no disk access, so it is instant.</summary>
public static class ScanSearch
{
    /// <param name="query">Part of a name ("invoice"), or a wildcard pattern ("*.iso", "report??.pdf").</param>
    /// <returns>Matches, biggest first, at most <paramref name="max"/>; <paramref name="total"/> counts all of them.</returns>
    public static List<SearchHit> Find(ScanResult scan, string query, int max, out int total, CancellationToken ct = default)
    {
        total = 0;
        query = query.Trim();
        if (query.Length == 0) return [];

        Func<string, bool> matches;
        if (query.IndexOfAny(['*', '?']) >= 0)
        {
            var regex = new Regex("^" + Regex.Escape(query).Replace(@"\*", ".*").Replace(@"\?", ".") + "$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            matches = regex.IsMatch;
        }
        else matches = name => name.Contains(query, StringComparison.OrdinalIgnoreCase);

        // Keep only the biggest `max` hits while walking, so a search over millions of files stays cheap.
        var top = new PriorityQueue<SearchHit, long>();
        int count = 0;
        void Offer(SearchHit hit)
        {
            count++;
            if (top.Count < max) top.Enqueue(hit, hit.Size);
            else if (hit.Size > top.Peek().Size) top.EnqueueDequeue(hit, hit.Size);
        }

        var stack = new Stack<DirNode>();
        stack.Push(scan.Root);
        while (stack.Count > 0 && !ct.IsCancellationRequested)
        {
            var node = stack.Pop();
            foreach (var c in node.Children)
            {
                if (matches(c.Name)) Offer(new SearchHit(c, null));
                stack.Push(c);
            }
            foreach (var f in node.Files)
                if (matches(f.Name)) Offer(new SearchHit(null, f));
        }

        total = count;
        var list = new List<SearchHit>(top.Count);
        while (top.Count > 0) list.Add(top.Dequeue());
        list.Reverse();
        return list;
    }
}
