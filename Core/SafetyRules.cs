namespace DiskTrim.Core;

public enum SafetyLevel
{
    /// <summary>Cache / temporary data, rebuilt automatically.</summary>
    Safe,
    /// <summary>Safe for your data but with a side effect.</summary>
    Caution,
    /// <summary>Personal or unknown data — only the user can decide.</summary>
    Review,
    /// <summary>Needed by Windows or installed programs.</summary>
    Keep,
}

public sealed record SafetyVerdict(SafetyLevel Level, string Title, string Reason, string Impact)
{
    public static string LevelText(SafetyLevel level) => level switch
    {
        SafetyLevel.Safe => "Safe to delete",
        SafetyLevel.Caution => "Safe, with side effect",
        SafetyLevel.Review => "Your decision",
        _ => "Do not delete",
    };
}

/// <summary>
/// Tells what a folder or file is and whether deleting it is safe.
/// Patterns use {sys} {win} {users} {pd} {pf} {pf86} tokens, * (one name) and ** (any depth).
/// A pattern matches the path itself and everything below it; the most specific match wins,
/// except that a generic "anywhere" rule (node_modules, *.tmp …) never overrides a "Do not delete" location.
/// </summary>
public static class SafetyRules
{
    sealed record Rule(Regex Regex, bool IsGeneric, bool NeedsProjectFile, SafetyVerdict Verdict);

    const SafetyLevel S = SafetyLevel.Safe, C = SafetyLevel.Caution, R = SafetyLevel.Review, K = SafetyLevel.Keep;

    public static readonly SafetyVerdict Unrecognised = new(R, "Unrecognised",
        "Not a Windows or application location DiskTrim knows — usually your own files, or data of a program installed outside Program Files.",
        "If these are your own files, only you can decide (consider moving them to another drive). If a program uses them, check that program first.");

    static readonly SafetyVerdict Drive = new(R, "Drive", "The whole drive.", "Look inside — DiskTrim marks each folder.");

    static readonly (SafetyLevel Level, string Title, string Reason, string Impact, string[] Patterns)[] Data =
    [
        (K, "Outlook data file (.pst)", "Mail stored only on this PC.", "Deleting loses that mail permanently.",
            [@"{sys}\**\*.pst"]),
        (S, "Temporary files", "Scratch files apps create while running; nothing needs them after the app closes.",
            "None. DiskTrim skips files that are in use and files changed in the last 24 hours.",
            [@"{users}\*\AppData\Local\Temp", @"{win}\Temp", @"{win}\SystemTemp"]),
        (C, "Recycle Bin", "Files you already deleted.", "They can no longer be restored.",
            [@"{sys}\$Recycle.Bin"]),
        (S, "Internet cache", "Cached web content used by Windows components and older apps.", "Re-downloaded when needed.",
            [@"{users}\*\AppData\Local\Microsoft\Windows\INetCache", @"{users}\*\AppData\Local\Packages\*\AC\INetCache"]),
        (S, "Thumbnail cache", "Preview images Explorer generates for pictures and videos.",
            "Explorer rebuilds thumbnails the next time you open a folder.",
            [@"{users}\*\AppData\Local\Microsoft\Windows\Explorer\thumbcache_*"]),
        (S, "Crash dumps & error reports", "Memory dumps and reports written when an app or Windows crashed.",
            "None, unless a support engineer has asked you for a specific dump.",
            [@"{users}\*\AppData\Local\CrashDumps", @"{users}\*\AppData\Local\Microsoft\Windows\WER", @"{pd}\Microsoft\Windows\WER",
             @"{win}\Minidump", @"{win}\MEMORY.DMP", @"{win}\LiveKernelReports"]),
        (S, "Windows Update downloads", "Update packages that were already downloaded and installed.",
            "None. Windows downloads again anything it still needs.",
            [@"{win}\SoftwareDistribution\Download"]),
        (S, "Delivery Optimization cache", "Update files Windows keeps to share with other PCs.",
            "None. Use the cleanup command (it uses Windows' own command).",
            [@"{win}\ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization"]),
        (S, "Browser cache", "Cached images, scripts and compiled code from websites.",
            "Pages load a bit slower once while the cache refills. Close the browser first. Passwords, cookies, history and bookmarks are NOT stored here.",
            [@"{users}\*\AppData\Local\**\User Data\*\Cache", @"{users}\*\AppData\Local\**\User Data\*\Code Cache",
             @"{users}\*\AppData\Local\**\User Data\*\GPUCache", @"{users}\*\AppData\Local\**\User Data\*\Service Worker\CacheStorage",
             @"{users}\*\AppData\Local\**\User Data\*\Service Worker\ScriptCache", @"{users}\*\AppData\Local\**\User Data\ShaderCache",
             @"{users}\*\AppData\Local\**\User Data\GrShaderCache", @"{users}\*\AppData\Local\Mozilla\Firefox\Profiles\*\cache2"]),
        (C, "Graphics shader cache", "Pre-compiled graphics shaders.", "Games/apps may stutter briefly the first time while it rebuilds.",
            [@"{users}\*\AppData\Local\D3DSCache", @"{users}\*\AppData\Local\NVIDIA\DXCache", @"{users}\*\AppData\Local\NVIDIA\GLCache",
             @"{users}\*\AppData\Local\AMD\DxCache", @"{users}\*\AppData\Local\AMD\DxcCache"]),
        (S, "Developer download cache", "Package-manager download caches (npm, pip, yarn, NuGet HTTP cache).",
            "Packages are downloaded again on the next install.",
            [@"{users}\*\AppData\Local\npm-cache", @"{users}\*\AppData\Roaming\npm-cache", @"{users}\*\AppData\Local\pip\Cache",
             @"{users}\*\AppData\Local\Yarn\Cache", @"{users}\*\AppData\Local\NuGet\v3-cache", @"{users}\*\AppData\Local\NuGet\plugins-cache"]),
        (S, "VS Code cache", "Visual Studio Code's internal caches.", "None. Close VS Code first.",
            [@"{users}\*\AppData\Roaming\Code\Cache", @"{users}\*\AppData\Roaming\Code\CachedData", @"{users}\*\AppData\Roaming\Code\Code Cache",
             @"{users}\*\AppData\Roaming\Code\CachedExtensionVSIXs", @"{users}\*\AppData\Roaming\Code\GPUCache", @"{users}\*\AppData\Roaming\Code\logs"]),
        (C, "Developer package store", "Downloaded libraries used by builds (NuGet, Gradle, Maven).",
            "The next build re-downloads them — needs internet and takes time. Offline builds will fail until then.",
            [@"{users}\*\.nuget\packages", @"{users}\*\.gradle\caches", @"{users}\*\.m2\repository"]),
        (C, "Teams cache", "Microsoft Teams cached data.", "Close Teams first. Teams rebuilds it; you may need to sign in again.",
            [@"{users}\*\AppData\Local\Packages\MSTeams_*\LocalCache", @"{users}\*\AppData\Roaming\Microsoft\Teams\Cache",
             @"{users}\*\AppData\Roaming\Microsoft\Teams\Code Cache", @"{users}\*\AppData\Roaming\Microsoft\Teams\GPUCache",
             @"{users}\*\AppData\Roaming\Microsoft\Teams\blob_storage",
             @"{users}\*\AppData\Roaming\Microsoft\Teams\Service Worker\CacheStorage"]),
        (C, "Outlook offline mailbox (.ost)", "A local copy of a mailbox that lives on the mail server.",
            "Best fix: in Outlook > Account Settings > your account > Change, set 'Download email for the past' to 3–12 months, close Outlook, then delete the .ost — Outlook rebuilds a much smaller one (re-download can take hours). Never delete .pst files.",
            [@"{users}\*\AppData\Local\Microsoft\Outlook\*.ost"]),
        (C, "iPhone/iPad update downloads", "iOS/iPadOS software updates downloaded by Apple Devices / iTunes.",
            "Downloaded again the next time you update a device from this PC.",
            [@"{pd}\Apple\AMPDevicesAgent\iPhone-Temporary-Files", @"{users}\*\AppData\Roaming\Apple Computer\iTunes\iPhone Software Updates"]),
        (C, "OneNote automatic backups", "Periodic backup copies OneNote makes of your notebooks.",
            "Your notebooks themselves (in OneDrive/SharePoint) are not affected; you lose only the older backup copies.",
            [@"{users}\*\AppData\Local\Microsoft\OneNote\*\Backup"]),
        (K, "Visual Studio installer cache", "Setup packages kept after Visual Studio was installed.",
            "Remove it the supported way: Visual Studio Installer > More > Settings > untick 'Keep download cache after installation'. Repairs then re-download.",
            [@"{pd}\Microsoft\VisualStudio\Packages"]),
        (C, "Previous Windows installation", "Copy of your old Windows kept after a feature update.",
            "You lose the option to roll back to the previous Windows version. Remove it via Settings > System > Storage > Temporary files.",
            [@"{sys}\Windows.old"]),
        (C, "Windows upgrade leftovers", "Setup files left behind by Windows upgrades.",
            "Remove via Settings > System > Storage > Temporary files.",
            [@"{sys}\$WINDOWS.~BT", @"{sys}\$WINDOWS.~WS", @"{sys}\$GetCurrent", @"{sys}\ESD"]),
        (K, "Hibernation file", "Holds memory contents for Hibernate and Fast Startup.",
            "Don't delete it directly. Turn hibernation off with the cleanup command (powercfg /h off) — you lose Hibernate and Fast Startup.",
            [@"{sys}\hiberfil.sys"]),
        (C, "Windows prefetch", "Start-up hints for frequently used apps.", "Apps start a little slower the first time afterwards.",
            [@"{win}\Prefetch"]),
        (S, "Windows servicing logs", "Logs and archived logs from Windows updates.", "Only lost troubleshooting history.",
            [@"{win}\Logs\CBS", @"{win}\Logs\DISM", @"{win}\Logs\WindowsUpdate"]),
        (K, "Docker data", "Docker images, containers and volumes.",
            "Don't delete files here. Use 'docker system prune' or Docker Desktop > Troubleshoot > Clean / Purge data.",
            [@"{users}\*\AppData\Local\Docker", @"{pd}\DockerDesktop"]),
        (K, "Android emulator", "Android virtual devices (each holds a full phone image).",
            "Delete unused devices from Android Studio's Device Manager rather than deleting files.",
            [@"{users}\*\.android\avd"]),
        (K, "Android SDK", "Android build tools, platforms and emulator system images.",
            "Remove unused platforms/system images in Android Studio > SDK Manager.",
            [@"{users}\*\AppData\Local\Android\Sdk"]),
        (R, "OneDrive", "Files synced with OneDrive.",
            "Deleting here deletes them in the cloud too! Right-click > 'Free up space' keeps them online-only and frees disk space.",
            [@"{users}\*\OneDrive*"]),
        (R, "Downloads", "Things you downloaded — usually installers and archives.",
            "Installers you already ran and archives you already extracted are safe to delete.",
            [@"{users}\*\Downloads"]),
        (R, "Personal files", "Your documents, pictures, videos and desktop.",
            "Only you can decide. Consider moving large media to another drive or to OneDrive.",
            [@"{users}\*\Documents", @"{users}\*\Desktop", @"{users}\*\Pictures", @"{users}\*\Videos", @"{users}\*\Music"]),
        (K, "WSL Linux disk", "The virtual disk of a WSL Linux distribution — your Linux files live inside it.",
            "Deleting it destroys that distribution. Compact it or remove the distro with 'wsl --unregister'.",
            [@"{users}\*\AppData\Local\Packages\*\LocalState\ext4.vhdx", @"{users}\*\AppData\Local\wsl"]),
        (K, "Windows Installer cache", "Needed to update, repair and uninstall programs.",
            "Deleting breaks uninstalls and future patches of installed programs.",
            [@"{win}\Installer"]),
        (K, "Windows component store", "Windows system components (WinSxS).",
            "Never delete. Use 'Clean up Windows component store' (cleanup command). Much of its size is shared with System32 (hard links).",
            [@"{win}\WinSxS"]),
        (K, "Windows", "The Windows operating system.", "Deleting files here can stop Windows from starting.",
            [@"{win}"]),
        (K, "Installed programs", "Program files of installed applications.",
            "Uninstall apps from Settings > Apps (or the apps command) instead of deleting folders.",
            [@"{pf}", @"{pf86}", @"{sys}\Program Files (Arm)"]),
        (K, "Installer package cache", "Setup packages for Visual Studio, .NET, SQL Server etc.",
            "Needed to repair, update and uninstall those products.",
            [@"{pd}\Package Cache"]),
        (K, "Shared application data", "Settings and data that installed programs share between users.",
            "Deleting can break or reset installed programs.",
            [@"{pd}"]),
        (K, "System Restore points", "Restore points and shadow copies.",
            "Shrink them in System Properties > System Protection > Configure (or 'vssadmin resize shadowstorage').",
            [@"{sys}\System Volume Information"]),
        (K, "Windows boot & recovery", "Files Windows needs to start and to repair itself.", "Do not delete.",
            [@"{sys}\Recovery", @"{sys}\Boot", @"{sys}\$WinREAgent", @"{sys}\bootmgr", @"{sys}\BOOTNXT"]),
        (K, "Virtual memory", "The page/swap file Windows uses as extra memory.",
            "Never delete. Size can be changed in System Properties > Advanced > Performance.",
            [@"{sys}\pagefile.sys", @"{sys}\swapfile.sys"]),
        (K, "Microsoft Store apps", "Data of installed Store apps (Teams, Photos, WSL …).",
            "Deleting resets or breaks those apps. Safe sub-folders (caches) are marked separately.",
            [@"{users}\*\AppData\Local\Packages"]),
        (K, "Roaming app settings", "Settings and profiles of your apps.",
            "Deleting resets or breaks apps. Safe sub-folders (caches) are marked separately.",
            [@"{users}\*\AppData\Roaming"]),
        (K, "User registry", "The user's part of the Windows registry (settings of Windows and all apps).",
            "Never delete. Windows can't sign this user in without it.",
            [@"{users}\*\NTUSER.DAT*", @"{users}\*\AppData\Local\Microsoft\Windows\UsrClass.dat*"]),
        (R, "App data", "Local data of your apps: a mix of caches (cleanable) and important data.",
            "Open it: sub-folders marked Safe/Caution are cleanable; leave the rest.",
            [@"{users}\*\AppData"]),
        (R, "User profile", "A user's personal folder.", "Look inside — DiskTrim marks each sub-folder.",
            [@"{users}\*"]),
        (R, "User profiles", "All user profiles on this PC.", "Look inside — DiskTrim marks each sub-folder.",
            [@"{users}"]),

        // Generic rules: match anywhere on the disk.
        (C, "Node.js dependencies", "Packages restored by 'npm install'.",
            "The project won't run until you run 'npm install' again. Fine for projects you're not working on.",
            [@"**\node_modules"]),
        (S, "Build output", "Compiled output of a .NET/Visual Studio project.",
            "Rebuilt automatically the next time you build.",
            [@"**\obj", @"**\bin"]),
        (S, "Visual Studio cache", "Visual Studio's per-solution cache (.vs).", "VS rebuilds it; you may lose open-tab layout.",
            [@"**\.vs"]),
        (S, "Python cache", "Compiled Python bytecode.", "Rebuilt automatically.",
            [@"**\__pycache__"]),
        (C, "Temporary file", "A file marked as temporary.", "Usually leftovers. Skip it if the app that made it is still open.",
            [@"**\*.tmp", @"**\~$*"]),
        (S, "Memory dump", "A crash/memory dump.", "Only useful for crash troubleshooting.",
            [@"**\*.dmp"]),
        (R, "Backup / old copy", "A backup or old copy of another file.", "Check the original still exists before deleting.",
            [@"**\*.bak", @"**\*.old"]),
        (R, "Disk image / installer", "ISO/MSI/EXE or archive.", "Safe to delete if you've already installed or extracted it.",
            [@"**\*.iso", @"**\*.msi", @"**\*.zip", @"**\*.7z", @"**\*.rar"]),
        (R, "Photo / video", "Your own photos or videos.",
            "Only you can decide. To free space without losing them: move to an external drive, or to OneDrive and use 'Free up space'.",
            [@"**\*.jpg", @"**\*.jpeg", @"**\*.png", @"**\*.heic", @"**\*.cr2", @"**\*.cr3", @"**\*.nef", @"**\*.arw", @"**\*.dng",
             @"**\*.raw", @"**\*.mp4", @"**\*.mov", @"**\*.mkv", @"**\*.avi", @"**\*.mts", @"**\*.m2ts"]),
        (R, "Document", "A document, spreadsheet or presentation.", "Only you can decide.",
            [@"**\*.docx", @"**\*.doc", @"**\*.xlsx", @"**\*.xls", @"**\*.pptx", @"**\*.pdf"]),
        (R, "Virtual machine disk", "A virtual hard disk.",
            "Contains a whole virtual machine. Delete only VMs you no longer need, from their manager.",
            [@"**\*.vhd", @"**\*.vhdx", @"**\*.vmdk"]),
    ];

    static readonly Lazy<List<Rule>> Rules = new(Build);
    static readonly ConcurrentDictionary<string, bool> ProjectDirs = new(StringComparer.OrdinalIgnoreCase);

    public static SafetyVerdict Classify(string path)
    {
        path = path.Length > 3 ? path.TrimEnd('\\') : path;
        if (path.Length <= 3 && path.Length >= 2 && path[1] == ':') return Drive;

        Rule? bestLoc = null, bestGen = null;
        int locLen = -1, genLen = -1;
        foreach (var rule in Rules.Value)
        {
            var m = rule.Regex.Match(path);
            if (!m.Success) continue;
            var own = m.Groups[1];
            if (rule.NeedsProjectFile && !HasProjectFile(Path.GetDirectoryName(own.Value))) continue;

            if (rule.IsGeneric) { if (own.Length > genLen) { genLen = own.Length; bestGen = rule; } }
            else if (own.Length > locLen) { locLen = own.Length; bestLoc = rule; }
        }

        if (bestGen is null) return bestLoc?.Verdict ?? Unrecognised;
        if (bestLoc is null) return bestGen.Verdict;
        // A generic rule only refines personal/unknown locations; a recognised location (Temp, Windows …) keeps its verdict.
        if (bestLoc.Verdict.Level != SafetyLevel.Review) return bestLoc.Verdict;
        return genLen > locLen ? bestGen.Verdict : bestLoc.Verdict;
    }

    // "bin" and "obj" are only build output when they sit next to a project or solution file.
    static bool HasProjectFile(string? dir)
    {
        if (string.IsNullOrEmpty(dir)) return false;
        return ProjectDirs.GetOrAdd(dir, d =>
        {
            try
            {
                return Directory.EnumerateFiles(d, "*.*proj").Any()
                    || Directory.EnumerateFiles(d, "*.sln").Any()
                    || Directory.EnumerateFiles(d, "*.slnx").Any();
            }
            catch { return false; }
        });
    }

    static List<Rule> Build()
    {
        string users = Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) ?? @"C:\Users";
        var tokens = new Dictionary<string, string>
        {
            ["sys"] = AppPaths.SystemRoot.TrimEnd('\\'),
            ["win"] = Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            ["users"] = users,
            ["pd"] = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            ["pf"] = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            ["pf86"] = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        };

        var rules = new List<Rule>();
        foreach (var (level, title, reason, impact, patterns) in Data)
        {
            var verdict = new SafetyVerdict(level, title, reason, impact);
            foreach (var p in patterns)
            {
                bool generic = p.StartsWith("**", StringComparison.Ordinal);
                bool needsProject = title == "Build output";
                var regex = Compile(p, tokens);
                if (regex is not null) rules.Add(new Rule(regex, generic, needsProject, verdict));
            }
        }
        return rules;
    }

    /// <summary>Glob → regex. Group 1 is the part the pattern itself covers; anything below it also matches.</summary>
    static Regex? Compile(string pattern, Dictionary<string, string> tokens)
    {
        var sb = new StringBuilder("^(");
        int i = 0;
        while (i < pattern.Length)
        {
            if (pattern[i] == '{')
            {
                int end = pattern.IndexOf('}', i);
                var value = tokens[pattern[(i + 1)..end]];
                if (string.IsNullOrEmpty(value)) return null; // e.g. no Program Files (x86) on this PC
                sb.Append(Regex.Escape(value));
                i = end + 1;
            }
            else if (pattern.AsSpan(i).StartsWith(@"**\")) { sb.Append(@"(?:.*\\)?"); i += 3; }
            else if (pattern.AsSpan(i).StartsWith("**")) { sb.Append(".*"); i += 2; }
            else
            {
                char c = pattern[i++];
                sb.Append(c switch { '*' => @"[^\\]*", '?' => @"[^\\]", _ => Regex.Escape(c.ToString()) });
            }
        }
        sb.Append(@")(?:\\.*)?$");
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
