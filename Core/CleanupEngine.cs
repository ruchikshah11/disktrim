namespace DiskTrim.Core;

public sealed record CleanupResult(long FreedBytes, int Deleted, int Skipped, string Message, bool Failed = false);

/// <summary>Measures and cleans <see cref="CleanupItem"/>s. Never follows links, skips files in use, logs every deletion.</summary>
public static class CleanupEngine
{
    static readonly EnumerationOptions Opts = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
        AttributesToSkip = 0,
    };

    public static void Measure(CleanupItem item, CancellationToken ct = default)
    {
        item.BlockedReason = BlockReason(item);
        try
        {
            if (item.Kind == CleanupKind.RecycleBin)
            {
                (item.Bytes, item.FileCount) = Native.QueryRecycleBin();
            }
            else
            {
                long bytes = 0, count = 0;
                foreach (var f in EnumerateAll(item, ct)) { bytes += SizeOf(f); count++; }
                item.Bytes = bytes;
                item.FileCount = count;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            item.BlockedReason ??= "Could not measure: " + ex.Message;
        }
        item.Measured = true;
    }

    public static string? BlockReason(CleanupItem item)
    {
        if (item.NeedsAdmin && !AppPaths.IsAdmin) return "Needs administrator";
        var running = item.BlockingProcesses.FirstOrDefault(IsRunning);
        return running is null ? null : $"Close {item.AppName ?? running} first";
    }

    public static bool IsRunning(string processName)
    {
        var procs = Process.GetProcessesByName(processName);
        foreach (var p in procs) p.Dispose();
        return procs.Length > 0;
    }

    public static CleanupResult Clean(CleanupItem item, AuditLog log, Action<string>? progress, CancellationToken ct)
    {
        item.BlockedReason = BlockReason(item);
        if (item.BlockedReason is { } reason)
        {
            log.Write(item.Id, "skip", "", 0, reason);
            return new CleanupResult(0, 0, 0, reason, Failed: true);
        }

        return item.Kind switch
        {
            CleanupKind.RecycleBin => EmptyRecycleBin(item, log),
            CleanupKind.Command => RunCommand(item, log, progress),
            CleanupKind.OpenSettings => OpenSettings(item, log),
            CleanupKind.RecycleFiles => RecycleFiles(item, log, progress, ct),
            _ => DeleteFiles(item, log, progress, ct),
        };
    }

    // ---- Kinds ---------------------------------------------------------------------------------

    static CleanupResult DeleteFiles(CleanupItem item, AuditLog log, Action<string>? progress, CancellationToken ct)
    {
        long freed = 0;
        int deleted = 0, skipped = 0;
        foreach (var target in item.Targets)
        {
            if (ct.IsCancellationRequested) break;
            // Materialise first: deleting while enumerating the same folder is unreliable.
            foreach (var f in Enumerate(target, ct).ToList())
            {
                if (ct.IsCancellationRequested) break;
                long size = SizeOf(f);
                try
                {
                    if ((f.Attributes & FileAttributes.ReadOnly) != 0) f.Attributes &= ~FileAttributes.ReadOnly;
                    f.Delete();
                    freed += size;
                    deleted++;
                    log.Write(item.Id, "delete", f.FullName, size, "Deleted");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    skipped++;
                    log.Write(item.Id, "skip", f.FullName, size, "In use — skipped");
                }
                if ((deleted + skipped) % 250 == 0) progress?.Invoke($"Deleting {Format.Count(deleted)} files · {Format.Bytes(freed)}");
            }
            if (target.Recurse && !target.IsFile && Directory.Exists(target.Root))
                RemoveEmptyDirs(new DirectoryInfo(target.Root), isRoot: true, DateTime.Now.AddDays(-target.MinAgeDays));
        }

        string msg = ct.IsCancellationRequested ? "Cancelled" : $"Freed {Format.Bytes(freed)}";
        if (skipped > 0) msg += $" · {Format.Count(skipped)} file(s) in use were left alone";
        return new CleanupResult(freed, deleted, skipped, msg);
    }

    static CleanupResult EmptyRecycleBin(CleanupItem item, AuditLog log)
    {
        var (before, count) = Native.QueryRecycleBin();
        int hr = Native.EmptyRecycleBin();
        if (hr == 0)
        {
            log.Write(item.Id, "empty-recycle-bin", "", before, "Emptied");
            return new CleanupResult(before, (int)count, 0, $"Emptied — freed {Format.Bytes(before)}");
        }
        if (before == 0 && hr == Native.E_UNEXPECTED)
            return new CleanupResult(0, 0, 0, "Already empty");
        log.Write(item.Id, "empty-recycle-bin", "", 0, $"HRESULT 0x{hr:X8}");
        return new CleanupResult(0, 0, 0, $"Failed (0x{hr:X8})", Failed: true);
    }

    static CleanupResult RunCommand(CleanupItem item, AuditLog log, Action<string>? progress)
    {
        long before = EnumerateAll(item, CancellationToken.None).Sum(SizeOf);
        var psi = new ProcessStartInfo(item.Command!, item.Arguments ?? "")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        log.Write(item.Id, "command", $"{item.Command} {item.Arguments}", 0, "Running");
        try
        {
            using var p = Process.Start(psi)!;
            p.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) progress?.Invoke(e.Data.Trim()); };
            p.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) progress?.Invoke(e.Data.Trim()); };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            p.WaitForExit();

            long after = EnumerateAll(item, CancellationToken.None).Sum(SizeOf);
            long freed = Math.Max(0, before - after);
            log.Write(item.Id, "command", item.Command!, freed, $"Exit {p.ExitCode}");
            return p.ExitCode == 0
                ? new CleanupResult(freed, 0, 0, freed > 0 ? $"Done — freed {Format.Bytes(freed)}" : "Done")
                : new CleanupResult(freed, 0, 0, $"Failed (exit code {p.ExitCode})", Failed: true);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            log.Write(item.Id, "command", item.Command!, 0, "Failed: " + ex.Message);
            return new CleanupResult(0, 0, 0, "Failed: " + ex.Message, Failed: true);
        }
    }

    static CleanupResult OpenSettings(CleanupItem item, AuditLog log)
    {
        Process.Start(new ProcessStartInfo(item.Command!) { UseShellExecute = true })?.Dispose();
        log.Write(item.Id, "open-settings", item.Command!, 0, "Opened");
        return new CleanupResult(0, 0, 0, "Opened Windows Settings — finish there");
    }

    static CleanupResult RecycleFiles(CleanupItem item, AuditLog log, Action<string>? progress, CancellationToken ct)
    {
        var files = EnumerateAll(item, ct).ToList();
        long moved = 0;
        int ok = 0, failed = 0;
        foreach (var batch in files.Chunk(50))
        {
            if (ct.IsCancellationRequested) break;
            progress?.Invoke($"Recycling {Format.Count(ok + failed)} / {Format.Count(files.Count)} files");
            int rc = Native.MoveToRecycleBin(batch.Select(f => f.FullName).ToArray());
            foreach (var f in batch)
            {
                bool gone = !File.Exists(f.FullName);
                long size = SizeOf(f);
                if (gone) { ok++; moved += size; }
                else failed++;
                log.Write(item.Id, "recycle", f.FullName, size, gone ? "Moved to Recycle Bin" : $"Could not move to Recycle Bin (code {rc})");
            }
        }
        string msg = $"Moved {Format.Count(ok)} file(s), {Format.Bytes(moved)} to the Recycle Bin";
        if (failed > 0) msg += $" · {Format.Count(failed)} item(s) in use or protected were left alone";
        return new CleanupResult(moved, ok, failed, msg);
    }

    // ---- Enumeration ---------------------------------------------------------------------------

    static IEnumerable<FileInfo> EnumerateAll(CleanupItem item, CancellationToken ct) =>
        item.Targets.SelectMany(t => Enumerate(t, ct));

    /// <summary>Files of a target that are old enough. Junctions/symlinks are never entered.</summary>
    static IEnumerable<FileInfo> Enumerate(CleanupTarget t, CancellationToken ct)
    {
        var cutoff = DateTime.Now.AddDays(-t.MinAgeDays);
        if (t.IsFile)
        {
            var fi = new FileInfo(t.Root);
            if (fi.Exists && fi.LastWriteTime <= cutoff) yield return fi;
            yield break;
        }
        if (!Directory.Exists(t.Root)) yield break;

        var stack = new Stack<DirectoryInfo>();
        stack.Push(new DirectoryInfo(t.Root));
        while (stack.Count > 0)
        {
            if (ct.IsCancellationRequested) yield break;
            var dir = stack.Pop();
            FileInfo[] files;
            DirectoryInfo[] subs;
            try
            {
                files = dir.GetFiles(t.Pattern, Opts);
                subs = t.Recurse ? dir.GetDirectories("*", Opts) : [];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

            foreach (var f in files)
                if ((f.Attributes & FileAttributes.ReparsePoint) == 0 && f.LastWriteTime <= cutoff)
                    yield return f;
            foreach (var s in subs)
                if ((s.Attributes & FileAttributes.ReparsePoint) == 0)
                    stack.Push(s);
        }
    }

    static void RemoveEmptyDirs(DirectoryInfo dir, bool isRoot, DateTime cutoff)
    {
        DirectoryInfo[] subs;
        try { subs = dir.GetDirectories("*", Opts); }
        catch { return; }
        foreach (var s in subs)
            if ((s.Attributes & FileAttributes.ReparsePoint) == 0)
                RemoveEmptyDirs(s, isRoot: false, cutoff);
        if (isRoot) return;
        try
        {
            if (dir.LastWriteTime <= cutoff && !dir.EnumerateFileSystemInfos("*", Opts).Any())
                dir.Delete(recursive: false);
        }
        catch { /* in use or protected: leave it */ }
    }

    static long SizeOf(FileInfo f)
    {
        try { return Scanner.IsOnlineOnly(f.Attributes) ? 0 : f.Length; }
        catch { return 0; }
    }
}
