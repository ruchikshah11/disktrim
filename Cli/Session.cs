namespace DiskTrim.Cli;

/// <summary>Keeps the last scan so the interactive menu doesn't rescan for every page.</summary>
static class Session
{
    public static ScanResult? LastScan { get; private set; }

    public static ScanResult Scan(string path)
    {
        var root = Scanner.NormalizeRoot(path);
        var settings = AppSettings.Load();
        var progress = new ScanProgress();

        Ui.Muted($"Scanning {root}  (Ctrl+C stops and keeps the results so far)");
        var result = Ui.WithProgress(
            () => $"Scanning…  {Format.Count(progress.Files)} files · {Format.Bytes(progress.Bytes)} · {progress.Current}",
            ct => Scanner.Scan(root, settings.Exclusions, progress, ct));

        Ui.Ok($"Scanned {Format.Count(result.Root.FileCount)} files in {Format.Count(result.Root.FolderCount)} folders · " +
              $"{Format.Bytes(result.TotalBytes)} · {result.Duration.TotalSeconds:0.0} s");
        if (result.UnreadableFolders > 0)
            Ui.Muted($"{Format.Count(result.UnreadableFolders)} folders not readable" +
                     (AppPaths.IsAdmin ? "" : " (run as administrator to include them)"));
        if (settings.Exclusions.Count > 0)
            Ui.Muted($"{settings.Exclusions.Count} excluded folder(s) skipped (see: settings exclude list)");
        if (result.Cancelled) Ui.Warn("Scan stopped — the results so far are shown.");
        else
        {
            try { History.Save(result); }
            catch (Exception ex) { AppPaths.LogError(ex); }
        }

        LastScan = result;
        return result;
    }

    /// <summary>Reuses the last scan when it covers <paramref name="path"/>; otherwise scans.</summary>
    public static ScanResult GetOrScan(string? path)
    {
        var root = Scanner.NormalizeRoot(path ?? AppPaths.SystemRoot);
        if (LastScan is not null && LastScan.Root.FullPath.Equals(root, StringComparison.OrdinalIgnoreCase))
            return LastScan;
        return Scan(root);
    }

    public static List<CleanupItem> MeasureCatalog()
    {
        var items = CleanupCatalog.Build();
        int done = 0;
        Ui.WithProgress(
            () => $"Measuring caches and temporary files… {done}/{items.Count}",
            ct => Parallel.ForEach(items, new ParallelOptions { MaxDegreeOfParallelism = 4 }, item =>
            {
                CleanupEngine.Measure(item, ct);
                Interlocked.Increment(ref done);
            }));
        return items;
    }
}
