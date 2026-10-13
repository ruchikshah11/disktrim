using System.Windows;
using System.Windows.Controls;
using DiskTrim.UI;

namespace DiskTrim.Pages;

/// <summary>Side panel: what a folder or file is, how big, and whether deleting it is safe.</summary>
public partial class ItemDetails : UserControl
{
    string? path;

    public ItemDetails()
    {
        InitializeComponent();
    }

    public bool ShowExplorerButton { set => ExplorerButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed; }
    public bool ShowTreemapButton { set => TreemapButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed; }

    public void Clear(string? placeholder = null)
    {
        path = null;
        Panel.Visibility = Visibility.Collapsed;
        Placeholder.Visibility = Visibility.Visible;
        if (placeholder is not null) Placeholder.Text = placeholder;
    }

    public void Show(DirNode? dir, FileEntry? file, string? groupLabel = null)
    {
        if (dir is null && file is null) { Clear(); return; }
        path = dir?.FullPath ?? file!.FullPath;
        var v = SafetyRules.Classify(path);

        Placeholder.Visibility = Visibility.Collapsed;
        Panel.Visibility = Visibility.Visible;
        NameText.Text = groupLabel ?? (dir is not null ? (dir.Parent is null ? dir.FullPath : dir.Name) : file!.Name);
        PathText.Text = path;

        if (dir is not null)
        {
            SizeText.Text = Format.Bytes(dir.Size);
            CountText.Text = $"{Format.Count(dir.FileCount)} files in {Format.Count(dir.FolderCount)} folders" +
                             (dir.LastChange > DateTime.MinValue ? $" · last change {Format.Stamp(dir.LastChange)}" : "") +
                             (dir.IsLink ? " · link — not followed" : dir.Unreadable ? " · access denied" : "");
        }
        else
        {
            SizeText.Text = Format.Bytes(file!.Size);
            CountText.Text = $"{FileCategories.Of(file.Extension)} · modified {Format.Stamp(file.Modified)}" +
                             (file.IsOnlineOnly ? " · online-only OneDrive file (uses no local space)" : "");
        }

        Badge.DataContext = v.Level;
        VerdictTitle.Text = v.Title;
        ReasonText.Text = v.Reason;
        ImpactText.Text = "If you delete it: " + v.Impact;
    }

    IReadOnlyList<string> Selection => path is null ? [] : [path];

    void Open_Click(object sender, RoutedEventArgs e) { if (path is not null) FileActions.ShowInExplorer(path); }
    void Copy_Click(object sender, RoutedEventArgs e) => FileActions.CopyPaths(Selection);
    async void InUse_Click(object sender, RoutedEventArgs e) => await FileActions.CheckInUseAsync(Selection);
    void Explorer_Click(object sender, RoutedEventArgs e) { if (path is not null) AppState.Current.Navigate("explorer", path); }
    void Treemap_Click(object sender, RoutedEventArgs e) { if (path is not null) AppState.Current.Navigate("treemap", path); }
    async void Recycle_Click(object sender, RoutedEventArgs e) => await FileActions.RecycleAsync(Selection);
    async void Move_Click(object sender, RoutedEventArgs e) => await FileActions.MoveAsync(Selection);
}
