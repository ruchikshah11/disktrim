using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DiskTrim.UI;
using Microsoft.Win32;

namespace DiskTrim.Pages;

public sealed record LogRow(FileInfo File)
{
    public string Path => File.FullName;
    public string Name => File.Name;
    public string TimeText => Format.Stamp(File.LastWriteTime);
    public string SizeText => Format.Bytes(File.Length);
}

public partial class SettingsView : UserControl, IPage
{
    static readonly string[] Themes = ["System", "Light", "Dark"];
    static readonly string[] Frequencies = ["Daily", "Weekly", "Monthly"];

    readonly AppState state = AppState.Current;
    bool loading;

    public SettingsView()
    {
        InitializeComponent();
        ThemeBox.ItemsSource = Themes;
        FrequencyBox.ItemsSource = Frequencies;
        FrequencyBox.SelectedIndex = 1;
    }

    public void OnNavigatedTo(object? argument) => Load();

    void Load()
    {
        loading = true;
        var s = AppSettings.Load();
        ThemeBox.SelectedItem = Themes.Contains(s.Theme) ? s.Theme : "System";
        loading = false;

        Exclusions.ItemsSource = s.Exclusions.ToList();
        NoExclusions.Visibility = s.Exclusions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        bool scheduled = ScheduledCleanup.IsScheduled();
        ScheduleStatus.Text = scheduled
            ? $"Runs from Task Scheduler › {ScheduledCleanup.TaskName}. Only 'Safe to delete' items are cleaned."
            : "Clean the 'Safe to delete' items automatically, without opening DiskTrim.";
        SetPill(scheduled);
        UnscheduleButton.IsEnabled = scheduled;
        ScheduleHint.Text = (AppPaths.IsAdmin
                ? "Runs with administrator rights (includes Windows temp, update cache, crash dumps)."
                : "Runs as you (only your own temp files and caches). Run DiskTrim as administrator to include system items.")
            + " Keep DiskTrim in this folder — the task points at this exact file.";

        bool shell = ShellIntegration.IsInstalled();
        ShellStatus.Text = shell
            ? "Right-click any folder or drive → 'Analyze with DiskTrim' (on Windows 11 it's under 'Show more options')."
            : "Add 'Analyze with DiskTrim' when you right-click a folder or drive.";
        ShellButton.Content = shell ? "Remove" : "Add";

        var logs = AuditLog.List().Take(15).Select(f => new LogRow(f)).ToList();
        Logs.ItemsSource = logs;
        NoLogs.Visibility = logs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        AboutTitle.Text = $"DiskTrim {AppPaths.Version}";
        AboutText.Text = $".NET {Environment.Version} · " + (AppPaths.IsAdmin ? "running as administrator" : "running as standard user") +
                         $" · {AppPaths.ExePath}\nThe same program runs in the console: DiskTrim help";
    }

    void SetPill(bool on)
    {
        var level = on ? SafetyLevel.Safe : SafetyLevel.Review;
        SchedulePill.Background = on ? SafetyBrushes.Background(level) : (Brush)FindResource("ControlStrokeColorDefaultBrush");
        SchedulePillText.Foreground = on ? SafetyBrushes.Foreground(level) : (Brush)FindResource("TextFillColorSecondaryBrush");
        SchedulePillText.Text = on ? "On" : "Off";
    }

    void Theme_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (loading || ThemeBox.SelectedItem is not string theme) return;
        var s = AppSettings.Load();
        s.Theme = theme;
        s.Save();
        GuiHost.ApplyTheme(theme);
    }

    void AddExclusion_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Folder to exclude from scans" };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        var s = AppSettings.Load();
        var path = Scanner.NormalizeRoot(dlg.FolderName);
        if (!s.Exclusions.Contains(path, StringComparer.OrdinalIgnoreCase)) s.Exclusions.Add(path);
        s.Save();
        state.Status = $"Saved {s.Exclusions.Count} exclusion(s). They apply to the next scan.";
        Load();
    }

    void RemoveExclusion_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string path) return;
        var s = AppSettings.Load();
        s.Exclusions.RemoveAll(x => x.Equals(path, StringComparison.OrdinalIgnoreCase));
        s.Save();
        Load();
    }

    void Schedule_Click(object sender, RoutedEventArgs e)
    {
        var time = TimeBox.Text.Trim();
        if (!TimeOnly.TryParseExact(time, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            Dialogs.Info("Enter the time as HH:mm, for example 12:30.");
            return;
        }
        var freq = (string)FrequencyBox.SelectedItem;
        if (!Dialogs.Confirm("Schedule automatic cleanup?",
                $"DiskTrim will clean the 'Safe to delete' items {freq.ToLowerInvariant()} at {time}.\n\nProgram: {AppPaths.ExePath}\n\n{ScheduleHint.Text}"))
            return;
        var (code, output) = ScheduledCleanup.Create(freq, time);
        if (code == 0) state.Status = $"Scheduled: {freq} at {time}";
        else Dialogs.Error(output);
        Load();
    }

    void Unschedule_Click(object sender, RoutedEventArgs e)
    {
        if (!ScheduledCleanup.IsScheduled()) { Dialogs.Info("Nothing to turn off — automatic cleanup wasn't scheduled."); return; }
        var (code, output) = ScheduledCleanup.Delete();
        if (code == 0) state.Status = "Scheduled cleanup removed";
        else Dialogs.Error(output);
        Load();
    }

    void Shell_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (ShellIntegration.IsInstalled())
            {
                ShellIntegration.Uninstall();
                state.Status = "Removed from the right-click menu.";
            }
            else
            {
                ShellIntegration.Install();
                state.Status = "Added to the right-click menu.";
            }
        }
        catch (Exception ex) { Dialogs.Error("Couldn't change the right-click menu: " + ex.Message); }
        Load();
    }

    async void Report_Click(object sender, RoutedEventArgs e) => await FileActions.SaveReportAsync(Window.GetWindow(this));

    void OpenLogs_Click(object sender, RoutedEventArgs e) => FileActions.Open(AppPaths.LogsDir);

    void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string path) FileActions.Open(path);
    }

    void OpenData_Click(object sender, RoutedEventArgs e) => FileActions.Open(AppPaths.DataDir);
}
