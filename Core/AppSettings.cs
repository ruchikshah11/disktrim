namespace DiskTrim.Core;

public sealed class AppSettings
{
    /// <summary>Folders left out of every scan.</summary>
    public List<string> Exclusions { get; set; } = [];

    /// <summary>Window theme: Light, Dark or System.</summary>
    public string Theme { get; set; } = "System";

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile), Json) ?? new();
        }
        catch (Exception ex) { AppPaths.LogError(ex); }
        return new AppSettings();
    }

    public void Save() => File.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(this, Json));
}
