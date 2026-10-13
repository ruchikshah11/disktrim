using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DiskTrim.UI;

namespace DiskTrim.Pages;

public sealed record CategoryRow(string Name, long Count, long Bytes, double BarWidth, Brush Brush)
{
    public string CountText => $"{Format.Count(Count)} files";
    public string SizeText => Format.Bytes(Bytes);
}

public sealed record ExtensionRow(string Extension, string Category, long Count, long Bytes, double Share)
{
    public string SizeText => Format.Bytes(Bytes);
    public string ShareText => Format.Percent(Share);
    public string CountText => Format.Count(Count);
}

public partial class TypesView : UserControl, IPage
{
    const string All = "All types";
    static readonly Color[] Palette =
    [
        Color.FromRgb(0x38, 0x8B, 0xFD), Color.FromRgb(0x2D, 0xA4, 0x4E), Color.FromRgb(0xD2, 0x99, 0x22), Color.FromRgb(0xA3, 0x71, 0xF7),
        Color.FromRgb(0xE5, 0x53, 0x4B), Color.FromRgb(0x1F, 0xB2, 0xA6), Color.FromRgb(0xDB, 0x61, 0xA2), Color.FromRgb(0x8C, 0x95, 0x9F),
        Color.FromRgb(0xF0, 0x88, 0x3E), Color.FromRgb(0x6E, 0x77, 0x81),
    ];

    readonly AppState state = AppState.Current;
    int shownVersion = -1;
    List<ExtensionRow> allExtensions = [];

    public TypesView()
    {
        InitializeComponent();
        state.ScanChanged += () => { if (IsVisible) _ = RefreshAsync(); };
    }

    public void OnNavigatedTo(object? argument)
    {
        if (shownVersion != state.ScanVersion) _ = RefreshAsync();
    }

    async Task RefreshAsync()
    {
        shownVersion = state.ScanVersion;
        if (state.Scan is not { } scan)
        {
            Categories.ItemsSource = null;
            Extensions.ItemsSource = null;
            Hint.Text = "Scan a drive first.";
            return;
        }
        Hint.Text = "Counting…";

        var (cats, exts) = await Task.Run(() =>
        {
            var byExt = new Dictionary<string, (long Count, long Bytes)>();
            foreach (var f in scan.Files)
            {
                var e = f.Extension;
                var cur = byExt.GetValueOrDefault(e);
                byExt[e] = (cur.Count + 1, cur.Bytes + f.Size);
            }
            long total = Math.Max(1, scan.TotalBytes);
            var extRows = byExt.Select(kv => new ExtensionRow(kv.Key.Length == 0 ? "(none)" : "." + kv.Key,
                    FileCategories.Of(kv.Key), kv.Value.Count, kv.Value.Bytes, (double)kv.Value.Bytes / total))
                .OrderByDescending(r => r.Bytes).ToList();
            var catRows = extRows.GroupBy(r => r.Category)
                .Select(g => (Name: g.Key, Count: g.Sum(r => r.Count), Bytes: g.Sum(r => r.Bytes)))
                .OrderByDescending(c => c.Bytes).ToList();
            return (catRows, extRows);
        });

        long max = Math.Max(1, cats.FirstOrDefault().Bytes);
        var rows = new List<CategoryRow> { new(All, scan.Files.Count, scan.TotalBytes, 0, Brushes.Transparent) };
        for (int i = 0; i < cats.Count; i++)
            rows.Add(new CategoryRow(cats[i].Name, cats[i].Count, cats[i].Bytes, 180.0 * cats[i].Bytes / max,
                SafetyBrushes.Freeze(new SolidColorBrush(Palette[i % Palette.Length]))));

        allExtensions = exts;
        Categories.ItemsSource = rows;
        Categories.SelectedIndex = 0;
        Hint.Text = $"{Format.Count(scan.Files.Count)} files · {Format.Count(exts.Count)} extensions. Click a category to filter.";
    }

    void Categories_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var cat = (Categories.SelectedItem as CategoryRow)?.Name ?? All;
        var list = cat == All ? allExtensions : allExtensions.Where(x => x.Category == cat).ToList();
        Extensions.ItemsSource = list;
        ExtSummary.Text = $"{(cat == All ? "All extensions" : cat)}: {Format.Count(list.Count)} extensions · {Format.Bytes(list.Sum(x => x.Bytes))}";
    }

    void ShowFiles_Click(object sender, RoutedEventArgs e)
    {
        var cat = (Categories.SelectedItem as CategoryRow)?.Name;
        state.Navigate("files", new FilesFilter(MinSize: 0, Category: cat is null or All ? null : cat));
    }
}
