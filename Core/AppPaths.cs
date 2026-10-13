using System.Security.Principal;

namespace DiskTrim.Core;

/// <summary>Where the console edition keeps its data, plus a few facts about the running process.</summary>
public static class AppPaths
{
    // Separate from the GUI's %LocalAppData%\DiskTrim so the two never overwrite each other's settings.
    // DISKTRIM_DATA_DIR redirects everything (used by the tests).
    public static string DataDir { get; } = Environment.GetEnvironmentVariable("DISKTRIM_DATA_DIR") is { Length: > 0 } custom
        ? custom
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiskTrimConsole");

    public static string LogsDir => Ensure(Path.Combine(DataDir, "Logs"));
    public static string HistoryDir => Ensure(Path.Combine(DataDir, "History"));
    public static string SettingsFile => Path.Combine(Ensure(DataDir), "settings.json");
    public static string ErrorLog => Path.Combine(Ensure(DataDir), "errors.log");

    public static string ExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "DiskTrim.exe");

    public static string Version =>
        typeof(AppPaths).Assembly.GetName().Version is { } v
            ? (v.Build > 0 ? v.ToString(3) : v.ToString(2))
            : "1.0";

    /// <summary>"C:\" — the drive Windows is installed on.</summary>
    public static string SystemRoot { get; } = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";

    public static bool IsAdmin { get; } = CheckAdmin();

    static bool CheckAdmin()
    {
        try { return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); }
        catch { return false; }
    }

    public static void LogError(Exception ex)
    {
        try { File.AppendAllText(ErrorLog, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {ex}{Environment.NewLine}{Environment.NewLine}"); }
        catch { /* logging must never crash the app */ }
    }

    static string Ensure(string dir)
    {
        Directory.CreateDirectory(dir);
        return dir;
    }
}
