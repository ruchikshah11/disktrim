using Microsoft.Win32;

namespace DiskTrim.Core;

public sealed record InstallOptions(bool AllUsers, bool DesktopShortcut = true, bool AddToPath = true);

/// <summary>
/// Installs / uninstalls DiskTrim: program folder, Start menu and desktop shortcuts, PATH, and the
/// Settings > Apps entry. The same DiskTrim.exe is the installer (renamed DiskTrim-Setup-x.y.exe) and the app.
/// </summary>
public static class Installer
{
    public const string AppName = "DiskTrim";
    const string ExeName = "DiskTrim.exe";
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + AppName;
    const string Description = "Find what fills your disk and free space safely";

    public static string InstallDir(bool allUsers) => allUsers
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), AppName)
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppName);

    static string StartMenuDir(bool allUsers) => Environment.GetFolderPath(allUsers
        ? Environment.SpecialFolder.CommonPrograms : Environment.SpecialFolder.Programs);

    static string DesktopDir(bool allUsers) => Environment.GetFolderPath(allUsers
        ? Environment.SpecialFolder.CommonDesktopDirectory : Environment.SpecialFolder.DesktopDirectory);

    static RegistryKey Hive(bool allUsers) => allUsers ? Registry.LocalMachine : Registry.CurrentUser;

    /// <summary>The version installed for this user or all users, if any.</summary>
    public static (bool AllUsers, string Version, string Dir)? FindInstalled()
    {
        foreach (var all in new[] { false, true })
        {
            using var key = Hive(all).OpenSubKey(UninstallKey);
            if (key?.GetValue("InstallLocation") is string dir)
                return (all, key.GetValue("DisplayVersion") as string ?? "", dir);
        }
        return null;
    }

    /// <summary>True when this process runs from an installed copy (so it must not delete itself while running).</summary>
    public static bool IsRunningFrom(string dir) =>
        Path.GetDirectoryName(AppPaths.ExePath)!.TrimEnd('\\').Equals(dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    public static void Install(InstallOptions o, Action<string> progress)
    {
        var dir = InstallDir(o.AllUsers);
        var target = Path.Combine(dir, ExeName);
        var source = AppPaths.ExePath;

        progress("Closing DiskTrim if it is running…");
        CloseRunning(target);

        progress("Copying DiskTrim.exe…");
        Directory.CreateDirectory(dir);
        if (!source.Equals(target, StringComparison.OrdinalIgnoreCase))
            CopyWithRetry(source, target);

        progress("Adding shortcuts…");
        CreateShortcut(Path.Combine(StartMenuDir(o.AllUsers), AppName + ".lnk"), target);
        var desktopLnk = Path.Combine(DesktopDir(o.AllUsers), AppName + ".lnk");
        if (o.DesktopShortcut) CreateShortcut(desktopLnk, target);
        else if (File.Exists(desktopLnk)) File.Delete(desktopLnk);

        if (o.AddToPath)
        {
            progress("Adding DiskTrim to PATH…");
            EditPath(o.AllUsers, dir, add: true);
        }

        progress("Registering in Settings > Apps…");
        using (var key = Hive(o.AllUsers).CreateSubKey(UninstallKey))
        {
            string uninstall = $"\"{target}\" --uninstall" + (o.AllUsers ? " --all-users" : "");
            key.SetValue("DisplayName", AppName);
            key.SetValue("DisplayVersion", AppPaths.Version);
            key.SetValue("Publisher", AppName);
            key.SetValue("DisplayIcon", target + ",0");
            key.SetValue("InstallLocation", dir);
            key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
            key.SetValue("UninstallString", uninstall);
            key.SetValue("QuietUninstallString", uninstall + " --silent");
            key.SetValue("Comments", Description);
            key.SetValue("EstimatedSize", (int)Math.Ceiling(new FileInfo(target).Length / 1024.0), RegistryValueKind.DWord);
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        }
        BroadcastEnvironmentChange();
        progress("Done.");
    }

    /// <param name="removeData">Also delete scan history, settings and deletion logs.</param>
    public static void Uninstall(bool allUsers, bool removeData, Action<string> progress)
    {
        var dir = InstallDir(allUsers);
        var exe = Path.Combine(dir, ExeName);

        progress("Closing DiskTrim if it is running…");
        CloseRunning(exe);

        progress("Removing scheduled cleanup and right-click menu…");
        try { if (ScheduledCleanup.IsScheduled()) ScheduledCleanup.Delete(); } catch { /* not there */ }
        try { ShellIntegration.Uninstall(); } catch { /* not there */ }

        progress("Removing shortcuts…");
        foreach (var lnk in new[] { Path.Combine(StartMenuDir(allUsers), AppName + ".lnk"), Path.Combine(DesktopDir(allUsers), AppName + ".lnk") })
            if (File.Exists(lnk)) File.Delete(lnk);

        progress("Removing from PATH…");
        EditPath(allUsers, dir, add: false);

        progress("Removing from Settings > Apps…");
        Hive(allUsers).DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
        BroadcastEnvironmentChange();

        if (removeData && Directory.Exists(AppPaths.DataDir))
        {
            progress("Removing scan history, settings and logs…");
            try { Directory.Delete(AppPaths.DataDir, recursive: true); } catch { /* in use */ }
        }

        progress("Removing program files…");
        if (IsRunningFrom(dir))
        {
            // A running exe can't delete itself: let a hidden cmd remove the folder a moment after we exit.
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping 127.0.0.1 -n 3 > nul & rmdir /s /q \"{dir}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden,
            })?.Dispose();
        }
        else if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
        progress("Done.");
    }

    // ---- helpers ---------------------------------------------------------------------------------

    static void CloseRunning(string exe)
    {
        int self = Environment.ProcessId;
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)))
        {
            using (p)
            {
                if (p.Id == self) continue;
                string? path = null;
                try { path = p.MainModule?.FileName; } catch { /* other user / elevated */ }
                if (path is null || !path.Equals(exe, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    p.CloseMainWindow();
                    if (!p.WaitForExit(3000)) { p.Kill(); p.WaitForExit(3000); }
                }
                catch { /* already gone */ }
            }
        }
    }

    static void CopyWithRetry(string source, string target)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Copy(source, target, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < 10)
            {
                Thread.Sleep(500); // the old exe may still be closing, or antivirus is scanning it
            }
        }
    }

    static void CreateShortcut(string lnkPath, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(lnkPath)!);
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("Windows Script Host is not available.");
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic lnk = shell.CreateShortcut(lnkPath);
            lnk.TargetPath = target;
            lnk.WorkingDirectory = Path.GetDirectoryName(target);
            lnk.IconLocation = target + ",0";
            lnk.Description = Description;
            lnk.Save();
            Marshal.FinalReleaseComObject(lnk);
        }
        finally { Marshal.FinalReleaseComObject(shell); }
    }

    /// <summary>Adds or removes only our own entry; every other character of PATH stays as it was.</summary>
    static void EditPath(bool allUsers, string dir, bool add)
    {
        using var key = allUsers
            ? Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Environment", writable: true)
            : Registry.CurrentUser.OpenSubKey("Environment", writable: true);
        if (key is null) return;
        var current = key.GetValue("Path", "", RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";
        var parts = current.Split(';');
        bool present = parts.Any(p => p.Equals(dir, StringComparison.OrdinalIgnoreCase));

        if (add && !present)
        {
            var updated = current.Length == 0 ? dir : current.EndsWith(';') ? current + dir + ";" : current + ";" + dir;
            key.SetValue("Path", updated, RegistryValueKind.ExpandString);
        }
        else if (!add && present)
        {
            key.SetValue("Path", string.Join(';', parts.Where(p => !p.Equals(dir, StringComparison.OrdinalIgnoreCase))),
                RegistryValueKind.ExpandString);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, UIntPtr wParam, string lParam, uint flags, uint timeout, out UIntPtr result);

    /// <summary>Lets Explorer and new terminals pick up the PATH change without signing out.</summary>
    static void BroadcastEnvironmentChange() =>
        SendMessageTimeout((IntPtr)0xffff, 0x001A, UIntPtr.Zero, "Environment", 0x0002, 3000, out _);
}
