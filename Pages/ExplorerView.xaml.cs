using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DiskTrim.UI;

namespace DiskTrim.Pages;

/// <summary>One row of the Explorer tree: a folder, a file, or a "… and N more" / loading placeholder.</summary>
public sealed class FolderVM : Observable
{
    const int MaxChildren = 300;
    static readonly FolderVM Loading = new(null, null, 0, "Loading…");

    readonly string? label;
    bool loaded, isExpanded, isSelected;
    SafetyLevel? level;

    public FolderVM(DirNode? dir, FileEntry? file, long parentSize, string? label = null)
    {
        Dir = dir;
        File = file;
        this.label = label;
        Share = parentSize > 0 ? (double)Size / parentSize : 0;
        if (dir is { IsLink: false } && (dir.Children.Count > 0 || dir.Files.Count > 0)) Children.Add(Loading);
    }

    public DirNode? Dir { get; }
    public FileEntry? File { get; }
    public bool IsPlaceholder => Dir is null && File is null;

    public string Name => label ?? (Dir is not null
        ? (Dir.Parent is null ? Dir.FullPath : Dir.Name) + (Dir.IsLink ? "  (link)" : Dir.Unreadable ? "  (no access)" : "")
        : File!.Name + (File.IsOnlineOnly ? "  (online-only)" : ""));

    public string Path => Dir?.FullPath ?? File?.FullPath ?? "";
    public long Size => Dir?.Size ?? File?.Size ?? 0;
    public string SizeText => IsPlaceholder ? "" : Format.Bytes(Size);
    public double Share { get; }
    public double BarWidth => Math.Max(0, Share * 70);
    public string ShareText => IsPlaceholder ? "" : Format.Percent(Share);
    public string Icon => Dir is not null ? "" : File is not null ? "" : "";
    public SafetyLevel Level => level ??= IsPlaceholder ? SafetyLevel.Review : SafetyRules.Classify(Path).Level;

    public ObservableCollection<FolderVM> Children { get; } = [];

    public bool IsExpanded
    {
        get => isExpanded;
        set { if (Set(ref isExpanded, value) && value) Load(); }
    }

    public bool IsSelected { get => isSelected; set => Set(ref isSelected, value); }

    /// <summary>Adds one specific child that is not among the biggest ones listed (e.g. a search result).</summary>
    public FolderVM? Include(string name)
    {
        if (Dir is null) return null;
        Load();
        FolderVM? vm = Dir.Children.FirstOrDefault(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } d2
            ? new FolderVM(d2, null, Dir.Size)
            : Dir.Files.FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } f2
                ? new FolderVM(null, f2, Dir.Size) : null;
        if (vm is null) return null;
        int at = Children.Count > 0 && Children[^1].IsPlaceholder ? Children.Count - 1 : Children.Count;
        Children.Insert(at, vm);
        return vm;
    }

    public void Load()
    {
        if (loaded || Dir is null) return;
        loaded = true;
        Children.Clear();
        var entries = Dir.Children.Take(MaxChildren).Select(d => new FolderVM(d, null, Dir.Size))
            .Concat(Dir.Files.Take(MaxChildren).Select(f => new FolderVM(null, f, Dir.Size)))
            .OrderByDescending(e => e.Size)
            .Take(MaxChildren)
            .ToList();
        foreach (var e in entries) Children.Add(e);
        int hidden = Dir.Children.Count + Dir.Files.Count - entries.Count;
        if (hidden > 0) Children.Add(new FolderVM(null, null, 0, $"… and {Format.Count(hidden)} smaller items"));
    }
}

/// <summary>One search result row.</summary>
public sealed record SearchRow(SearchHit Hit)
{
    SafetyLevel? level;

    public string Name => Hit.Name;
    public string Folder => System.IO.Path.GetDirectoryName(Hit.Path) ?? "";
    public string SizeText => Format.Bytes(Hit.Size);
    public string Icon => Hit.IsFolder ? "" : "";
    public SafetyLevel Level => level ??= SafetyRules.Classify(Hit.Path).Level;
}

public partial class ExplorerView : UserControl, IPage
{
    const int MaxResults = 500;

    readonly AppState state = AppState.Current;
    readonly System.Windows.Threading.DispatcherTimer searchDelay = new() { Interval = TimeSpan.FromMilliseconds(300) };
    int shownVersion = -1;
    FolderVM? rootVm;
    CancellationTokenSource? searchCts;

    public ExplorerView()
    {
        InitializeComponent();
        Details.ShowExplorerButton = false;
        foreach (var level in Enum.GetValues<SafetyLevel>())
        {
            var item = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 16, 0) };
            item.Children.Add(new System.Windows.Shapes.Ellipse
            {
                Width = 9, Height = 9, Fill = SafetyBrushes.Foreground(level), Margin = new Thickness(0, 1, 6, 0), VerticalAlignment = VerticalAlignment.Center,
            });
            item.Children.Add(new TextBlock { Text = SafetyVerdict.LevelText(level), FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
            Legend.Children.Add(item);
        }
        Tree.ContextMenu = ItemMenu.Build(
            () => Tree.SelectedItem is FolderVM { IsPlaceholder: false } v ? [v.Path] : [],
            includeExplorerView: false);
        state.ScanChanged += () =>
        {
            if (!IsVisible) return;
            Rebuild(null);
            if (SearchBox.Text.Length > 0) _ = SearchAsync();
        };
        searchDelay.Tick += (_, _) => { searchDelay.Stop(); _ = SearchAsync(); };
        Results.ContextMenu = ItemMenu.Build(() => Results.SelectedItem is SearchRow r ? [r.Hit.Path] : []);
    }

    // ---- search ----------------------------------------------------------------------------------

    void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        searchDelay.Stop();
        if (SearchBox.Text.Trim().Length == 0) { ShowTree(); return; }
        searchDelay.Start();
    }

    void Search_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) SearchBox.Clear();
        else if (e.Key == Key.Enter) { searchDelay.Stop(); _ = SearchAsync(); }
        else if (e.Key == Key.Down && Results.Items.Count > 0) { Results.SelectedIndex = 0; Results.Focus(); }
    }

    async Task SearchAsync()
    {
        var query = SearchBox.Text.Trim();
        if (query.Length == 0 || state.Scan is not { } scan)
        {
            if (state.Scan is null && query.Length > 0) { ResultsCard.Visibility = Visibility.Visible; TreeCard.Visibility = Visibility.Collapsed; ResultsSummary.Text = "Scan a drive first."; }
            return;
        }
        searchCts?.Cancel();
        var cts = searchCts = new CancellationTokenSource();
        ResultsSummary.Text = "Searching…";
        ResultsCard.Visibility = Visibility.Visible;
        TreeCard.Visibility = Visibility.Collapsed;

        int total = 0;
        var hits = await Task.Run(() => ScanSearch.Find(scan, query, MaxResults, out total, cts.Token));
        if (cts.IsCancellationRequested) return;
        Results.ItemsSource = hits.Select(h => new SearchRow(h)).ToList();
        ResultsSummary.Text = total == 0
            ? $"Nothing named \"{query}\" in this scan."
            : $"{Format.Count(total)} match(es) for \"{query}\"" + (total > hits.Count ? $" · showing the biggest {hits.Count}" : "") +
              " · double-click (or Enter) to show it in the tree";
    }

    void ShowTree()
    {
        searchCts?.Cancel();
        ResultsCard.Visibility = Visibility.Collapsed;
        TreeCard.Visibility = Visibility.Visible;
    }

    void ClearSearch_Click(object sender, RoutedEventArgs e) => SearchBox.Clear();

    void Results_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Results.SelectedItem is SearchRow r) Details.Show(r.Hit.Dir, r.Hit.File);
    }

    void Results_MouseDoubleClick(object sender, MouseButtonEventArgs e) => OpenSelectedResult();

    void Results_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) OpenSelectedResult();
        else if (e.Key == Key.Escape) SearchBox.Clear();
    }

    /// <summary>Shows the chosen result in the tree, expanded and selected.</summary>
    void OpenSelectedResult()
    {
        if (Results.SelectedItem is not SearchRow r) return;
        ShowTree();
        Reveal(r.Hit.Path, select: true);
    }

    public void OnNavigatedTo(object? argument)
    {
        if (shownVersion != state.ScanVersion || argument is string) Rebuild(argument as string);
    }

    void Rebuild(string? selectPath)
    {
        shownVersion = state.ScanVersion;
        if (state.Scan is not { } scan)
        {
            Tree.ItemsSource = null;
            Details.Clear("Scan a drive first.");
            Hint.Text = "Scan a drive first — choose a folder above and click Scan.";
            return;
        }

        // Remember what was open so a refresh after deleting something keeps the view.
        var expanded = rootVm is null ? [] : ExpandedPaths(rootVm).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selected = selectPath ?? (Tree.SelectedItem as FolderVM)?.Path;

        rootVm = new FolderVM(scan.Root, null, scan.Root.Size) { IsExpanded = true };
        Tree.ItemsSource = new[] { rootVm };
        Hint.Text = $"{scan.Root.FullPath} · {Format.Bytes(scan.TotalBytes)} · scanned {Format.Stamp(scan.Time)}" +
                    (scan.Cancelled ? " · scan was stopped, results are partial" : "") + ". Right-click for actions.";

        foreach (var p in expanded) Reveal(p, select: false);
        Reveal(selected ?? scan.Root.FullPath, select: true);
        // The top folder always starts open (a recycled tree row can push "collapsed" back into it).
        var top = rootVm;
        Dispatcher.BeginInvoke(() => top.IsExpanded = true, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    static IEnumerable<string> ExpandedPaths(FolderVM vm)
    {
        if (!vm.IsExpanded) yield break;
        yield return vm.Path;
        foreach (var c in vm.Children)
            foreach (var p in ExpandedPaths(c)) yield return p;
    }

    /// <summary>Expands the tree down to a path and optionally selects it.</summary>
    void Reveal(string path, bool select)
    {
        if (rootVm?.Dir is not { } root) return;
        var vm = rootVm;
        var prefix = root.FullPath.TrimEnd('\\') + "\\";
        if (!path.Equals(root.FullPath, StringComparison.OrdinalIgnoreCase))
        {
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return;
            foreach (var part in path[prefix.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
            {
                vm.IsExpanded = true;
                var next = vm.Children.FirstOrDefault(c => !c.IsPlaceholder &&
                    (c.Dir?.Name ?? c.File!.Name).Equals(part, StringComparison.OrdinalIgnoreCase))
                    ?? vm.Include(part); // a small item outside the biggest 300 shown
                if (next is null) break;
                vm = next;
            }
        }
        if (select)
        {
            vm.IsSelected = true;
            ShowDetails(vm);
            // Bringing the root into view would scroll to the bottom of its (huge) bounds — go to the top instead.
            if (vm == rootVm) Dispatcher.BeginInvoke(() => FindScrollViewer(Tree)?.ScrollToHome(), System.Windows.Threading.DispatcherPriority.Loaded);
            else Dispatcher.BeginInvoke(() =>
            {
                // Only the row itself, not the item with all its expanded children.
                if (FindContainer(Tree, vm) is { } item)
                    ((item.Template.FindName("PART_Header", item) as FrameworkElement) ?? item).BringIntoView();
            }, System.Windows.Threading.DispatcherPriority.Loaded);
        }
        else vm.IsExpanded = true;
    }

    static ScrollViewer? FindScrollViewer(DependencyObject d)
    {
        if (d is ScrollViewer sv) return sv;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
            if (FindScrollViewer(VisualTreeHelper.GetChild(d, i)) is { } found) return found;
        return null;
    }

    static TreeViewItem? FindContainer(ItemsControl parent, FolderVM target)
    {
        if (parent.ItemContainerGenerator.ContainerFromItem(target) is TreeViewItem direct) return direct;
        foreach (var item in parent.Items)
            if (parent.ItemContainerGenerator.ContainerFromItem(item) is TreeViewItem tvi && tvi.IsExpanded && FindContainer(tvi, target) is { } found)
                return found;
        return null;
    }

    void Tree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is FolderVM vm) ShowDetails(vm);
    }

    void ShowDetails(FolderVM vm)
    {
        if (vm.IsPlaceholder) return;
        Details.Show(vm.Dir, vm.File);
    }

    void Tree_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Right-click selects the row under the mouse first, like File Explorer.
        for (var d = e.OriginalSource as DependencyObject; d is not null;
             d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
        {
            if (d is TreeViewItem item)
            {
                item.IsSelected = true;
                item.Focus();
                return;
            }
        }
    }
}
