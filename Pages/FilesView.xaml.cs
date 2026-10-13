using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using DiskTrim.UI;

namespace DiskTrim.Pages;

/// <summary>Filter other pages can open this page with (Home → "Show files", File types → "Show largest files").</summary>
public sealed record FilesFilter(long MinSize = 0, int OlderThanDays = 0, string? Category = null);

public sealed class ItemRow(FileEntry file)
{
    SafetyVerdict? verdict;

    public FileEntry File { get; } = file;
    public string Name => File.Name + (File.IsOnlineOnly ? "  (online-only)" : "");
    public string Folder => File.Dir.FullPath;
    public string Path => File.FullPath;
    public long Size => File.Size;
    public string SizeText => Format.Bytes(File.Size);
    public DateTime Modified => File.Modified;
    public string ModifiedText => Format.Date(File.Modified);
    public string Category => FileCategories.Of(File.Extension);
    public SafetyVerdict Verdict => verdict ??= SafetyRules.Classify(Path);
    public SafetyLevel Level => Verdict.Level;
}

public partial class FilesView : UserControl, IPage
{
    const int MaxRows = 2000;

    static readonly (string Text, long Bytes)[] Sizes =
        [("Any size", 0), ("≥ 10 MB", 10L << 20), ("≥ 100 MB", 100L << 20), ("≥ 500 MB", 500L << 20), ("≥ 1 GB", 1L << 30)];
    static readonly (string Text, int Days)[] Ages =
        [("Any time", 0), ("Older than 3 months", 90), ("Older than 6 months", 182), ("Older than 1 year", 365),
         ("Older than 2 years", 730), ("Older than 5 years", 1825)];

    readonly AppState state = AppState.Current;
    readonly DispatcherTimer searchDelay = new() { Interval = TimeSpan.FromMilliseconds(350) };
    int shownVersion = -1;
    bool ready;
    CancellationTokenSource? refreshCts;

    public FilesView()
    {
        InitializeComponent();
        SizeBox.ItemsSource = Sizes.Select(s => s.Text).ToList();
        AgeBox.ItemsSource = Ages.Select(a => a.Text).ToList();
        TypeBox.ItemsSource = new[] { "All types" }.Concat(FileCategories.Names).ToList();
        SizeBox.SelectedIndex = 2;
        AgeBox.SelectedIndex = 0;
        TypeBox.SelectedIndex = 0;
        ready = true;

        Grid.ContextMenu = ItemMenu.Build(SelectedPaths);
        searchDelay.Tick += (_, _) => { searchDelay.Stop(); _ = RefreshAsync(); };
        state.ScanChanged += () => { if (IsVisible) _ = RefreshAsync(); };
    }

    public void OnNavigatedTo(object? argument)
    {
        if (argument is FilesFilter f)
        {
            ready = false;
            SizeBox.SelectedIndex = Math.Max(0, Array.FindLastIndex(Sizes, s => s.Bytes <= f.MinSize));
            AgeBox.SelectedIndex = Math.Max(0, Array.FindLastIndex(Ages, a => a.Days <= f.OlderThanDays));
            TypeBox.SelectedItem = f.Category ?? "All types";
            ready = true;
            _ = RefreshAsync();
        }
        else if (shownVersion != state.ScanVersion) _ = RefreshAsync();
    }

    void Filter_Changed(object sender, RoutedEventArgs e) { if (ready && IsLoaded) _ = RefreshAsync(); }

    void Search_Changed(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        searchDelay.Stop();
        searchDelay.Start();
    }

    async Task RefreshAsync()
    {
        shownVersion = state.ScanVersion;
        if (state.Scan is not { } scan)
        {
            Grid.ItemsSource = null;
            Summary.Text = "Scan a drive first.";
            return;
        }

        long minSize = Sizes[Math.Max(0, SizeBox.SelectedIndex)].Bytes;
        int days = Ages[Math.Max(0, AgeBox.SelectedIndex)].Days;
        string? category = TypeBox.SelectedIndex > 0 ? TypeBox.SelectedItem as string : null;
        string search = SearchBox.Text.Trim();
        bool hideKeep = HideKeepBox.IsChecked == true;
        var cutoff = days > 0 ? DateTime.Now.AddDays(-days) : DateTime.MaxValue;

        refreshCts?.Cancel();
        var cts = refreshCts = new CancellationTokenSource();
        Summary.Text = "Searching…";

        var (rows, count, total) = await Task.Run(() =>
        {
            // Keep only the largest MaxRows matches without sorting millions of files.
            var top = new PriorityQueue<FileEntry, long>();
            long n = 0, bytes = 0;
            foreach (var f in scan.Files)
            {
                if (cts.IsCancellationRequested) break;
                if (f.Size < minSize || f.Modified > cutoff) continue;
                if (category is not null && FileCategories.Of(f.Extension) != category) continue;
                if (search.Length > 0 && !f.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                                      && !f.Dir.FullPath.Contains(search, StringComparison.OrdinalIgnoreCase)) continue;
                n++;
                bytes += f.Size;
                if (top.Count < MaxRows * 2) top.Enqueue(f, f.Size);
                else if (f.Size > top.Peek().Size) top.EnqueueDequeue(f, f.Size);
            }
            var list = new List<ItemRow>();
            while (top.Count > 0) list.Add(new ItemRow(top.Dequeue()));
            list.Reverse();
            // Classifying is only needed for the rows we show.
            if (hideKeep) list = list.Where(r => r.Level != SafetyLevel.Keep).ToList();
            return (list.Take(MaxRows).ToList(), n, bytes);
        });
        if (cts.IsCancellationRequested) return;

        Grid.ItemsSource = rows;
        Summary.Text = rows.Count == 0
            ? "No files match these filters."
            : $"{Format.Count(count)} files match · {Format.Bytes(total)} · showing the largest {Format.Count(rows.Count)}";
    }

    IReadOnlyList<string> SelectedPaths() => Grid.SelectedItems.OfType<ItemRow>().Select(r => r.Path).ToList();

    void Open_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedPaths() is [var first, ..]) FileActions.ShowInExplorer(first);
        else Dialogs.Info("Select a file in the list first.");
    }

    async void InUse_Click(object sender, RoutedEventArgs e) => await FileActions.CheckInUseAsync(SelectedPaths());

    async void Recycle_Click(object sender, RoutedEventArgs e) => await FileActions.RecycleAsync(SelectedPaths());

    async void Move_Click(object sender, RoutedEventArgs e) => await FileActions.MoveAsync(SelectedPaths());

    void Grid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Grid.SelectedItem is ItemRow row) FileActions.ShowInExplorer(row.Path);
    }
}
