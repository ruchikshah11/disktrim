using System.Windows;
using DiskTrim.UI;

namespace DiskTrim.Setup;

/// <summary>
/// Install / uninstall entry points of DiskTrim.exe:
///   DiskTrim-Setup-1.0.exe                      → install wizard (any file name containing "setup")
///   DiskTrim.exe --install [--silent] [--all-users] [--no-desktop-shortcut] [--no-path]
///   DiskTrim.exe --uninstall [--silent] [--all-users] [--remove-data]   (what Settings › Apps runs)
/// </summary>
public static class SetupHost
{
    public static bool TryRun(string[] args, out int exitCode)
    {
        bool Has(string flag) => args.Any(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));
        bool install = Has("--install"), uninstall = Has("--uninstall");
        bool setupName = args.Length == 0 &&
                         Path.GetFileNameWithoutExtension(AppPaths.ExePath).Contains("setup", StringComparison.OrdinalIgnoreCase);
        bool offer = args.Length == 0 && !setupName && ShouldOfferInstall();
        exitCode = 0;
        if (!install && !uninstall && !setupName && !offer) return false;

        bool silent = Has("--silent"), allUsers = Has("--all-users");
        if (uninstall)
            exitCode = Uninstall(allUsers, silent, Has("--remove-data"), args);
        else if (install && silent)
            exitCode = SilentInstall(new InstallOptions(allUsers, !Has("--no-desktop-shortcut"), !Has("--no-path")));
        else
        {
            ConsoleWindow.Detach();
            exitCode = GuiHost.RunWindow(() => new SetupWindow(offerRunWithoutInstalling: offer));
        }
        return true;
    }

    /// <summary>
    /// A shipped DiskTrim.exe started from anywhere (Downloads, a USB stick, an e-mail attachment) installs itself
    /// when DiskTrim isn't installed yet or an older version is. The installed copy, the same version run from
    /// elsewhere, and developer builds (not single-file) simply run.
    /// </summary>
    static bool ShouldOfferInstall()
    {
        if (!string.IsNullOrEmpty(typeof(SetupHost).Assembly.Location)) return false; // dev build, not the published single file
        var installed = Installer.FindInstalled();
        if (installed is { } i && Installer.IsRunningFrom(i.Dir)) return false;           // this IS the installed copy
        if (Installer.IsRunningFrom(Installer.InstallDir(false)) || Installer.IsRunningFrom(Installer.InstallDir(true))) return false;
        return installed is null || installed.Value.Version != AppPaths.Version;
    }

    static int SilentInstall(InstallOptions o)
    {
        if (o.AllUsers && !AppPaths.IsAdmin)
        {
            Console.Error.WriteLine("--all-users needs administrator rights: run this from an elevated terminal.");
            return 2;
        }
        try
        {
            Installer.Install(o, s => Console.WriteLine(s));
            Console.WriteLine($"DiskTrim {AppPaths.Version} installed to {Installer.InstallDir(o.AllUsers)}");
            return 0;
        }
        catch (Exception ex)
        {
            AppPaths.LogError(ex);
            Console.Error.WriteLine("Installation failed: " + ex.Message);
            return 1;
        }
    }

    static int Uninstall(bool allUsers, bool silent, bool removeData, string[] args)
    {
        if (silent)
        {
            if (allUsers && !AppPaths.IsAdmin) { Console.Error.WriteLine("--all-users needs administrator rights."); return 2; }
            try { Installer.Uninstall(allUsers, removeData, s => Console.WriteLine(s)); return 0; }
            catch (Exception ex) { AppPaths.LogError(ex); Console.Error.WriteLine("Uninstall failed: " + ex.Message); return 1; }
        }

        ConsoleWindow.Detach();
        if (allUsers && !AppPaths.IsAdmin)
        {
            try
            {
                var psi = new ProcessStartInfo(AppPaths.ExePath) { UseShellExecute = true, Verb = "runas" };
                foreach (var a in args) psi.ArgumentList.Add(a);
                using var p = Process.Start(psi)!;
                p.WaitForExit();
                return p.ExitCode;
            }
            catch (System.ComponentModel.Win32Exception) { return 1; } // UAC declined
        }

        const string title = "Uninstall DiskTrim";
        if (MessageBox.Show($"Remove DiskTrim {AppPaths.Version} from this PC?\n\nThis removes the program, its shortcuts, " +
                            "the PATH entry and — if you turned them on — the scheduled cleanup and the right-click menu.",
                title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return 1;

        if (!removeData && Directory.Exists(AppPaths.DataDir))
            removeData = MessageBox.Show("Also delete your scan history, settings and deletion logs?\n\n" + AppPaths.DataDir,
                title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;

        try
        {
            Installer.Uninstall(allUsers, removeData, _ => { });
            MessageBox.Show("DiskTrim was uninstalled.", title, MessageBoxButton.OK, MessageBoxImage.Information);
            return 0;
        }
        catch (Exception ex)
        {
            AppPaths.LogError(ex);
            MessageBox.Show("Uninstall failed: " + ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
    }
}
