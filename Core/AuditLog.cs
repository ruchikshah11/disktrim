namespace DiskTrim.Core;

/// <summary>CSV log of every file DiskTrim deletes, moves or skips (one file per cleanup run).</summary>
public sealed class AuditLog : IDisposable
{
    readonly object gate = new();
    StreamWriter? writer;

    public AuditLog(string kind = "cleanup")
    {
        FilePath = Path.Combine(AppPaths.LogsDir, $"{kind}-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
    }

    public string FilePath { get; }

    public bool HasEntries => writer is not null;

    public void Write(string item, string action, string path, long bytes, string result)
    {
        lock (gate)
        {
            writer ??= Open();
            writer.WriteLine(string.Join(',',
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                Csv(item), Csv(action), Csv(path), bytes.ToString(CultureInfo.InvariantCulture), Csv(result)));
        }
    }

    StreamWriter Open()
    {
        var w = new StreamWriter(FilePath, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        w.WriteLine($"# DiskTrim {AppPaths.Version} on {Environment.MachineName}");
        w.WriteLine("Time,Item,Action,Path,Bytes,Result");
        return w;
    }

    public void Dispose()
    {
        lock (gate) { writer?.Dispose(); writer = null; }
    }

    public static IEnumerable<FileInfo> List() =>
        new DirectoryInfo(AppPaths.LogsDir).GetFiles("*.csv").OrderByDescending(f => f.LastWriteTime);

    static string Csv(string s) =>
        s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
}
