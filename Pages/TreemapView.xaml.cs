using System.Windows;
using System.Windows.Controls;
using DiskTrim.Controls;
using DiskTrim.UI;

namespace DiskTrim.Pages;

public partial class TreemapView : UserControl, IPage
{
    readonly AppState state = AppState.Current;
    int shownVersion = -1;

    public TreemapView()
    {
        InitializeComponent();
        Details.ShowTreemapButton = false;
        Details.Clear("Click a block for details.");

        foreach (var level in Enum.GetValues<SafetyLevel>())
        {
            var badge = new Border { Style = (Style)FindResource("Badge"), DataContext = level, Margin = new Thickness(6, 0, 0, 0) };
            badge.Child = new TextBlock { Style = (Style)FindResource("BadgeText") };
            Legend.Children.Add(badge);
        }

        Map.BlockSelected += b =>
        {
            if (b.IsGroup) Details.Clear($"{b.Label} ({Format.Bytes(b.Size)}) — many small files grouped together.");
            else Details.Show(b.Folder, b.File);
        };
        Map.BlockHovered += b => HoverText.Text = b is null
            ? "Click a block for details."
            : $"{b.Path ?? b.Label}   ·   {Format.Bytes(b.Size)}   ·   {SafetyVerdict.LevelText(b.Level)}";
        Map.ZoomRequested += ShowFolder;
        Map.ContextMenu = ItemMenu.Build(() => selectedPath is null ? [] : [selectedPath], includeTreemap: false);
        Map.BlockSelected += b => selectedPath = b.Path;

        state.ScanChanged += () => { if (IsVisible) Reload(null); };
    }

    string? selectedPath;

    public void OnNavigatedTo(object? argument)
    {
        if (argument is string path || shownVersion != state.ScanVersion) Reload(argument as string);
    }

    void Reload(string? path)
    {
        shownVersion = state.ScanVersion;
        if (state.Scan is not { } scan)
        {
            Map.Root = null;
            Empty.Visibility = Visibility.Visible;
            CurrentText.Text = "";
            return;
        }
        Empty.Visibility = Visibility.Collapsed;

        // Keep the zoomed folder when the scan was only updated (files deleted); otherwise start at the top.
        var target = path is not null ? scan.Find(path) ?? scan.Find(Path.GetDirectoryName(path) ?? "")
                   : Map.Root is { } current ? scan.Find(current.FullPath) : null;
        ShowFolder(target ?? scan.Root);
        Map.Refresh();
    }

    void ShowFolder(DirNode node)
    {
        Map.Root = node;
        CurrentText.Text = $"{node.FullPath}  ·  {Format.Bytes(node.Size)}  ·  {Format.Count(node.FileCount)} files";
        UpButton.IsEnabled = node.Parent is not null;
        Details.Show(node, null);
        selectedPath = node.FullPath;
    }

    void Up_Click(object sender, RoutedEventArgs e)
    {
        if (Map.Root?.Parent is { } parent) ShowFolder(parent);
    }

    void Top_Click(object sender, RoutedEventArgs e)
    {
        if (state.Scan is { } scan) ShowFolder(scan.Root);
    }
}
