using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DiskTrim.UI;

namespace DiskTrim.Pages;

public sealed record DriveCard(string Title, double UsedPercent, string FreeText, Brush BarBrush);

public sealed record RecCard(Recommendation Rec)
{
    public string Title => Rec.Title;
    public string Detail => Rec.Detail + (Rec.Path is null ? "" : "\n" + Rec.Path);
    public SafetyLevel Level => Rec.Level;
    public string SizeText => Rec.Bytes > 0 ? Format.Bytes(Rec.Bytes) : "";

    public string ActionLabel => Rec.Action switch
    {
        RecAction.CleanSafe => "Clean now",
        RecAction.OpenCleanup => "Open Cleanup",
        RecAction.RestartAdmin => "Restart as admin",
        RecAction.EmptyRecycleBin => "Empty",
        RecAction.TurnOffHibernation => "Turn off",
        RecAction.ShowFile => "Show file",
        RecAction.ShowFolder => "Show folder",
        RecAction.ShowFiles => "Show files",
        _ => "Find duplicates",
    };
}

public partial class HomeView : UserControl, IPage
{
    readonly AppState state = AppState.Current;
    int shownScanVersion = -1;
    List<CleanupItem>? shownItems;
    bool measuring;

    public HomeView()
    {
        InitializeComponent();
        MachineText.Text = $"{Environment.MachineName} · {Environment.UserName}";
        SafeBytes.Foreground = SafetyBrushes.Foreground(SafetyLevel.Safe);
        ScanDriveText.Text = $"Scan {AppPaths.SystemRoot.TrimEnd('\\')}";
        state.ScanChanged += () => { ShowScanCard(); if (IsVisible) _ = RefreshAsync(measure: false); };
        state.CleanupMeasured += () => { if (IsVisible) ShowRecommendations(); };
    }

    public void OnNavigatedTo(object? argument)
    {
        Scroller.ScrollToHome();
        ShowDrives();
        ShowScanCard();
        if (state.CleanupItems is null) _ = RefreshAsync(measure: true);
        // Measuring may have finished while another page was open.
        else if (shownItems != state.CleanupItems || shownScanVersion != state.ScanVersion) ShowRecommendations();
    }

    async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync(measure: true);

    async Task RefreshAsync(bool measure)
    {
        ShowDrives();
        if (measure || state.CleanupItems is null)
        {
            if (measuring) return;
            measuring = true;
            RecHint.Text = "Measuring caches and temporary files…";
            SafeBytes.Text = "Measuring…";
            CleanSafeButton.IsEnabled = false;
            try { await state.MeasureAsync(); }
            finally { measuring = false; }
        }
        ShowRecommendations();
    }

    void ShowDrives()
    {
        var cards = new List<DriveCard>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady || d.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
                long total = d.TotalSize, free = d.AvailableFreeSpace;
                double used = total == 0 ? 0 : 100.0 * (total - free) / total;
                var label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? (d.DriveType == DriveType.Removable ? "Removable" : "Local disk") : d.VolumeLabel;
                var color = used > 90 ? SafetyLevel.Keep : used > 75 ? SafetyLevel.Caution : SafetyLevel.Review;
                cards.Add(new DriveCard($"{label} ({d.Name.TrimEnd('\\')})", used,
                    $"{Format.Bytes(free)} free of {Format.Bytes(total)}  ·  {used:0} % used", SafetyBrushes.Foreground(color)));
            }
            catch (IOException) { /* drive went away */ }
        }
        DriveList.ItemsSource = cards;
    }

    void ShowRecommendations()
    {
        if (state.CleanupItems is not { } items) return;
        shownScanVersion = state.ScanVersion;
        shownItems = items;

        var safe = SafeItems(items);
        long safeBytes = safe.Sum(i => i.Bytes);
        SafeBytes.Text = Format.Bytes(safeBytes);
        CleanSafeButton.IsEnabled = safe.Count > 0;
        SafeDetail.Text = safe.Count == 0
            ? "Nothing safe to clean right now. Blocked items (apps open, administrator needed) are listed under Cleanup."
            : $"{safe.Count} item(s): temporary files and caches that Windows and your apps rebuild by themselves. Your own files are never touched.";

        // The "safe" total already has its own card.
        var recs = Recommendations.Build(items, state.Scan).Where(r => r.Action != RecAction.CleanSafe).ToList();
        RecList.ItemsSource = recs.Select(r => new RecCard(r)).ToList();
        RecHint.Text = recs.Count == 0
            ? "Nothing to recommend — your drive looks tidy."
            : "Biggest wins first." + (state.Scan is null ? " Scan the drive for more recommendations (big folders, photos to move, old files, duplicates)." : "");
    }

    static List<CleanupItem> SafeItems(IEnumerable<CleanupItem> items) =>
        items.Where(i => i.Level == SafetyLevel.Safe && !i.IsBlocked && i.Bytes > 0
                         && i.Kind is CleanupKind.Files or CleanupKind.Command).ToList();

    void ShowScanCard()
    {
        if (state.Scan is { } scan)
        {
            ScanTitle.Text = $"{scan.Root.FullPath} · {Format.Bytes(scan.TotalBytes)}";
            ScanDetail.Text = $"{Format.Count(scan.Root.FileCount)} files in {Format.Count(scan.Root.FolderCount)} folders · scanned {Format.Stamp(scan.Time)}" +
                              (scan.Cancelled ? " · stopped early, results are partial" : "");
            OpenExplorerButton.IsEnabled = true;
        }
        else
        {
            ScanTitle.Text = "Not scanned yet";
            ScanDetail.Text = "Scan a drive to see what uses the space and get personalised recommendations.";
            OpenExplorerButton.IsEnabled = false;
        }
    }

    async void CleanSafe_Click(object sender, RoutedEventArgs e)
    {
        if (state.CleanupItems is { } items) await CleanupRunner.RunAsync(SafeItems(items));
    }

    void ReviewCleanup_Click(object sender, RoutedEventArgs e) => state.Navigate("cleanup");

    async void ScanDrive_Click(object sender, RoutedEventArgs e) => await state.ScanAsync(AppPaths.SystemRoot);

    void OpenExplorer_Click(object sender, RoutedEventArgs e) => state.Navigate("explorer");

    async void Action_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not RecCard card || state.CleanupItems is not { } items) return;
        var rec = card.Rec;
        switch (rec.Action)
        {
            case RecAction.CleanSafe:
                await CleanupRunner.RunAsync(items.Where(i => i.Level == SafetyLevel.Safe && !i.IsBlocked && i.Bytes > 0
                                                              && i.Kind is CleanupKind.Files or CleanupKind.Command).ToList());
                break;
            case RecAction.EmptyRecycleBin:
                await CleanupRunner.RunAsync(items.Where(i => i.Kind == CleanupKind.RecycleBin).ToList());
                break;
            case RecAction.TurnOffHibernation:
                await CleanupRunner.RunAsync(items.Where(i => i.Id == "hiberfil").ToList());
                break;
            case RecAction.OpenCleanup:
                state.Navigate("cleanup");
                break;
            case RecAction.RestartAdmin:
                (Window.GetWindow(this) as MainWindow)?.AdminButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                break;
            case RecAction.ShowFile when rec.Path is not null:
                FileActions.ShowInExplorer(rec.Path);
                break;
            case RecAction.ShowFolder when rec.Path is not null:
                state.Navigate("explorer", rec.Path);
                break;
            case RecAction.ShowFiles:
                state.Navigate("files", new FilesFilter(MinSize: 100L * 1024 * 1024, OlderThanDays: 365));
                break;
            case RecAction.FindDuplicates:
                state.Navigate("duplicates");
                break;
        }
    }
}
