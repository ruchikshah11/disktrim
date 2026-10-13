using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace DiskTrim.Tests;

static class TestSetup
{
    /// <summary>Keeps logs, history and settings of the test run out of the user's real data folder.</summary>
    [ModuleInitializer]
    internal static void Init()
    {
        var dir = Path.Combine(Path.GetTempPath(), "DiskTrimTests-data-" + Environment.ProcessId);
        Environment.SetEnvironmentVariable("DISKTRIM_DATA_DIR", dir);
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
    }
}

/// <summary>A throw-away folder under %TEMP%, deleted afterwards.</summary>
sealed class TempDir : IDisposable
{
    /// <param name="baseDir">Defaults to %TEMP%; pass another folder when the test must not sit inside a "Temporary files" location.</param>
    public TempDir(string? baseDir = null)
    {
        Root = Path.Combine(baseDir ?? Path.Combine(Path.GetTempPath(), "DiskTrimTests"), Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string P(string relative) => Path.Combine(Root, relative);

    public string File(string relative, int size, DateTime? modified = null, byte fill = 0x41)
    {
        var path = P(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var data = new byte[size];
        Array.Fill(data, fill);
        System.IO.File.WriteAllBytes(path, data);
        if (modified is { } m) System.IO.File.SetLastWriteTime(path, m);
        return path;
    }

    public string Dir(string relative) => Directory.CreateDirectory(P(relative)).FullName;

    /// <summary>Directory junction (no admin rights needed).</summary>
    public string Junction(string relative, string target)
    {
        var link = P(relative);
        Run("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"");
        Assert.True(Directory.Exists(link), "junction was not created");
        return link;
    }

    public string HardLink(string relative, string target)
    {
        var link = P(relative);
        Run("cmd.exe", $"/c mklink /H \"{link}\" \"{target}\"");
        Assert.True(System.IO.File.Exists(link), "hard link was not created");
        return link;
    }

    static void Run(string exe, string args)
    {
        using var p = Process.Start(new ProcessStartInfo(exe, args) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
        p.WaitForExit();
    }

    public void Dispose()
    {
        try
        {
            // Remove junctions first so deleting never reaches through them.
            foreach (var d in Directory.EnumerateDirectories(Root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }).ToList())
                if ((new DirectoryInfo(d).Attributes & FileAttributes.ReparsePoint) != 0) Directory.Delete(d);
            foreach (var f in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                System.IO.File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(Root, recursive: true);
        }
        catch { /* best effort */ }
    }
}
