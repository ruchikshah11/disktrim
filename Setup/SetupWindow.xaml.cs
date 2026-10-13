using System.Windows;
using DiskTrim.UI;

namespace DiskTrim.Setup;

/// <summary>The install wizard shown when DiskTrim.exe runs under a setup name (DiskTrim-Setup-1.0.exe).</summary>
public partial class SetupWindow : Window
{
    enum Step { Options, Working, Done }

    Step step = Step.Options;
    readonly bool offerPortable;
    bool succeeded;
    string? installedExe;

    /// <param name="offerRunWithoutInstalling">Shown when a plain DiskTrim.exe was double-clicked (portable use).</param>
    public SetupWindow(bool offerRunWithoutInstalling = false)
    {
        InitializeComponent();
        offerPortable = offerRunWithoutInstalling;
        PortableButton.Visibility = offerPortable ? Visibility.Visible : Visibility.Collapsed;
        HeaderText.Text = $"Install DiskTrim {AppPaths.Version}";

        if (Installer.FindInstalled() is { } existing)
        {
            UpdateNote.Visibility = Visibility.Visible;
            UpdateText.Text = existing.Version == AppPaths.Version
                ? $"DiskTrim {existing.Version} is already installed. Installing again repairs it."
                : $"DiskTrim {existing.Version} is installed. This updates it to {AppPaths.Version}; your scan history and settings are kept.";
            AllUsersBox.IsChecked = existing.AllUsers;
        }
        UpdateLocation();
    }

    void AllUsers_Changed(object sender, RoutedEventArgs e) => UpdateLocation();

    void UpdateLocation()
    {
        if (LocationText is null) return;
        LocationText.Text = Installer.InstallDir(AllUsersBox.IsChecked == true);
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>Run this copy as the app, without installing anything.</summary>
    void Portable_Click(object sender, RoutedEventArgs e)
    {
        var psi = new ProcessStartInfo(AppPaths.ExePath) { UseShellExecute = true };
        psi.ArgumentList.Add("--portable");
        Process.Start(psi)?.Dispose();
        Close();
    }

    async void Main_Click(object sender, RoutedEventArgs e)
    {
        if (step == Step.Done)
        {
            if (succeeded && LaunchBox.IsChecked == true && installedExe is not null)
                Process.Start(new ProcessStartInfo(installedExe) { UseShellExecute = true })?.Dispose();
            Close();
            return;
        }

        var options = new InstallOptions(AllUsersBox.IsChecked == true, DesktopBox.IsChecked == true, PathBox.IsChecked == true);
        Show(Step.Working);
        string? error = null;

        if (options.AllUsers && !AppPaths.IsAdmin)
        {
            // Program Files needs administrator rights: run the same setup elevated, silently, and wait.
            ProgressText.Text = "Waiting for administrator approval…";
            error = await Task.Run(() => RunElevated(options));
        }
        else
        {
            try
            {
                await Task.Run(() => Installer.Install(options, s => Dispatcher.BeginInvoke(() => ProgressText.Text = s)));
            }
            catch (Exception ex)
            {
                AppPaths.LogError(ex);
                error = ex.Message;
            }
        }

        succeeded = error is null;
        installedExe = Path.Combine(Installer.InstallDir(options.AllUsers), "DiskTrim.exe");
        DoneTitle.Text = succeeded ? $"DiskTrim {AppPaths.Version} is installed." : "Installation failed";
        DoneIcon.Text = succeeded ? "" : "";
        DoneText.Text = succeeded
            ? $"Start it from the Start menu{(options.DesktopShortcut ? " or the desktop" : "")}" +
              (options.AddToPath ? ", or type 'DiskTrim help' in a new terminal" : "") +
              ". Uninstall from Settings › Apps › DiskTrim."
            : error!;
        LaunchBox.Visibility = succeeded ? Visibility.Visible : Visibility.Collapsed;
        Show(Step.Done);
    }

    static string? RunElevated(InstallOptions o)
    {
        var psi = new ProcessStartInfo(AppPaths.ExePath) { UseShellExecute = true, Verb = "runas" };
        foreach (var a in new[] { "--install", "--silent", "--all-users" }) psi.ArgumentList.Add(a);
        if (!o.DesktopShortcut) psi.ArgumentList.Add("--no-desktop-shortcut");
        if (!o.AddToPath) psi.ArgumentList.Add("--no-path");
        try
        {
            using var p = Process.Start(psi)!;
            p.WaitForExit();
            return p.ExitCode == 0 ? null : $"The installation for all users failed (code {p.ExitCode}). Details are in {AppPaths.ErrorLog}.";
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return "Administrator rights were not granted. Untick 'Install for all users' to install just for you.";
        }
    }

    void Show(Step s)
    {
        step = s;
        OptionsPanel.Visibility = s == Step.Options ? Visibility.Visible : Visibility.Collapsed;
        ProgressPanel.Visibility = s == Step.Working ? Visibility.Visible : Visibility.Collapsed;
        DonePanel.Visibility = s == Step.Done ? Visibility.Visible : Visibility.Collapsed;
        MainButton.IsEnabled = s != Step.Working;
        CancelButton.Visibility = s == Step.Options ? Visibility.Visible : Visibility.Collapsed;
        PortableButton.Visibility = s == Step.Options && offerPortable ? Visibility.Visible : Visibility.Collapsed;
        MainButton.Content = s == Step.Done ? "Finish" : "Install";
    }
}
