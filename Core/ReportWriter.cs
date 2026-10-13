using System.Net;

namespace DiskTrim.Core;

/// <summary>A self-contained HTML report: drive usage, cleanup recommendations, biggest folders and files.</summary>
public static class ReportWriter
{
    public static void Write(string file, ScanResult scan, IReadOnlyList<CleanupItem> items, int topFolders = 30, int topFiles = 50)
    {
        var sb = new StringBuilder();
        sb.Append("""
            <!doctype html><html><head><meta charset="utf-8"><title>DiskTrim report</title>
            <style>
            body{font-family:Segoe UI,Arial,sans-serif;margin:24px;color:#1f2328;background:#fff}
            h1{margin:0 0 4px}h2{margin-top:28px;border-bottom:1px solid #ddd;padding-bottom:4px}
            .muted{color:#666}table{border-collapse:collapse;width:100%;font-size:13px}
            th,td{text-align:left;padding:5px 8px;border-bottom:1px solid #eee;vertical-align:top}th{background:#f6f8fa}
            td.n{text-align:right;white-space:nowrap}.b{display:inline-block;padding:1px 7px;border-radius:9px;font-size:12px;white-space:nowrap}
            .Safe{background:#dafbe1;color:#116329}.Caution{background:#fff8c5;color:#7d4e00}.Review{background:#ddf4ff;color:#0550ae}.Keep{background:#ffebe9;color:#a40e26}
            </style></head><body>
            <h1>DiskTrim report</h1><div class=muted>
            """);
        sb.Append(H($"{Environment.MachineName} · {Format.Stamp(scan.Time)} · DiskTrim {AppPaths.Version}"));
        sb.Append("</div>\n");

        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(scan.Root.FullPath)!);
            long used = drive.TotalSize - drive.AvailableFreeSpace;
            sb.Append($"<p><b>{H(drive.Name)}</b> {H(Format.Bytes(used))} used of {H(Format.Bytes(drive.TotalSize))} — <b>{H(Format.Bytes(drive.AvailableFreeSpace))} free</b></p>\n");
        }
        catch { /* network path or removed drive */ }

        sb.Append($"<p class=muted>Scanned {H(scan.Root.FullPath)}: {H(Format.Count(scan.Root.FileCount))} files, {H(Format.Bytes(scan.TotalBytes))}");
        if (scan.Cancelled) sb.Append(" (scan was stopped — results are partial)");
        sb.Append("</p>\n");

        long safeTotal = items.Where(i => i.Level == SafetyLevel.Safe && !i.IsBlocked).Sum(i => i.Bytes);
        sb.Append($"<h2>Cleanup recommendations</h2><p>Safe to free right now: <b>{H(Format.Bytes(safeTotal))}</b></p>\n");
        sb.Append("<table><tr><th>Item</th><th>Safety</th><th class=n>Size</th><th>What it is / impact</th><th>Status</th></tr>\n");
        foreach (var i in items.OrderByDescending(i => i.Bytes))
        {
            sb.Append($"<tr><td>{H(i.Title)}</td><td>{Badge(i.Level)}</td><td class=n>{H(i.SizeHint ?? Format.Bytes(i.Bytes))}</td>");
            sb.Append($"<td>{H(i.Description)}<br><span class=muted>{H(i.Impact)}</span></td><td>{H(i.Status)}</td></tr>\n");
        }
        sb.Append("</table>\n");

        sb.Append($"<h2>Biggest folders in {H(scan.Root.FullPath)}</h2><table><tr><th>Folder</th><th class=n>Size</th><th class=n>Share</th><th>Safety</th><th>Why</th></tr>\n");
        foreach (var d in BiggestFolders(scan.Root, topFolders))
        {
            var v = SafetyRules.Classify(d.FullPath);
            double share = scan.TotalBytes == 0 ? 0 : (double)d.Size / scan.TotalBytes;
            sb.Append($"<tr><td>{H(d.FullPath)}</td><td class=n>{H(Format.Bytes(d.Size))}</td><td class=n>{H(Format.Percent(share))}</td>");
            sb.Append($"<td>{Badge(v.Level)}</td><td>{H(v.Title)}<br><span class=muted>{H(v.Impact)}</span></td></tr>\n");
        }
        sb.Append("</table>\n");

        sb.Append("<h2>Largest files</h2><table><tr><th>File</th><th class=n>Size</th><th>Modified</th><th>Safety</th><th>Why</th></tr>\n");
        foreach (var f in scan.Files.OrderByDescending(f => f.Size).Take(topFiles))
        {
            var v = SafetyRules.Classify(f.FullPath);
            sb.Append($"<tr><td>{H(f.FullPath)}</td><td class=n>{H(Format.Bytes(f.Size))}</td><td>{H(Format.Date(f.Modified))}</td>");
            sb.Append($"<td>{Badge(v.Level)}</td><td>{H(v.Title)}<br><span class=muted>{H(v.Impact)}</span></td></tr>\n");
        }
        sb.Append("</table>\n");

        sb.Append("<p class=muted>Safe = cache/temporary data, rebuilt automatically · Caution = safe for your data but with a side effect · Your decision = personal or unknown data · Do not delete = needed by Windows or installed programs.</p></body></html>");
        File.WriteAllText(file, sb.ToString(), Encoding.UTF8);
    }

    /// <summary>Top folders across the first two levels below the scanned root.</summary>
    static IEnumerable<DirNode> BiggestFolders(DirNode root, int count) =>
        root.Children.Concat(root.Children.SelectMany(c => c.Children))
            .OrderByDescending(d => d.Size)
            .Take(count);

    static string Badge(SafetyLevel level) =>
        $"<span class=\"b {(level == SafetyLevel.Review ? "Review" : level.ToString())}\">{H(SafetyVerdict.LevelText(level))}</span>";

    static string H(string s) => WebUtility.HtmlEncode(s);
}
