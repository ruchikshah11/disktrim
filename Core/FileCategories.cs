namespace DiskTrim.Core;

public static class FileCategories
{
    public const string Other = "Other";

    static readonly (string Name, string Extensions)[] Data =
    [
        ("Videos", "mp4 mov mkv avi wmv m4v mts m2ts webm flv 3gp mpg mpeg"),
        ("Photos", "jpg jpeg png gif bmp tif tiff heic heif webp raw cr2 cr3 nef arw dng orf rw2 psd"),
        ("Music", "mp3 wav flac aac m4a wma ogg opus"),
        ("Documents", "pdf doc docx xls xlsx xlsm ppt pptx txt rtf odt ods csv one pst ost msg eml vsdx"),
        ("Archives", "zip 7z rar tar gz tgz bz2 xz cab"),
        ("Installers", "msi msix msixbundle appx appxbundle msu vsix nupkg aea"),
        ("Disk images", "iso img vhd vhdx vmdk qcow2 wim esd dmg"),
        ("Code & dev", "cs js ts json xml pdb lib obj class jar java py go rs cpp h map tsx node ps1 psm1 psd1 ps1xml bat cmd sh"),
        ("Programs & system", "exe dll so sys mui cat manifest ocx drv efi bin dat db sqlite ldb log etl evtx tmp"),
    ];

    public static IReadOnlyList<string> Names { get; } = [.. Data.Select(d => d.Name), Other];

    static readonly Dictionary<string, string> Map = BuildMap();

    public static string Of(string extension) => Map.TryGetValue(extension, out var c) ? c : Other;

    public static bool IsMedia(string category) => category is "Photos" or "Videos";

    static Dictionary<string, string> BuildMap()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, exts) in Data)
            foreach (var e in exts.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                map.TryAdd(e, name);
        return map;
    }
}
