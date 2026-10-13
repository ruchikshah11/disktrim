namespace DiskTrim.Tests;

/// <summary>The mover removes originals — every safety promise gets a test.</summary>
public class FileMoverTests
{
    static MoveResult Move(IReadOnlyList<string> sources, string to)
    {
        using var log = new AuditLog("test-move");
        return FileMover.Move(sources, to, log, null, CancellationToken.None);
    }

    [Fact]
    public void MovesAFileAndKeepsItsContentAndDate()
    {
        using var t = new TempDir();
        var when = new DateTime(2020, 5, 17, 10, 30, 0);
        var src = t.File(@"src\video.mp4", 300_000, when, 0x42);
        var dest = t.Dir("dest");

        var r = Move([src], dest);

        Assert.True(r.Failed == 0, string.Join(" | ", r.Errors));
        Assert.Equal(1, r.Moved);
        Assert.Equal(300_000, r.Bytes);
        Assert.False(File.Exists(src));
        var moved = Path.Combine(dest, "video.mp4");
        Assert.Equal(300_000, new FileInfo(moved).Length);
        Assert.All(File.ReadAllBytes(moved), b => Assert.Equal(0x42, b));
        Assert.Equal(when, File.GetLastWriteTime(moved));
    }

    [Fact]
    public void MovesAWholeFolderWithItsStructure()
    {
        using var t = new TempDir();
        t.File(@"Photos\2019\a.jpg", 1000);
        t.File(@"Photos\2020\b.jpg", 2000);
        t.File(@"Photos\c.jpg", 3000);
        var dest = t.Dir("external");

        var r = Move([t.P("Photos")], dest);

        Assert.Equal(3, r.Moved);
        Assert.Equal(6000, r.Bytes);
        Assert.False(Directory.Exists(t.P("Photos")));
        Assert.Contains(t.P("Photos"), r.MovedFolders);
        Assert.True(File.Exists(Path.Combine(dest, @"Photos\2019\a.jpg")));
        Assert.True(File.Exists(Path.Combine(dest, @"Photos\2020\b.jpg")));
        Assert.True(File.Exists(Path.Combine(dest, @"Photos\c.jpg")));
    }

    [Fact]
    public void NeverOverwritesAnExistingFile()
    {
        using var t = new TempDir();
        var src = t.File(@"src\report.pdf", 100, null, 1);
        var existing = t.File(@"dest\report.pdf", 50, null, 2);

        Move([src], t.P("dest"));

        Assert.Equal(50, new FileInfo(existing).Length);                       // untouched
        Assert.Equal(100, new FileInfo(t.P(@"dest\report (2).pdf")).Length);   // the moved one
    }

    [Fact]
    public void KeepsTheOriginalWhenItIsInUse()
    {
        using var t = new TempDir();
        var src = t.File(@"src\open.db", 1000);
        var dest = t.Dir("dest");

        MoveResult r;
        using (new FileStream(src, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            r = Move([src], dest);

        Assert.Equal(0, r.Moved);
        Assert.Equal(1, r.Failed);
        Assert.True(File.Exists(src));
        Assert.False(File.Exists(Path.Combine(dest, "open.db"))); // no half copy left behind
    }

    [Fact]
    public void KeepsBothCopiesWhenTheOriginalCannotBeRemoved()
    {
        using var t = new TempDir();
        var src = t.File(@"src\shared.bin", 1000);
        var dest = t.Dir("dest");

        MoveResult r;
        // Others may read but not delete: the copy works, removing the original doesn't.
        using (new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read))
            r = Move([src], dest);

        Assert.Equal(1, r.Failed);
        Assert.True(File.Exists(src));
        Assert.True(File.Exists(Path.Combine(dest, "shared.bin")));
        Assert.Contains("original could not be removed", r.Errors[0]);
    }

    [Fact]
    public void MovesReadOnlyFilesAndKeepsThemReadOnly()
    {
        using var t = new TempDir();
        var src = t.File(@"src\ro.txt", 10);
        File.SetAttributes(src, FileAttributes.ReadOnly);
        var dest = t.Dir("dest");

        Assert.Equal(1, Move([src], dest).Moved);
        Assert.True(File.GetAttributes(Path.Combine(dest, "ro.txt")).HasFlag(FileAttributes.ReadOnly));
    }

    [Fact]
    public void NeverFollowsAJunctionInsideTheFolder()
    {
        using var t = new TempDir();
        var precious = t.File(@"elsewhere\keep.docx", 500);
        t.File(@"project\code.cs", 100);
        t.Junction(@"project\link", t.P("elsewhere"));
        var dest = t.Dir("dest");

        var r = Move([t.P("project")], dest);

        Assert.Equal(1, r.Moved);
        Assert.True(File.Exists(precious), "a file behind a junction was moved!");
    }

    [Fact]
    public void RejectsADestinationInsideTheSource()
    {
        using var t = new TempDir();
        t.File(@"data\x.bin", 10);
        var inside = t.Dir(@"data\sub");
        Assert.Equal("The destination is inside one of the items being moved.", FileMover.Validate([t.P("data")], inside, 10));
    }

    [Fact]
    public void RejectsProtectedLocations()
    {
        using var t = new TempDir();
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        Assert.Contains("won't move", FileMover.Validate([Path.Combine(win, "System32")], t.Root, 1));
    }

    [Fact]
    public void RejectsWhenThereIsNotEnoughSpace()
    {
        using var t = new TempDir();
        var src = t.File(@"src\a.bin", 10);
        Assert.Contains("is needed", FileMover.Validate([src], t.Dir("dest"), long.MaxValue / 2));
    }

    [Fact]
    public void RecognisesTheSameDrive()
    {
        using var t = new TempDir();
        Assert.True(FileMover.IsSameDrive([t.P("a")], t.P("b")));
    }

    [Fact]
    public void EveryMoveIsLogged()
    {
        using var t = new TempDir();
        var src = t.File(@"src\a.bin", 10);
        var dest = t.Dir("dest");
        string file;
        using (var log = new AuditLog("test-move"))
        {
            FileMover.Move([src], dest, log, null, CancellationToken.None);
            file = log.FilePath;
        }
        Assert.Contains(File.ReadAllLines(file), l => l.Contains(",move,") && l.EndsWith(",OK (verified)"));
    }
}

public class SearchTests
{
    static ScanResult Scan(TempDir t)
    {
        t.File(@"Projects\Invoice-2024.pdf", 3000);
        t.File(@"Projects\old\invoice-2019.pdf", 1000);
        t.File(@"Downloads\setup.iso", 9000);
        t.File(@"Downloads\other.iso", 500);
        t.Dir(@"Invoices");
        t.File(@"Invoices\x.txt", 10);
        return Scanner.Scan(t.Root, [], new ScanProgress(), CancellationToken.None);
    }

    [Fact]
    public void FindsFilesAndFoldersByPartOfTheName()
    {
        using var t = new TempDir();
        var hits = ScanSearch.Find(Scan(t), "INVOICE", 50, out int total);
        Assert.Equal(3, total);
        Assert.Contains(hits, h => h.IsFolder && h.Name == "Invoices");
        Assert.Equal("Invoice-2024.pdf", hits[0].Name); // biggest first
    }

    [Fact]
    public void SupportsWildcards()
    {
        using var t = new TempDir();
        var hits = ScanSearch.Find(Scan(t), "*.iso", 50, out int total);
        Assert.Equal(2, total);
        Assert.Equal(["setup.iso", "other.iso"], hits.Select(h => h.Name));
        Assert.Equal(1, ScanSearch.Find(Scan(new TempDir()), "invoice-20??.pdf", 50, out _).Count(h => h.Name == "invoice-2019.pdf"));
    }

    [Fact]
    public void KeepsOnlyTheBiggestButCountsAll()
    {
        using var t = new TempDir();
        var hits = ScanSearch.Find(Scan(t), "o", 2, out int total);
        Assert.Equal(2, hits.Count);
        Assert.True(total >= 5);
        Assert.Equal("Downloads", hits[0].Name); // 9.5 KB folder beats every single file
    }

    [Fact]
    public void EmptyQueryFindsNothing()
    {
        using var t = new TempDir();
        Assert.Empty(ScanSearch.Find(Scan(t), "  ", 10, out int total));
        Assert.Equal(0, total);
    }
}
