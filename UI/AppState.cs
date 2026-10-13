using System.Windows.Threading;

namespace DiskTrim.UI;

/// <summary>Everything the pages share: the current scan, cleanup measurements, status line and navigation.</summary>
public sealed class AppState : Observable
{
    public static AppState Current { get; } = new();

    ScanResult? scan;
    List<CleanupItem>? cleanupItems;
    string status = "Ready";
    string? operation;
    bool isScanning;
    CancellationTokenSource? scanCts;

    public ScanResult? Scan { get => scan; private set => Set(ref scan, value); }

    /// <summary>Changes whenever the scan or its contents change — pages compare it to know when to refresh.</summary>
    public int ScanVersion { get; private set; }

    public event Action? ScanChanged;

    public List<CleanupItem>? CleanupItems { get => cleanupItems; private set => Set(ref cleanupItems, value); }

    public event Action? CleanupMeasured;

    public string Status { get => status; set => Set(ref status, value); }

    public bool IsScanning
    {
        get => isScanning;
        private set { if (Set(ref isScanning, value)) Raise(nameof(IsBusy)); }
    }

    public string? Operation
    {
        get => operation;
        private set { if (Set(ref operation, value)) Raise(nameof(IsBusy)); }
    }

    public bool IsBusy => IsScanning || Operation is not null;

    // ---- navigation ------------------------------------------------------------------------------

    public event Action<string, object?>? NavigateRequested;

    public void Navigate(string page, object? argument = null) => NavigateRequested?.Invoke(page, argument);

    // ---- long operations -------------------------------------------------------------------------

    /// <summary>Marks a long operation as running. Returns null (and tells the user) if one is already running.</summary>
    public IDisposable? Begin(string name)
    {
        if (IsBusy)
        {
            Dialogs.Info("Another operation is still running — wait for it to finish.");
            return null;
        }
        Operation = name;
        Status = name;
        return new EndOperation(this);
    }

    sealed class EndOperation(AppState state) : IDisposable
    {
        bool done;
        public void Dispose()
        {
            if (done) return;
            done = true;
            state.Operation = null;
        }
    }

    // ---- scan ------------------------------------------------------------------------------------

    public async Task ScanAsync(string path)
    {
        if (IsBusy) { Dialogs.Info("Another operation is still running — wait for it to finish."); return; }

        string root;
        try { root = Scanner.NormalizeRoot(path); }
        catch (Exception ex) { Dialogs.Error(ex.Message); return; }
        if (!Directory.Exists(root)) { Dialogs.Error("Folder not found: " + root); return; }

        var settings = AppSettings.Load();
        var progress = new ScanProgress();
        scanCts = new CancellationTokenSource();
        IsScanning = true;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) => Status = $"Scanning…  {Format.Count(progress.Files)} files  ·  {Format.Bytes(progress.Bytes)}  ·  {progress.Current}";
        timer.Start();
        try
        {
            var token = scanCts.Token;
            var result = await Task.Run(() => Scanner.Scan(root, settings.Exclusions, progress, token));
            if (!result.Cancelled)
                _ = Task.Run(() => { try { History.Save(result); } catch (Exception ex) { AppPaths.LogError(ex); } });

            Scan = result;
            ScanVersion++;
            Status = (result.Cancelled ? "Scan was stopped — results are partial. " : "") +
                     $"{Format.Count(result.Root.FileCount)} files in {Format.Count(result.Root.FolderCount)} folders · scanned {Format.Bytes(result.TotalBytes)} in {result.Duration.TotalSeconds:0.0} s" +
                     (result.UnreadableFolders > 0
                         ? $" · {Format.Count(result.UnreadableFolders)} folders not readable" + (AppPaths.IsAdmin ? "" : " (run as administrator to include them)")
                         : "");
            ScanChanged?.Invoke();
        }
        catch (Exception ex)
        {
            Status = "Scan failed: " + ex.Message;
            AppPaths.LogError(ex);
        }
        finally
        {
            timer.Stop();
            scanCts.Dispose();
            scanCts = null;
            IsScanning = false;
        }
    }

    public void StopScan() => scanCts?.Cancel();

    /// <summary>Call after files were deleted or moved so every page shows the new sizes.</summary>
    public void Removed(IReadOnlyCollection<string> paths)
    {
        if (Scan is null || paths.Count == 0) return;
        Scan.RemovePaths(paths);
        ScanVersion++;
        ScanChanged?.Invoke();
    }

    // ---- cleanup measurement ---------------------------------------------------------------------

    public async Task<List<CleanupItem>> MeasureAsync()
    {
        var items = await Task.Run(() =>
        {
            var list = CleanupCatalog.Build();
            Parallel.ForEach(list, new ParallelOptions { MaxDegreeOfParallelism = 4 }, i => CleanupEngine.Measure(i));
            return list;
        });
        CleanupItems = items;
        CleanupMeasured?.Invoke();
        return items;
    }
}
