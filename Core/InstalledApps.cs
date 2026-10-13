using Microsoft.Win32;

namespace DiskTrim.Core;

public sealed record InstalledApp(
    string Name, string Publisher, string Version, long SizeBytes, DateTime? InstalledOn,
    string Scope, string? InstallLocation, string? UninstallString);

/// <summary>Programs listed in Settings > Apps (read from the Uninstall registry keys).</summary>
public static class InstalledApps
{
    const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public static List<InstalledApp> Read()
    {
        var list = new List<InstalledApp>();
        Read(RegistryHive.LocalMachine, RegistryView.Registry64, "All users", list);
        Read(RegistryHive.LocalMachine, RegistryView.Registry32, "All users", list);
        Read(RegistryHive.CurrentUser, RegistryView.Default, "Current user", list);
        return list
            .GroupBy(a => (a.Name.ToLowerInvariant(), a.Version))
            .Select(g => g.First())
            .OrderByDescending(a => a.SizeBytes)
            .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    static void Read(RegistryHive hive, RegistryView view, string scope, List<InstalledApp> into)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var key = baseKey.OpenSubKey(UninstallKey);
            if (key is null) return;
            foreach (var sub in key.GetSubKeyNames())
            {
                try
                {
                    using var k = key.OpenSubKey(sub);
                    if (k?.GetValue("DisplayName") is not string name || string.IsNullOrWhiteSpace(name)) continue;
                    if (k.GetValue("SystemComponent") is int sc && sc == 1) continue;
                    if (k.GetValue("ParentKeyName") is not null) continue; // updates/patches of another entry

                    long size = k.GetValue("EstimatedSize") is int kb ? (uint)kb * 1024L : 0;
                    DateTime? date = DateTime.TryParseExact(k.GetValue("InstallDate") as string, "yyyyMMdd",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

                    into.Add(new InstalledApp(
                        name.Trim(),
                        (k.GetValue("Publisher") as string ?? "").Trim(),
                        (k.GetValue("DisplayVersion") as string ?? "").Trim(),
                        size, date, scope,
                        k.GetValue("InstallLocation") as string,
                        k.GetValue("UninstallString") as string));
                }
                catch { /* one broken entry shouldn't hide the rest */ }
            }
        }
        catch { /* hive not readable */ }
    }

    /// <summary>Starts the program's own uninstaller. DiskTrim never deletes program folders itself.</summary>
    public static void StartUninstaller(InstalledApp app)
    {
        var cmd = app.UninstallString?.Trim();
        if (string.IsNullOrEmpty(cmd)) throw new InvalidOperationException($"{app.Name} doesn't report an uninstaller.");

        ProcessStartInfo psi;
        if (cmd.StartsWith("MsiExec.exe", StringComparison.OrdinalIgnoreCase))
        {
            // "MsiExec.exe /I{GUID}" opens the repair dialog; /X uninstalls.
            var args = cmd["MsiExec.exe".Length..].Trim().Replace("/I{", "/X{", StringComparison.OrdinalIgnoreCase);
            psi = new ProcessStartInfo("msiexec.exe", args) { UseShellExecute = true };
        }
        else
        {
            psi = new ProcessStartInfo("cmd.exe", $"/c \"{cmd}\"") { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden };
        }
        Process.Start(psi)?.Dispose();
    }
}
