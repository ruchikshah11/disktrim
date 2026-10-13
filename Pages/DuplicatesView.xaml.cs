using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using DiskTrim.UI;

namespace DiskTrim.Pages;

public sealed class DupRow(DuplicateGroup group, FileEntry file, string groupName, bool initiallyMarked, Action changed) : Observable
{
    bool marked = initiallyMarked;

    public DuplicateGroup Group { get; } = group;
    public FileEntry File { get; } = file;
    public string GroupName { get; } = groupName;
    public string Path => File.FullPath;
    public string ModifiedText => Format.Stamp(File.Modified);
    public string SizeText => Format.Bytes(File.Size);

    public bool Marked
    {
        get => marked;
        set { if (Set(ref marked, value)) changed(); }
    }
}

public partial class DuplicatesView : UserControl, IPage
{
    const int MaxGroups = 500;

    static readonly (string Text, long Bytes)[] MinSizes =
        [("100 KB", 100L << 10), ("1 MB", 1L << 20), ("10 MB", 10L << 20), ("100 MB", 100L << 20)];
    static readonly (string Text, KeepRule Rule)[] KeepRules =
        [("Oldest copy (the original)", KeepRule.Oldest), ("Newest copy", KeepRule.Newest), ("Copy with the shortest path", KeepRule.ShortestPath)];

    readonly AppState state = AppState.Current;
    List<DuplicateGroup> groups = [];
    List<DupRow> rows = [];
    int foundForVersion = -1;

    public DuplicatesView()
    {
        InitializeComponent();
        MinSizeBox.ItemsSource = MinSizes.Select(s => s.Text).ToList();
        MinSizeBox.SelectedIndex = 1;
        KeepBox.ItemsSource = KeepRules.Select(k => k.Text).ToList();
        KeepBox.SelectedIndex = 0;
        Grid.ContextMenu = ItemMenu.Build(() => Grid.SelectedItem is DupRow r ? [r.Path] : []);
        state.ScanChanged += () =>
        {
            // Rows of files that no longer exist must go; a new scan invalidates everything.
            if (groups.Count > 0 && state.Scan is not null) Prune();
        };
    }

    public void OnNavigatedTo(object? argument)
    {
        if (state.Scan is null)
        {
            Summary.Text = "";
            EmptyText.Text = "Scan a drive first";
            EmptyHint.Text = "Duplicates are searched in the scanned files.";
        }
        else if (groups.Count == 0 && foundForVersion < 0)
        {
            EmptyText.Text = "No results yet";
            EmptyHint.Text = "Choose the options above and click Find duplicates.";
        }
    }

    KeepRule Keep => KeepRules[Math.Max(0, KeepBox.SelectedIndex)].Rule;

    async void Find_Click(object sender, RoutedEventArgs e)
    {
        if (state.Scan is not { } scan) { Dialogs.Info("Scan a drive first."); return; }
        using var op = state.Begin("Finding duplicate files…");
        if (op is null) return;

        long minSize = MinSizes[Math.Max(0, MinSizeBox.SelectedIndex)].Bytes;
        Grid.Visibility = Visibility.Collapsed;
        EmptyState.Visibility = Visibility.Visible;
        EmptyText.Text = "Finding duplicate files…";
        EmptyHint.Text = "Files of the same size are compared by content. On a full drive this can take a few minutes.";
        Summary.Text = "";
        var progress = new ScanProgress();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        timer.Tick += (_, _) => state.Status = $"Comparing files… {Format.Count(progress.Files)} compared · {progress.Current}";
        timer.Start();
        int unreadable = 0;
        try
        {
            groups = await Task.Run(() => Duplicates.Find(scan.Files, minSize, progress, CancellationToken.None, out unreadable));
        }
        finally { timer.Stop(); }

        foundForVersion = state.ScanVersion;
        state.Status = groups.Count == 0
            ? "No duplicates found."
            : $"Found {Format.Count(groups.Count)} groups of duplicates — {Format.Bytes(groups.Sum(g => g.Wasted))} could be freed." +
              (unreadable > 0 ? " · some contents could not be read" : "");
        ShowRows();
    }

    void ShowRows()
    {
        var keep = Keep;
        rows = [];
        foreach (var g in groups.Take(MaxGroups))
        {
            var keeper = g.Keeper(keep);
            string name = $"{Format.Bytes(g.Size)} × {g.Files.Count} copies  ·  {Format.Bytes(g.Wasted)} wasted";
            foreach (var f in g.Files.OrderBy(f => f != keeper).ThenBy(f => f.FullPath))
                rows.Add(new DupRow(g, f, name, f != keeper, UpdateSummary));
        }
        var view = new ListCollectionView(rows);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(DupRow.GroupName)));
        Grid.ItemsSource = view;
        Grid.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = rows.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        if (rows.Count == 0 && foundForVersion >= 0)
        {
            EmptyText.Text = "No duplicate files found";
            EmptyHint.Text = "Try a smaller minimum size, or scan a bigger folder.";
        }
        UpdateSummary();
    }

    void Keep_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded && groups.Count > 0) ShowRows();
    }

    void UpdateSummary()
    {
        if (groups.Count == 0) { Summary.Text = foundForVersion < 0 ? "Choose the options above and click Find duplicates." : "No duplicate files found."; return; }
        var marked = rows.Where(r => r.Marked).ToList();
        Summary.Text = $"{Format.Count(groups.Count)} groups" + (groups.Count > MaxGroups ? $" (showing the biggest {MaxGroups})" : "") +
                       $"  ·  {Format.Count(marked.Count)} copies ticked · {Format.Bytes(marked.Sum(r => r.File.Size))} would be freed";
    }

    async void Recycle_Click(object sender, RoutedEventArgs e)
    {
        var marked = rows.Where(r => r.Marked).ToList();
        if (marked.Count == 0) { Dialogs.Info("Nothing selected"); return; }

        // Never let every copy of a file go.
        var allTicked = rows.GroupBy(r => r.Group).Where(g => g.All(r => r.Marked)).Select(g => g.First().File.Name).ToList();
        if (allTicked.Count > 0)
        {
            Dialogs.Info("Every copy of these files is ticked — untick at least one copy of each:\n\n" + Dialogs.Bullets(allTicked), "Keep one copy");
            return;
        }

        if (await FileActions.RecycleAsync(marked.Select(r => r.Path).ToList())) Prune();
    }

    /// <summary>Drops rows whose file was removed and groups that no longer have duplicates.</summary>
    void Prune()
    {
        groups = groups
            .Select(g => new DuplicateGroup(g.Size, g.Files.Where(f => System.IO.File.Exists(f.FullPath)).ToList()))
            .Where(g => g.Files.Count > 1)
            .ToList();
        ShowRows();
    }

    void Open_Click(object sender, RoutedEventArgs e)
    {
        if (Grid.SelectedItem is DupRow r) FileActions.ShowInExplorer(r.Path);
        else Dialogs.Info("Select a file in the list first.");
    }

    void Grid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Grid.SelectedItem is DupRow r) FileActions.ShowInExplorer(r.Path);
    }
}
