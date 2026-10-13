using DiskTrim.Cli;

namespace DiskTrim.Tests;

public class ArgsTests
{
    [Fact]
    public void CommandPositionalAndOptions()
    {
        var a = Args.Parse(["files", @"D:\data", "--min-size", "10MB", "--older-than", "1y", "--all"]);
        Assert.Equal("files", a.Command);
        Assert.Equal(@"D:\data", a.Arg(0));
        Assert.Equal(10L * 1024 * 1024, a.Size("min-size", 0));
        Assert.Equal(365, a.Age("older-than")!.Value.TotalDays);
        Assert.True(a.Has("all"));
    }

    [Fact]
    public void FlagsDoNotSwallowTheNextArgument()
    {
        var a = Args.Parse(["duplicates", "--recycle", "--yes", @"C:\x"]);
        Assert.True(a.Has("recycle"));
        Assert.True(a.AssumeYes);
        Assert.Equal(@"C:\x", a.Arg(0));
    }

    [Fact]
    public void BadOptionValuesAreReported()
    {
        var a = Args.Parse(["files", "--min-size", "huge"]);
        var ex = Assert.Throws<ArgumentException>(() => a.Size("min-size", 0));
        Assert.Contains("not a size", ex.Message);
    }

    [Theory]
    [InlineData(new[] { "--clean-safe" }, "clean-safe")]
    [InlineData(new[] { "--scan", @"D:\" }, "scan")]
    [InlineData(new[] { "--report", "r.html" }, "report")]
    [InlineData(new[] { "-h" }, null)]
    public void UnderstandsTheGuiSwitches(string[] argv, string? command)
    {
        var a = Args.Parse(argv);
        Assert.Equal(command, a.Command);
        if (command == "scan") Assert.Equal(@"D:\", a.Arg(0));
        if (command == "report") Assert.Equal("r.html", a.Get("out"));
    }

    [Fact]
    public void CleanupSelection()
    {
        var items = new List<CleanupItem>
        {
            new() { Id = "a", Group = "G", Title = "A", Description = "", Impact = "", Level = SafetyLevel.Safe, Bytes = 10 },
            new() { Id = "b", Group = "G", Title = "B", Description = "", Impact = "", Level = SafetyLevel.Caution, Bytes = 10 },
            new() { Id = "c", Group = "G", Title = "C", Description = "", Impact = "", Level = SafetyLevel.Safe, Bytes = 0 },
        };
        Assert.Equal(["a"], Commands.Select(items, "safe").Select(i => i.Id));
        Assert.Equal(["b", "a"], Commands.Select(items, "2, a").Select(i => i.Id));
    }
}
