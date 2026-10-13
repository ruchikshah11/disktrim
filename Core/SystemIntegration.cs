using Microsoft.Win32;

namespace DiskTrim.Core;

/// <summary>Runs "DiskTrim --clean-safe" on a schedule through Windows Task Scheduler.</summary>
public static class ScheduledCleanup
{
    public const string TaskName = @"DiskTrimConsole\Safe cleanup";

    public static bool IsScheduled() => Schtasks("/Query", "/TN", TaskName).ExitCode == 0;

    /// <param name="frequency">DAILY, WEEKLY or MONTHLY.</param>
    /// <param name="time">HH:mm</param>
    public static (int ExitCode, string Output) Create(string frequency, string time)
    {
        var args = new List<string>
        {
            "/Create", "/F", "/TN", TaskName,
            "/TR", $"\"{AppPaths.ExePath}\" --clean-safe",
            "/SC", frequency.ToUpperInvariant(), "/ST", time,
        };
        // As administrator the task also cleans system items (Windows temp, update cache, crash dumps).
        if (AppPaths.IsAdmin) args.AddRange(["/RL", "HIGHEST"]);
        return Schtasks([.. args]);
    }

    public static (int ExitCode, string Output) Delete() => Schtasks("/Delete", "/F", "/TN", TaskName);

    static (int ExitCode, string Output) Schtasks(params string[] args)
    {
        var psi = new ProcessStartInfo("schtasks.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output.Trim());
    }
}

/// <summary>Right-click any folder or drive → "Analyze with DiskTrim (console)".</summary>
public static class ShellIntegration
{
    const string Verb = "DiskTrimConsole";
    static readonly string[] Keys =
    [
        $@"Software\Classes\Directory\shell\{Verb}",
        $@"Software\Classes\Drive\shell\{Verb}",
    ];

    public static bool IsInstalled()
    {
        using var k = Registry.CurrentUser.OpenSubKey(Keys[0]);
        return k is not null;
    }

    public static void Install()
    {
        foreach (var path in Keys)
        {
            using var key = Registry.CurrentUser.CreateSubKey(path);
            key.SetValue("", "Analyze with DiskTrim (console)");
            key.SetValue("Icon", AppPaths.ExePath);
            using var cmd = key.CreateSubKey("command");
            cmd.SetValue("", $"\"{AppPaths.ExePath}\" --path \"%1\"");
        }
    }

    public static void Uninstall()
    {
        foreach (var path in Keys) Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
    }
}

public sealed record RunningProcess(int Id, string Name, string Reason);

/// <summary>"Is it in use?" — programs started from a location, and programs with files open there.</summary>
public static class ProcessInspector
{
    public static List<RunningProcess> WhoUses(string path, int maxFiles = 2000) => WhoUses([path], maxFiles);

    /// <summary>Checks several files/folders at once (the process list is read only once).</summary>
    public static List<RunningProcess> WhoUses(IReadOnlyCollection<string> paths, int maxFiles = 2000)
    {
        var targets = paths.Select(Scanner.NormalizeRoot).Select(p => (Path: p, IsDir: Directory.Exists(p))).ToList();
        var result = new Dictionary<int, RunningProcess>();

        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                string? image = null;
                try { image = p.MainModule?.FileName; }
                catch { /* elevated or protected process */ }
                if (image is null) continue;
                bool hit = targets.Any(t => t.IsDir
                    ? image.StartsWith(t.Path.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)
                    : image.Equals(t.Path, StringComparison.OrdinalIgnoreCase));
                if (hit) result[p.Id] = new RunningProcess(p.Id, p.ProcessName, $"runs from {image}");
            }
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        string[] files = targets
            .SelectMany(t => t.IsDir ? Directory.EnumerateFiles(t.Path, "*", options) : [t.Path])
            .Take(maxFiles)
            .ToArray();

        foreach (var chunk in files.Chunk(500))
            foreach (var (pid, app) in Native.ProcessesLocking(chunk))
                result.TryAdd(pid, new RunningProcess(pid, app, "has files open"));

        return result.Values.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
