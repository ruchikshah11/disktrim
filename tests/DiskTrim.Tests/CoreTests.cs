namespace DiskTrim.Tests;

public class FormatTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(10L * 1024 * 1024, "10 MB")]
    [InlineData(150L * 1024 * 1024 * 1024, "150 GB")]
    [InlineData(-2048, "-2 KB")]
    public void Bytes(long value, string expected) => Assert.Equal(expected, Format.Bytes(value));

    [Theory]
    [InlineData("100MB", 100L * 1024 * 1024)]
    [InlineData("1.5 GB", 1536L * 1024 * 1024)]
    [InlineData("1,5gb", 1536L * 1024 * 1024)]
    [InlineData("500k", 500L * 1024)]
    [InlineData("2048", 2048L)]
    public void ParsesSizes(string text, long expected)
    {
        Assert.True(Format.TryParseSize(text, out var v));
        Assert.Equal(expected, v);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("10 XB")]
    public void RejectsBadSizes(string text) => Assert.False(Format.TryParseSize(text, out _));

    [Theory]
    [InlineData("30d", 30)]
    [InlineData("2w", 14)]
    [InlineData("6m", 180)]
    [InlineData("1y", 365)]
    [InlineData("10", 10)]
    public void ParsesAges(string text, int days)
    {
        Assert.True(Format.TryParseAge(text, out var age));
        Assert.Equal(days, age.TotalDays);
    }
}

public class SafetyRulesTests
{
    static readonly string Win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    static readonly string Profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    static readonly string Local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    static readonly string Pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    static readonly string Sys = AppPaths.SystemRoot.TrimEnd('\\');

    static SafetyVerdict V(string path) => SafetyRules.Classify(path);

    [Fact] public void WindowsIsKeep() => Assert.Equal(SafetyLevel.Keep, V(Path.Combine(Win, @"System32\kernel32.dll")).Level);
    [Fact] public void WindowsTempIsSafe() => Assert.Equal("Temporary files", V(Path.Combine(Win, @"Temp\x.log")).Title);
    [Fact] public void UserTempIsSafe() => Assert.Equal(SafetyLevel.Safe, V(Path.Combine(Local, @"Temp\setup.tmp")).Level);
    [Fact] public void WinSxSIsKeep() => Assert.Equal("Windows component store", V(Path.Combine(Win, "WinSxS")).Title);
    [Fact] public void DocumentsIsYourDecision() => Assert.Equal(SafetyLevel.Review, V(Path.Combine(Profile, @"Documents\notes")).Level);
    [Fact] public void PagefileIsKeep() => Assert.Equal(SafetyLevel.Keep, V(Sys + @"\pagefile.sys").Level);
    [Fact] public void RegistryHiveIsKeep() => Assert.Equal(SafetyLevel.Keep, V(Path.Combine(Profile, "NTUSER.DAT")).Level);
    [Fact] public void DriveRoot() => Assert.Equal("Drive", V(Sys + "\\").Title);
    [Fact] public void ChromeCacheIsSafe() => Assert.Equal("Browser cache", V(Path.Combine(Local, @"Google\Chrome\User Data\Default\Cache\data_1")).Title);
    [Fact] public void RoamingIsKeep() => Assert.Equal(SafetyLevel.Keep, V(Path.Combine(Profile, @"AppData\Roaming\SomeApp\config.json")).Level);
    [Fact] public void PstIsKeep() => Assert.Equal(SafetyLevel.Keep, V(Path.Combine(Profile, @"Documents\Outlook\archive.pst")).Level);

    [Fact]
    public void GenericRuleDoesNotRelabelATempFolder() =>
        Assert.Equal("Temporary files", V(Path.Combine(Local, @"Temp\build\node_modules\x")).Title);

    [Fact]
    public void NodeModulesInYourFilesIsCaution() =>
        Assert.Equal("Node.js dependencies", V(Path.Combine(Profile, @"Documents\app\node_modules\left-pad")).Title);

    [Fact]
    public void GenericRuleNeverOverridesProtectedLocation() =>
        Assert.Equal(SafetyLevel.Keep, V(Path.Combine(Pf, @"nodejs\node_modules\npm")).Level);

    [Fact]
    public void BinIsBuildOutputOnlyNextToAProject()
    {
        // Outside %TEMP%: inside it everything is already "Temporary files".
        using var t = new TempDir(Path.Combine(AppContext.BaseDirectory, "scratch"));
        t.File(@"withproj\app.csproj", 10);
        t.Dir(@"withproj\bin");
        t.Dir(@"noproj\bin");
        Assert.Equal("Build output", V(t.P(@"withproj\bin")).Title);
        Assert.NotEqual("Build output", V(t.P(@"noproj\bin")).Title);
    }
}

public class FileCategoryTests
{
    [Theory]
    [InlineData("mp4", "Videos")]
    [InlineData("JPG", "Photos")]
    [InlineData("ps1", "Code & dev")]
    [InlineData("dll", "Programs & system")]
    [InlineData("xyz", "Other")]
    [InlineData("", "Other")]
    public void Categorises(string ext, string category) => Assert.Equal(category, FileCategories.Of(ext));
}

public class ScannerTests
{
    static ScanResult Scan(string root, params string[] exclusions) =>
        Scanner.Scan(root, exclusions, new ScanProgress(), CancellationToken.None);

    [Fact]
    public void SumsSizesAndSortsBiggestFirst()
    {
        using var t = new TempDir();
        t.File(@"a\one.bin", 1000);
        t.File(@"a\two.bin", 3000);
        t.File(@"b\three.bin", 500);
        t.File("top.bin", 100);

        var scan = Scan(t.Root);
        Assert.Equal(4600, scan.TotalBytes);
        Assert.Equal(4, scan.Root.FileCount);
        Assert.Equal(2, scan.Root.FolderCount);
        Assert.Equal(4, scan.Files.Count);
        Assert.Equal("a", scan.Root.Children[0].Name);
        Assert.Equal("two.bin", scan.Root.Children[0].Files[0].Name);
        Assert.False(scan.Cancelled);
    }

    [Fact]
    public void ListsJunctionsButNeverFollowsThem()
    {
        using var t = new TempDir();
        var real = t.Dir("real");
        t.File(@"real\big.bin", 5000);
        t.Junction("link", real);

        var scan = Scan(t.Root);
        Assert.Equal(5000, scan.TotalBytes); // not counted twice
        var link = scan.Root.Children.Single(c => c.Name == "link");
        Assert.True(link.IsLink);
        Assert.Equal(0, link.Size);
    }

    [Fact]
    public void SkipsExcludedFolders()
    {
        using var t = new TempDir();
        t.File(@"keep\a.bin", 100);
        t.File(@"skip\b.bin", 900);
        var scan = Scan(t.Root, t.P("skip"));
        Assert.Equal(100, scan.TotalBytes);
        Assert.DoesNotContain(scan.Root.Children, c => c.Name == "skip");
    }

    [Fact]
    public void FindsFoldersByPath()
    {
        using var t = new TempDir();
        t.File(@"x\y\z.bin", 10);
        var scan = Scan(t.Root);
        Assert.Equal("y", scan.Find(t.P(@"x\Y"))!.Name);
        Assert.Same(scan.Root, scan.Find(t.Root + "\\"));
        Assert.Null(scan.Find(@"Z:\elsewhere"));
        Assert.Null(scan.Find("bad|path"));
    }

    [Fact]
    public void RemovePathsCorrectsEveryParent()
    {
        using var t = new TempDir();
        t.File(@"a\b\file1.bin", 1000);
        t.File(@"a\b\file2.bin", 2000);
        t.File(@"a\c\file3.bin", 4000);
        var scan = Scan(t.Root);

        scan.RemovePaths([t.P(@"a\b\file1.bin"), t.P(@"a\c")]);

        Assert.Equal(2000, scan.TotalBytes);
        Assert.Equal(2000, scan.Find(t.P("a"))!.Size);
        Assert.Equal(1, scan.Root.FileCount);
        Assert.Single(scan.Files);
        Assert.Null(scan.Find(t.P(@"a\c")));
    }

    [Fact]
    public void StopsWhenCancelled()
    {
        using var t = new TempDir();
        for (int i = 0; i < 20; i++) t.File($@"d{i}\f.bin", 10);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var scan = Scanner.Scan(t.Root, [], new ScanProgress(), cts.Token);
        Assert.True(scan.Cancelled);
    }

    [Theory]
    [InlineData("c:", @"c:\")]
    [InlineData("\"C:\\\"", @"C:\")]
    [InlineData("C:\"", @"C:\")]
    public void NormalisesDriveInput(string input, string expected) =>
        Assert.Equal(expected, Scanner.NormalizeRoot(input), ignoreCase: true);
}

public class DuplicatesTests
{
    [Fact]
    public void FindsIdenticalFilesOnly()
    {
        using var t = new TempDir();
        var old = DateTime.Now.AddDays(-10);
        t.File(@"a\photo.jpg", 200_000, old, 0x11);         // bigger than the 64 KB first pass
        t.File(@"b\photo-copy.jpg", 200_000, null, 0x11);
        t.File(@"c\same-size-other.jpg", 200_000, null, 0x22);
        t.File(@"d\small1.txt", 5000, null, 0x33);
        t.File(@"d\small2.txt", 5000, null, 0x33);

        var scan = Scanner.Scan(t.Root, [], new ScanProgress(), CancellationToken.None);
        var groups = Duplicates.Find(scan.Files, 1, new ScanProgress(), CancellationToken.None, out int unreadable);

        Assert.Equal(0, unreadable);
        Assert.Equal(2, groups.Count);
        var big = groups[0];
        Assert.Equal(200_000, big.Size);
        Assert.Equal(2, big.Files.Count);
        Assert.Equal(200_000, big.Wasted);
        Assert.Equal("photo.jpg", big.Keeper(KeepRule.Oldest).Name);
        Assert.Equal("photo-copy.jpg", big.Keeper(KeepRule.Newest).Name);
    }

    [Fact]
    public void HardLinksAreNotDuplicates()
    {
        using var t = new TempDir();
        var original = t.File("orig.bin", 100_000, null, 0x55);
        t.HardLink("link.bin", original);
        var scan = Scanner.Scan(t.Root, [], new ScanProgress(), CancellationToken.None);
        Assert.Empty(Duplicates.Find(scan.Files, 1, new ScanProgress(), CancellationToken.None, out _));
    }

    [Fact]
    public void RespectsMinimumSize()
    {
        using var t = new TempDir();
        t.File("a.bin", 1000, null, 1);
        t.File("b.bin", 1000, null, 1);
        var scan = Scanner.Scan(t.Root, [], new ScanProgress(), CancellationToken.None);
        Assert.Empty(Duplicates.Find(scan.Files, 2000, new ScanProgress(), CancellationToken.None, out _));
    }
}
