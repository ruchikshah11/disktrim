namespace DiskTrim.Tests;

/// <summary>The cleanup engine is the part that deletes things — every safety rule gets a test.</summary>
public class CleanupEngineTests
{
    static CleanupItem Item(params CleanupTarget[] targets) => new()
    {
        Id = "test", Group = "Test", Title = "Test item", Description = "", Impact = "",
        Level = SafetyLevel.Safe, Targets = [.. targets],
    };

    static CleanupResult Clean(CleanupItem item)
    {
        using var log = new AuditLog("test");
        CleanupEngine.Measure(item);
        return CleanupEngine.Clean(item, log, null, CancellationToken.None);
    }

    [Fact]
    public void MeasuresWhatItWouldDelete()
    {
        using var t = new TempDir();
        t.File(@"cache\a.bin", 1000);
        t.File(@"cache\sub\b.bin", 2000);
        var item = Item(new CleanupTarget(t.P("cache")));
        CleanupEngine.Measure(item);
        Assert.Equal(3000, item.Bytes);
        Assert.Equal(2, item.FileCount);
        Assert.Equal("Ready", item.Status);
    }

    [Fact]
    public void DeletesFilesAndEmptyFoldersButKeepsTheTargetFolder()
    {
        using var t = new TempDir();
        t.File(@"cache\a.bin", 1000);
        t.File(@"cache\sub\deeper\b.bin", 2000);

        var r = Clean(Item(new CleanupTarget(t.P("cache"))));

        Assert.Equal(3000, r.FreedBytes);
        Assert.Equal(2, r.Deleted);
        Assert.Equal(0, r.Skipped);
        Assert.True(Directory.Exists(t.P("cache")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(t.P("cache")));
    }

    [Fact]
    public void KeepsFilesNewerThanTheMinimumAge()
    {
        using var t = new TempDir();
        t.File(@"temp\old.tmp", 100, DateTime.Now.AddDays(-3));
        t.File(@"temp\fresh.tmp", 100, DateTime.Now.AddHours(-2));

        var r = Clean(Item(new CleanupTarget(t.P("temp"), MinAgeDays: 1)));

        Assert.Equal(1, r.Deleted);
        Assert.False(File.Exists(t.P(@"temp\old.tmp")));
        Assert.True(File.Exists(t.P(@"temp\fresh.tmp")));
    }

    [Fact]
    public void SkipsFilesInUse()
    {
        using var t = new TempDir();
        var locked = t.File(@"cache\locked.bin", 100);
        t.File(@"cache\free.bin", 100);

        CleanupResult r;
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
            r = Clean(Item(new CleanupTarget(t.P("cache"))));

        Assert.Equal(1, r.Deleted);
        Assert.Equal(1, r.Skipped);
        Assert.True(File.Exists(locked));
        Assert.Contains("in use", r.Message);
    }

    [Fact]
    public void DeletesReadOnlyFiles()
    {
        using var t = new TempDir();
        var ro = t.File(@"cache\ro.bin", 100);
        File.SetAttributes(ro, FileAttributes.ReadOnly);
        Assert.Equal(1, Clean(Item(new CleanupTarget(t.P("cache")))).Deleted);
        Assert.False(File.Exists(ro));
    }

    [Fact]
    public void NeverDeletesThroughAJunction()
    {
        using var t = new TempDir();
        var precious = t.File(@"precious\document.docx", 5000);
        t.File(@"cache\junk.bin", 100);
        t.Junction(@"cache\link-to-precious", t.P("precious"));

        var item = Item(new CleanupTarget(t.P("cache")));
        CleanupEngine.Measure(item);
        Assert.Equal(100, item.Bytes);

        Clean(item);
        Assert.True(File.Exists(precious), "a file behind a junction was deleted!");
    }

    [Fact]
    public void PatternAndNonRecursiveTargets()
    {
        using var t = new TempDir();
        t.File(@"explorer\thumbcache_32.db", 100);
        t.File(@"explorer\iconcache.db", 100);
        t.File(@"explorer\sub\thumbcache_64.db", 100);

        var r = Clean(Item(new CleanupTarget(t.P("explorer"), "thumbcache_*.db", Recurse: false)));

        Assert.Equal(1, r.Deleted);
        Assert.True(File.Exists(t.P(@"explorer\iconcache.db")));
        Assert.True(File.Exists(t.P(@"explorer\sub\thumbcache_64.db")));
    }

    [Fact]
    public void SingleFileTarget()
    {
        using var t = new TempDir();
        var dump = t.File("MEMORY.DMP", 4096);
        Assert.Equal(4096, Clean(Item(new CleanupTarget(dump, IsFile: true))).FreedBytes);
        Assert.False(File.Exists(dump));
    }

    [Fact]
    public void BlockedItemsAreNotCleaned()
    {
        using var t = new TempDir();
        var f = t.File(@"cache\a.bin", 100);
        var item = new CleanupItem
        {
            Id = "blocked", Group = "Test", Title = "Blocked", Description = "", Impact = "",
            Targets = [new(t.P("cache"))],
            BlockingProcesses = [Process.GetCurrentProcess().ProcessName], AppName = "the test runner",
        };
        var r = Clean(item);
        Assert.True(r.Failed);
        Assert.Equal("Close the test runner first", r.Message);
        Assert.True(File.Exists(f));
    }

    [Fact]
    public void EveryDeletionIsLogged()
    {
        using var t = new TempDir();
        var f = t.File(@"cache\a,b.bin", 100); // comma must be quoted in the CSV
        using var log = new AuditLog("test");
        var item = Item(new CleanupTarget(t.P("cache")));
        CleanupEngine.Clean(item, log, null, CancellationToken.None);
        log.Dispose();

        var lines = File.ReadAllLines(log.FilePath);
        Assert.StartsWith("# DiskTrim", lines[0]);
        Assert.Equal("Time,Item,Action,Path,Bytes,Result", lines[1]);
        Assert.Contains(lines, l => l.Contains($"\"{f}\"") && l.Contains(",delete,") && l.EndsWith(",Deleted"));
    }

    [Fact]
    public void NothingToCleanStatus()
    {
        using var t = new TempDir();
        t.Dir("empty");
        var item = Item(new CleanupTarget(t.P("empty")));
        CleanupEngine.Measure(item);
        Assert.Equal("Nothing to clean", item.Status);
    }
}

public class CatalogTests
{
    [Fact]
    public void IdsAreUniqueAndItemsDescribed()
    {
        var items = CleanupCatalog.Build();
        Assert.NotEmpty(items);
        Assert.Equal(items.Count, items.Select(i => i.Id).Distinct().Count());
        Assert.All(items, i =>
        {
            Assert.False(string.IsNullOrWhiteSpace(i.Title));
            Assert.False(string.IsNullOrWhiteSpace(i.Impact));
            if (i.Kind is CleanupKind.Command or CleanupKind.OpenSettings) Assert.NotNull(i.Command);
        });
        Assert.Contains(items, i => i.Id == "user-temp");
        Assert.Contains(items, i => i.Kind == CleanupKind.RecycleBin);
    }

    [Fact]
    public void NoCleanupTargetIsAProtectedLocation()
    {
        // A Files target must never be somewhere the safety rules call "Do not delete".
        foreach (var item in CleanupCatalog.Build().Where(i => i.Kind == CleanupKind.Files))
            foreach (var target in item.Targets)
                Assert.True(SafetyRules.Classify(target.Root).Level != SafetyLevel.Keep,
                    $"{item.Id} targets protected {target.Root}");
    }
}

public class HistoryTests
{
    static Snapshot Snap(params (string Path, long Size)[] folders) =>
        new() { Root = @"C:\", Folders = folders.ToDictionary(f => f.Path, f => f.Size) };

    [Fact]
    public void ListsChangesWhereTheyHappened()
    {
        var before = Snap((@"C:\", 1000), (@"C:\Users", 600), (@"C:\Users\me", 600), (@"C:\Users\me\Videos", 100), (@"C:\Temp", 400));
        var after = Snap((@"C:\", 1500), (@"C:\Users", 1200), (@"C:\Users\me", 1200), (@"C:\Users\me\Videos", 700), (@"C:\Temp", 300));

        var changes = History.Compare(before, after, threshold: 50);

        Assert.Equal(@"C:\Users\me\Videos", changes[0].Path);
        Assert.Equal(600, changes[0].Delta);
        Assert.Contains(changes, c => c.Path == @"C:\Temp" && c.Delta == -100);
        Assert.DoesNotContain(changes, c => c.Path == @"C:\Users"); // explained by Videos
    }

    [Fact]
    public void IgnoresSmallChanges()
    {
        var changes = History.Compare(Snap((@"C:\A", 100)), Snap((@"C:\A", 120)), threshold: 50);
        Assert.Empty(changes);
    }
}

public class ReportTests
{
    [Fact]
    public void WritesASelfContainedReport()
    {
        using var t = new TempDir();
        t.File(@"data\big & file.bin", 5000);
        var scan = Scanner.Scan(t.P("data"), [], new ScanProgress(), CancellationToken.None);
        var item = new CleanupItem { Id = "x", Group = "G", Title = "Cache & stuff", Description = "d", Impact = "i", Bytes = 42 };
        var file = t.P("report.html");

        ReportWriter.Write(file, scan, [item]);

        var html = File.ReadAllText(file);
        Assert.StartsWith("<!doctype html>", html);
        Assert.Contains("Biggest folders", html);
        Assert.Contains("big &amp; file.bin", html);   // HTML-encoded
        Assert.Contains("Cache &amp; stuff", html);
        Assert.DoesNotContain("<script", html);
    }
}
