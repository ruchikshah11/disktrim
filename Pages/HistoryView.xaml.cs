using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DiskTrim.UI;

namespace DiskTrim.Pages;

public sealed record SnapshotRow(Snapshot Snap)
{
    public string TimeText => Snap.Time.ToString("ddd yyyy-MM-dd  HH:mm", CultureInfo.CurrentCulture);
    public string Root => Snap.Root;
    public string Detail => $"{Format.Bytes(Snap.TotalBytes)} scanned · {Format.Count(Snap.Files)} files · {Format.Bytes(Snap.FreeBytes)} free on drive";
}

public sealed record ChangeRow(FolderChange Change)
{
    public string Path => Change.Path;
    public long Before => Change.Before;
    public long After => Change.After;
    public long Delta => Change.Delta;
    public string BeforeText => Format.Bytes(Change.Before);
    public string AfterText => Format.Bytes(Change.After);
    public string DeltaText => (Change.Delta > 0 ? "+" : "−") + Format.Bytes(Math.Abs(Change.Delta));
    public Brush DeltaBrush => SafetyBrushes.Foreground(Change.Delta > 0 ? SafetyLevel.Caution : SafetyLevel.Safe);
}

public partial class HistoryView : UserControl, IPage
{
    static readonly (string Text, long Bytes)[] Thresholds = [("10 MB", 10L << 20), ("100 MB", 100L << 20), ("1 GB", 1L << 30)];

    List<Snapshot> snaps = [];

    public HistoryView()
    {
        InitializeComponent();
        ThresholdBox.ItemsSource = Thresholds.Select(t => t.Text).ToList();
        ThresholdBox.SelectedIndex = 1;
    }

    public void OnNavigatedTo(object? argument) => _ = LoadAsync();

    async Task LoadAsync()
    {
        snaps = await Task.Run(() => History.List());
        Snapshots.ItemsSource = snaps.Select(s => new SnapshotRow(s)).ToList();
        if (snaps.Count == 0)
        {
            ChangeSummary.Text = "No scans saved yet. Scan a drive — each finished scan is remembered.";
            Changes.ItemsSource = null;
        }
        else Snapshots.SelectedIndex = 0;
    }

    void Snapshots_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowChanges();

    void Threshold_Changed(object sender, SelectionChangedEventArgs e) { if (IsLoaded) ShowChanges(); }

    void ShowChanges()
    {
        if (Snapshots.SelectedItem is not SnapshotRow row) return;
        var after = row.Snap;
        // The previous scan of the same folder.
        var before = snaps.Where(s => s.Root.Equals(after.Root, StringComparison.OrdinalIgnoreCase) && s.Time < after.Time)
                          .MaxBy(s => s.Time);
        if (before is null)
        {
            Changes.ItemsSource = null;
            ChangeSummary.Text = "Needs at least two scans of a drive. Scan again later to see what changed.";
            return;
        }

        long threshold = Thresholds[Math.Max(0, ThresholdBox.SelectedIndex)].Bytes;
        var changes = History.Compare(before, after, threshold);
        Changes.ItemsSource = changes.Select(c => new ChangeRow(c)).ToList();
        long delta = after.TotalBytes - before.TotalBytes;
        ChangeSummary.Text = $"{before.Time:MMM d, HH:mm}  →  {after.Time:MMM d, HH:mm}  ·  Total {(delta >= 0 ? "grew" : "shrank")} by {Format.Bytes(Math.Abs(delta))}" +
                             (changes.Count == 0 ? " · no folder changed by more than the threshold" : "");
    }

    void Changes_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Changes.SelectedItem is ChangeRow r && Directory.Exists(r.Path)) FileActions.Open(r.Path);
    }
}
