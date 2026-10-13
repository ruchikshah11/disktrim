namespace DiskTrim.Core;

public enum CleanupKind
{
    /// <summary>Delete matching files (in-use files are skipped).</summary>
    Files,
    /// <summary>Empty the Recycle Bin on all drives.</summary>
    RecycleBin,
    /// <summary>Run a Windows tool (DISM, powercfg …).</summary>
    Command,
    /// <summary>Only Windows itself can do it — open the right Settings page.</summary>
    OpenSettings,
    /// <summary>Move matching files to the Recycle Bin instead of deleting them.</summary>
    RecycleFiles,
}

public sealed record CleanupTarget(string Root, string Pattern = "*", bool Recurse = true, int MinAgeDays = 0, bool IsFile = false)
{
    public bool Exists => IsFile ? File.Exists(Root) : Directory.Exists(Root);
}

public sealed class CleanupItem
{
    public required string Id { get; init; }
    public required string Group { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public required string Impact { get; init; }
    public SafetyLevel Level { get; init; }
    public CleanupKind Kind { get; init; } = CleanupKind.Files;
    public bool NeedsAdmin { get; init; }
    /// <summary>Process names (without .exe) that must be closed first.</summary>
    public string[] BlockingProcesses { get; init; } = [];
    public string? AppName { get; init; }
    public List<CleanupTarget> Targets { get; init; } = [];
    public string? Command { get; init; }
    public string? Arguments { get; init; }
    /// <summary>Shown instead of a size when the saving can't be measured up front.</summary>
    public string? SizeHint { get; init; }

    // Filled by CleanupEngine.Measure
    public long Bytes { get; set; }
    public long FileCount { get; set; }
    public bool Measured { get; set; }
    public string? BlockedReason { get; set; }

    public bool IsBlocked => BlockedReason is not null;

    public string Status => BlockedReason
        ?? (Kind == CleanupKind.OpenSettings ? "Opens Windows Settings"
        : Kind == CleanupKind.Command && SizeHint is not null ? "Ready"
        : Bytes == 0 ? (Kind == CleanupKind.RecycleBin ? "Already empty" : "Nothing to clean")
        : "Ready");
}

/// <summary>Everything DiskTrim knows how to clean, with what it is and what happens if you clean it.</summary>
public static class CleanupCatalog
{
    public static List<CleanupItem> Build()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string pd = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        string sys = AppPaths.SystemRoot;
        string temp = Path.GetTempPath().TrimEnd('\\');
        string downloads = Path.Combine(profile, "Downloads");

        var items = new List<CleanupItem>
        {
            // ---- Windows ----------------------------------------------------------------------
            new()
            {
                Id = "user-temp", Group = "Windows", Level = SafetyLevel.Safe,
                Title = "Your temporary files",
                Description = $"Scratch files in {temp}. Apps create them while running and rarely remove them.",
                Impact = "None. Files in use and files changed in the last 24 hours are kept, so running apps and installers aren't affected.",
                Targets = [new(temp, MinAgeDays: 1)],
            },
            new()
            {
                Id = "win-temp", Group = "Windows", Level = SafetyLevel.Safe, NeedsAdmin = true,
                Title = "Windows temporary files",
                Description = $"{win}\\Temp used by Windows services and installers.",
                Impact = "None. Files in use and files changed in the last 24 hours are kept.",
                Targets = [new(Path.Combine(win, "Temp"), MinAgeDays: 1), new(Path.Combine(win, "SystemTemp"), MinAgeDays: 1)],
            },
            new()
            {
                Id = "recycle", Group = "Windows", Level = SafetyLevel.Caution, Kind = CleanupKind.RecycleBin,
                Title = "Recycle Bin",
                Description = "Files you've already deleted (all drives).",
                Impact = "They can no longer be restored — glance at the Recycle Bin first if unsure.",
            },
            new()
            {
                Id = "wu-cache", Group = "Windows", Level = SafetyLevel.Safe, NeedsAdmin = true,
                Title = "Windows Update download cache",
                Description = $"Update packages in {win}\\SoftwareDistribution\\Download that are already installed.",
                Impact = "None. Windows downloads again anything it still needs. Files Windows Update is using right now are skipped.",
                Targets = [new(Path.Combine(win, @"SoftwareDistribution\Download"))],
            },
            new()
            {
                Id = "do-cache", Group = "Windows", Level = SafetyLevel.Safe, NeedsAdmin = true, Kind = CleanupKind.Command,
                Title = "Delivery Optimization cache",
                Description = "Update files Windows keeps to share with other PCs. Cleared with Windows' own command.",
                Impact = "None.",
                Command = "powershell.exe",
                Arguments = "-NoProfile -NonInteractive -Command \"Delete-DeliveryOptimizationCache -Force\"",
                Targets = [new(Path.Combine(win, @"ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache"))],
            },
            new()
            {
                Id = "dumps-user", Group = "Windows", Level = SafetyLevel.Safe,
                Title = "Crash dumps & error reports (yours)",
                Description = "Memory dumps and Windows Error Reporting files from apps that crashed.",
                Impact = "None, unless a support engineer has asked you for a dump.",
                Targets = [new(Path.Combine(local, "CrashDumps")), new(Path.Combine(local, @"Microsoft\Windows\WER"))],
            },
            new()
            {
                Id = "dumps-system", Group = "Windows", Level = SafetyLevel.Safe, NeedsAdmin = true,
                Title = "Crash dumps & error reports (system)",
                Description = "System crash (blue screen) dumps and system error reports.",
                Impact = "None, unless a support engineer has asked you for a dump.",
                Targets =
                [
                    new(Path.Combine(pd, @"Microsoft\Windows\WER")), new(Path.Combine(win, "Minidump")),
                    new(Path.Combine(win, "LiveKernelReports")), new(Path.Combine(win, "MEMORY.DMP"), IsFile: true),
                ],
            },
            new()
            {
                Id = "thumbs", Group = "Windows", Level = SafetyLevel.Safe,
                Title = "Thumbnail cache",
                Description = "Preview images Explorer creates for photos and videos.",
                Impact = "Explorer rebuilds thumbnails when you open folders. Files Explorer has open are skipped.",
                Targets = [new(Path.Combine(local, @"Microsoft\Windows\Explorer"), "thumbcache_*.db", Recurse: false)],
            },
            new()
            {
                Id = "shader", Group = "Windows", Level = SafetyLevel.Caution,
                Title = "Graphics shader cache",
                Description = "Pre-compiled graphics shaders (DirectX, NVIDIA, AMD).",
                Impact = "Games and 3D apps may stutter briefly the first time while it rebuilds.",
                Targets =
                [
                    new(Path.Combine(local, "D3DSCache")), new(Path.Combine(local, @"NVIDIA\DXCache")),
                    new(Path.Combine(local, @"NVIDIA\GLCache")), new(Path.Combine(local, @"AMD\DxCache")),
                ],
            },
            new()
            {
                Id = "cbs-logs", Group = "Windows", Level = SafetyLevel.Safe, NeedsAdmin = true,
                Title = "Old Windows servicing logs",
                Description = $"Old update/servicing logs in {win}\\Logs (older than 7 days).",
                Impact = "Only troubleshooting history is lost.",
                Targets = [new(Path.Combine(win, @"Logs\CBS"), MinAgeDays: 7), new(Path.Combine(win, @"Logs\DISM"), MinAgeDays: 7)],
            },
            new()
            {
                Id = "dism", Group = "Windows", Level = SafetyLevel.Caution, NeedsAdmin = true, Kind = CleanupKind.Command,
                Title = "Clean up Windows component store",
                Description = "Removes superseded Windows update components from WinSxS using Microsoft's DISM tool — the only safe way to shrink WinSxS.",
                Impact = "None. Installed updates stay uninstallable. Takes 5–20 minutes.",
                Command = "dism.exe", Arguments = "/Online /Cleanup-Image /StartComponentCleanup",
                SizeHint = "typically 0.5–5 GB",
            },
            new()
            {
                Id = "hiberfil", Group = "Windows", Level = SafetyLevel.Caution, NeedsAdmin = true, Kind = CleanupKind.Command,
                Title = "Turn off hibernation (removes hiberfil.sys)",
                Description = "hiberfil.sys stores memory for Hibernate and Fast Startup; it is usually 40 % of your RAM.",
                Impact = "Hibernate and Fast Startup stop working (Sleep still works). Undo any time with 'powercfg /hibernate on'.",
                Command = "powercfg.exe", Arguments = "/hibernate off",
                Targets = [new(Path.Combine(sys, "hiberfil.sys"), IsFile: true)],
            },
            new()
            {
                Id = "windows-old", Group = "Windows", Level = SafetyLevel.Caution, Kind = CleanupKind.OpenSettings,
                Title = "Previous Windows installation & upgrade leftovers",
                Description = "Windows.old and $WINDOWS.~BT folders left after a Windows feature update.",
                Impact = "You can no longer roll back to the previous Windows version. Windows removes these itself: Settings opens on Storage > Temporary files.",
                Command = "ms-settings:storagesense",
                Targets =
                [
                    new(Path.Combine(sys, "Windows.old")), new(Path.Combine(sys, "$WINDOWS.~BT")), new(Path.Combine(sys, "$WINDOWS.~WS")),
                ],
            },

            // ---- Browsers & apps --------------------------------------------------------------
            Browser("edge", "Microsoft Edge", "msedge", Path.Combine(local, @"Microsoft\Edge\User Data")),
            Browser("chrome", "Google Chrome", "chrome", Path.Combine(local, @"Google\Chrome\User Data")),
            Browser("brave", "Brave", "brave", Path.Combine(local, @"BraveSoftware\Brave-Browser\User Data")),
            new()
            {
                Id = "firefox", Group = "Browsers & apps", Level = SafetyLevel.Safe,
                BlockingProcesses = ["firefox"], AppName = "Firefox",
                Title = "Firefox cache",
                Description = "Cached images and scripts from websites.",
                Impact = "Pages load a little slower once. Passwords, cookies, history and bookmarks are NOT touched.",
                Targets = [.. SubDirs(Path.Combine(local, @"Mozilla\Firefox\Profiles")).Select(p => new CleanupTarget(Path.Combine(p, "cache2")))],
            },
            new()
            {
                Id = "teams", Group = "Browsers & apps", Level = SafetyLevel.Caution,
                BlockingProcesses = ["ms-teams", "Teams"], AppName = "Microsoft Teams",
                Title = "Microsoft Teams cache",
                Description = "Cached data of new and classic Teams (Microsoft's documented way to reset Teams).",
                Impact = "Teams rebuilds it on next start; you may have to sign in again. Chats and files are in the cloud and are not affected.",
                Targets =
                [
                    new(Path.Combine(local, @"Packages\MSTeams_8wekyb3d8bbwe\LocalCache\Microsoft\MSTeams")),
                    .. new[] { "Cache", "Code Cache", "GPUCache", "blob_storage", @"Service Worker\CacheStorage" }
                        .Select(s => new CleanupTarget(Path.Combine(roaming, @"Microsoft\Teams", s))),
                ],
            },
            new()
            {
                Id = "vscode", Group = "Browsers & apps", Level = SafetyLevel.Safe,
                BlockingProcesses = ["Code"], AppName = "Visual Studio Code",
                Title = "VS Code cache",
                Description = "Visual Studio Code's internal caches and logs.",
                Impact = "None. Your settings, extensions and workspaces are not touched.",
                Targets = [.. new[] { "Cache", "CachedData", "Code Cache", "CachedExtensionVSIXs", "GPUCache", "logs" }
                    .Select(s => new CleanupTarget(Path.Combine(roaming, "Code", s)))],
            },
            new()
            {
                Id = "apple-updates", Group = "Browsers & apps", Level = SafetyLevel.Caution,
                BlockingProcesses = ["AppleDevices", "iTunes"], AppName = "Apple Devices / iTunes",
                Title = "iPhone/iPad update downloads",
                Description = "iOS/iPadOS update files downloaded by Apple Devices or iTunes (several GB each).",
                Impact = "Downloaded again the next time you update an iPhone/iPad from this PC. Your device backups are elsewhere and not touched.",
                Targets =
                [
                    new(Path.Combine(pd, @"Apple\AMPDevicesAgent\iPhone-Temporary-Files")),
                    new(Path.Combine(roaming, @"Apple Computer\iTunes\iPhone Software Updates")),
                ],
            },
            new()
            {
                Id = "onenote-backup", Group = "Browsers & apps", Level = SafetyLevel.Caution,
                BlockingProcesses = ["ONENOTE"], AppName = "OneNote",
                Title = "OneNote backups older than 30 days",
                Description = "Old automatic backup copies of your OneNote notebooks.",
                Impact = "Your notebooks (in OneDrive/SharePoint) and recent backups are kept; only backups older than 30 days are removed.",
                Targets = [.. SubDirs(Path.Combine(local, @"Microsoft\OneNote")).Select(v => new CleanupTarget(Path.Combine(v, "Backup"), MinAgeDays: 30))],
            },

            // ---- Developer --------------------------------------------------------------------
            new()
            {
                Id = "dev-download-cache", Group = "Developer", Level = SafetyLevel.Safe,
                Title = "Package manager download caches",
                Description = "Download caches of npm, pip, yarn and NuGet (HTTP cache).",
                Impact = "Packages are downloaded again the next time a project restores. Installed projects keep working.",
                Targets =
                [
                    new(Path.Combine(local, "npm-cache")), new(Path.Combine(roaming, "npm-cache")), new(Path.Combine(local, @"pip\Cache")),
                    new(Path.Combine(local, @"Yarn\Cache")), new(Path.Combine(local, @"NuGet\v3-cache")), new(Path.Combine(local, @"NuGet\plugins-cache")),
                ],
            },
            new()
            {
                Id = "nuget-packages", Group = "Developer", Level = SafetyLevel.Caution,
                BlockingProcesses = ["devenv", "MSBuild"], AppName = "Visual Studio",
                Title = "NuGet global packages",
                Description = $"All NuGet packages ever restored ({profile}\\.nuget\\packages).",
                Impact = "Next build of each .NET project re-downloads its packages (needs internet). Already-built apps keep running.",
                Targets = [new(Path.Combine(profile, @".nuget\packages"))],
            },
            new()
            {
                Id = "gradle", Group = "Developer", Level = SafetyLevel.Caution,
                BlockingProcesses = ["studio64", "java"], AppName = "Android Studio / Java",
                Title = "Gradle / Maven caches",
                Description = "Downloaded build dependencies for Android/Java projects.",
                Impact = "Next build re-downloads dependencies (can be several GB, needs internet).",
                Targets = [new(Path.Combine(profile, @".gradle\caches")), new(Path.Combine(profile, @".m2\repository"))],
            },

            // ---- Personal ---------------------------------------------------------------------
            new()
            {
                Id = "downloads-old", Group = "Personal (your decision)", Level = SafetyLevel.Review, Kind = CleanupKind.RecycleFiles,
                Title = "Old installers & archives in Downloads",
                Description = "Setup files (.exe, .msi, .iso) and archives (.zip, .7z, .rar) in Downloads older than 30 days.",
                Impact = "Moved to the Recycle Bin (not deleted) so you can restore them. Empty the Recycle Bin afterwards to actually free the space.",
                Targets = [.. new[] { "*.exe", "*.msi", "*.iso", "*.zip", "*.7z", "*.rar" }
                    .Select(p => new CleanupTarget(downloads, p, Recurse: false, MinAgeDays: 30))],
            },
        };

        // Hide things this PC doesn't have (no Brave, no Teams …) — Commands and Settings items stay
        // unless they are tied to something that doesn't exist (hiberfil.sys, Windows.old).
        return items.Where(i => i.Kind switch
        {
            CleanupKind.RecycleBin => true,
            CleanupKind.Command => i.Targets.Count == 0 || i.Id == "do-cache" || i.Targets.Any(t => t.Exists),
            _ => i.Targets.Any(t => t.Exists),
        }).ToList();
    }

    static CleanupItem Browser(string id, string name, string process, string userData) => new()
    {
        Id = id, Group = "Browsers & apps", Level = SafetyLevel.Safe,
        BlockingProcesses = [process], AppName = name,
        Title = $"{name} cache",
        Description = "Cached images, scripts and compiled code from websites, for every browser profile.",
        Impact = "Pages load a little slower once. Passwords, cookies, history and bookmarks are NOT touched.",
        Targets = [.. ChromiumTargets(userData)],
    };

    static IEnumerable<CleanupTarget> ChromiumTargets(string userData)
    {
        if (!Directory.Exists(userData)) yield break;
        // A profile folder is one with a "Preferences" file: Default, Profile 1, …
        foreach (var profile in SubDirs(userData).Where(d => File.Exists(Path.Combine(d, "Preferences"))))
            foreach (var sub in new[] { "Cache", "Code Cache", "GPUCache", @"Service Worker\CacheStorage", @"Service Worker\ScriptCache" })
                yield return new CleanupTarget(Path.Combine(profile, sub));
        yield return new CleanupTarget(Path.Combine(userData, "ShaderCache"));
        yield return new CleanupTarget(Path.Combine(userData, "GrShaderCache"));
    }

    static string[] SubDirs(string path)
    {
        try { return Directory.Exists(path) ? Directory.GetDirectories(path) : []; }
        catch { return []; }
    }
}
