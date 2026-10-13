using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DiskTrim.Pages;
using DiskTrim.UI;
using Microsoft.Win32;

namespace DiskTrim;

public sealed class NavEntry(string key, string title, string icon, Func<FrameworkElement> create) : Observable
{
    FrameworkElement? page;
    string badge = "";

    public string Key { get; } = key;
    public string Title { get; } = title;
    public string Icon { get; } = icon;
    public FrameworkElement Page => page ??= create();

    public string Badge
    {
        get => badge;
        set { if (Set(ref badge, value)) Raise(nameof(HasBadge)); }
    }

    public bool HasBadge => Badge.Length > 0;
}

/// <summary>A page that wants to know when it is shown (and with which argument, e.g. a folder path).</summary>
public interface IPage
{
    void OnNavigatedTo(object? argument);
}

public partial class MainWindow : Window
{
    readonly List<NavEntry> entries;
    readonly AppState state = AppState.Current;
    object? pendingArgument;

    public MainWindow(string? startPath)
    {
        InitializeComponent();
        DataContext = state;

        entries =
        [
            new("home", "Home", "", () => new HomeView()),
            new("cleanup", "Cleanup", "", () => new CleanupView()),
            new("explorer", "Explorer", "", () => new ExplorerView()),
            new("treemap", "Treemap", "", () => new TreemapView()),
            new("files", "Large & old files", "", () => new FilesView()),
            new("duplicates", "Duplicates", "", () => new DuplicatesView()),
            new("types", "File types", "", () => new TypesView()),
            new("apps", "Installed apps", "", () => new AppsView()),
            new("history", "History", "", () => new HistoryView()),
            new("settings", "Settings", "", () => new SettingsView()),
        ];
        Nav.ItemsSource = entries;

        Title = "DiskTrim" + (AppPaths.IsAdmin ? " (Administrator)" : "");
        VersionText.Text = $"Version {AppPaths.Version}" + (AppPaths.IsAdmin ? " · administrator" : "");
        AdminButton.Visibility = AppPaths.IsAdmin ? Visibility.Collapsed : Visibility.Visible;

        PathBox.ItemsSource = DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed).Select(d => d.Name).ToList();
        PathBox.Text = startPath is null ? AppPaths.SystemRoot : Scanner.NormalizeRoot(startPath);

        state.NavigateRequested += Navigate;
        state.CleanupMeasured += () =>
        {
            var safe = state.CleanupItems?.Where(i => i.Level == SafetyLevel.Safe && !i.IsBlocked).Sum(i => i.Bytes) ?? 0;
            entries[1].Badge = safe >= 1024 * 1024 ? Format.Bytes(safe) : "";
        };

        // Navigate once the ListBox exists on screen; selecting earlier gets reset to the first item.
        Loaded += async (_, _) =>
        {
            Navigate(startPath is null ? "home" : "explorer", null);
            if (startPath is not null) await state.ScanAsync(PathBox.Text);
        };
        Closing += (_, _) => state.StopScan();
    }

    void Navigate(string key, object? argument)
    {
        var entry = entries.First(e => e.Key == key);
        pendingArgument = argument;
        if (Nav.SelectedItem == entry) Show(entry);
        else Nav.SelectedItem = entry; // triggers Nav_SelectionChanged → Show
    }

    void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Nav.SelectedItem is NavEntry entry) Show(entry);
    }

    void Show(NavEntry entry)
    {
        PageHost.Content = entry.Page;
        var arg = pendingArgument;
        pendingArgument = null;
        (entry.Page as IPage)?.OnNavigatedTo(arg);
    }

    async void Scan_Click(object sender, RoutedEventArgs e) => await state.ScanAsync(PathBox.Text);

    void Stop_Click(object sender, RoutedEventArgs e) => state.StopScan();

    async void PathBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !state.IsBusy) await state.ScanAsync(PathBox.Text);
    }

    void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Folder to analyse" };
        if (Directory.Exists(PathBox.Text)) dlg.InitialDirectory = PathBox.Text;
        if (dlg.ShowDialog(this) == true) PathBox.Text = dlg.FolderName;
    }

    async void Report_Click(object sender, RoutedEventArgs e) => await FileActions.SaveReportAsync(this);

    void Admin_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var psi = new ProcessStartInfo(AppPaths.ExePath) { UseShellExecute = true, Verb = "runas" };
            if (state.Scan is { } s) { psi.ArgumentList.Add("--path"); psi.ArgumentList.Add(s.Root.FullPath); }
            Process.Start(psi)?.Dispose();
            Close();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            state.Status = "Administrator rights were not granted.";
        }
    }
}
