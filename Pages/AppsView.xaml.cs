using System.Windows;
using System.Windows.Controls;
using DiskTrim.UI;

namespace DiskTrim.Pages;

public sealed record AppRow(InstalledApp App, long Size, string SizeSource, string? MeasuredFolder = null)
{
    public string Name => App.Name;
    public string Publisher => App.Publisher;
    public string Version => App.Version;
    public string SizeText => Size > 0 ? Format.Bytes(Size) : "—";
    public DateTime? InstalledOn => App.InstalledOn;
    public string InstalledText => App.InstalledOn is { } d ? Format.Date(d) : "";
    public string Scope => App.Scope;
}

public partial class AppsView : UserControl, IPage
{
    readonly AppState state = AppState.Current;
    List<AppRow> all = [];
    bool loaded;

    public AppsView()
    {
        InitializeComponent();
    }

    public void OnNavigatedTo(object? argument)
    {
        if (!loaded) _ = LoadAsync();
    }

    async Task LoadAsync()
    {
        loaded = true;
        Summary.Text = "Reading installed programs…";
        var scan = state.Scan;
        all = await Task.Run(() =>
        {
            var rows = InstalledApps.Read().Select(a =>
            {
                // Programs often under-report their size; use the scan when it covers the install folder.
                if (scan is not null && !string.IsNullOrWhiteSpace(a.InstallLocation) &&
                    scan.Find(a.InstallLocation.Trim().Trim('"')) is { Size: > 0 } node)
                    return new AppRow(a, node.Size, "measured", node.FullPath);
                return new AppRow(a, a.SizeBytes, a.SizeBytes > 0 ? "reported" : "");
            }).ToList();

            // Office, Visio, OneNote … often share one install folder: say so instead of counting it once per app.
            var shared = rows.Where(r => r.MeasuredFolder is not null)
                .GroupBy(r => r.MeasuredFolder!, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .SelectMany(g => g.Select(r => (Row: r, Count: g.Count())))
                .ToDictionary(x => x.Row, x => x.Count);
            return rows.Select(r => shared.TryGetValue(r, out var n) ? r with { SizeSource = $"shared by {n} apps" } : r)
                       .OrderByDescending(r => r.Size).ToList();
        });
        ApplyFilter();
    }

    void ApplyFilter()
    {
        var q = SearchBox.Text.Trim();
        var list = q.Length == 0 ? all : all.Where(a => a.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                                                        || a.Publisher.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        Grid.ItemsSource = list;
        // A shared install folder counts once.
        long total = list.Where(a => a.MeasuredFolder is null).Sum(a => a.Size)
                   + list.Where(a => a.MeasuredFolder is not null)
                         .GroupBy(a => a.MeasuredFolder!, StringComparer.OrdinalIgnoreCase).Sum(g => g.First().Size);
        Summary.Text = $"{list.Count} programs · {Format.Bytes(total)} in total." +
                       (state.Scan is null ? " Scan C: to replace reported sizes with measured ones." : "");
    }

    void Search_Changed(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ApplyFilter();
    }

    async void Reload_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (Grid.SelectedItem is not AppRow row) { Dialogs.Info("Select a program first."); return; }
        var folder = row.App.InstallLocation?.Trim().Trim('"');
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) Dialogs.Info($"{row.Name} doesn't report an install folder.");
        else FileActions.Open(folder);
    }

    void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        if (Grid.SelectedItem is not AppRow row) { Dialogs.Info("Select a program first."); return; }
        var text = $"Start the uninstaller of {row.Name}?\n\nPublisher: {row.Publisher}\nSize: {row.SizeText}\nVersion: {row.Version}\n\n" +
                   "The program's own uninstaller opens and you finish there. DiskTrim never deletes program folders itself.";
        if (!Dialogs.Confirm("Uninstall program?", text)) return;
        try
        {
            InstalledApps.StartUninstaller(row.App);
            state.Status = $"Started the uninstaller of {row.Name}. Click Reload when it's finished.";
        }
        catch (Exception ex)
        {
            Dialogs.Error("Couldn't start the uninstaller: " + ex.Message);
        }
    }
}
