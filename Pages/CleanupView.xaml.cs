using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using DiskTrim.UI;

namespace DiskTrim.Pages;

public sealed class CleanupItemVM(CleanupItem item, Action changed) : Observable
{
    bool isChecked;

    public CleanupItem Item { get; } = item;
    public string Group => Item.Group;
    public string Title => Item.Title;
    public string Description => Item.Description;
    public string ImpactText => "If you clean it: " + Item.Impact;
    public SafetyLevel Level => Item.Level;
    public string SizeText => Item.SizeHint ?? Format.Bytes(Item.Bytes);
    public string Status => Item.Status + (Item.NeedsAdmin && AppPaths.IsAdmin ? " · administrator" : "");

    public Brush StatusBrush => Item.IsBlocked ? SafetyBrushes.Foreground(SafetyLevel.Caution)
        : Item.Status == "Ready" ? SafetyBrushes.Foreground(SafetyLevel.Safe)
        : Application.Current.TryFindResource("TextFillColorSecondaryBrush") as Brush ?? Brushes.Gray;

    public bool CanCheck => !Item.IsBlocked && (Item.Bytes > 0 || Item.Kind is CleanupKind.Command or CleanupKind.OpenSettings);

    public bool IsChecked
    {
        get => isChecked;
        set { if (Set(ref isChecked, value && CanCheck)) changed(); }
    }
}

public partial class CleanupView : UserControl, IPage
{
    readonly AppState state = AppState.Current;
    List<CleanupItemVM> rows = [];

    public CleanupView()
    {
        InitializeComponent();
        state.CleanupMeasured += Show;
    }

    public void OnNavigatedTo(object? argument)
    {
        if (state.CleanupItems is null) _ = MeasureAsync();
        else Show();
    }

    async Task MeasureAsync()
    {
        Summary.Text = "Measuring caches and temporary files…";
        await state.MeasureAsync();
    }

    void Show()
    {
        if (state.CleanupItems is not { } items) return;
        rows = items.Select(i => new CleanupItemVM(i, UpdateSummary)).ToList();
        var view = new ListCollectionView(rows);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(CleanupItemVM.Group)));
        ItemList.ItemsSource = view;
        UpdateSummary();
    }

    void UpdateSummary()
    {
        var picked = rows.Where(r => r.IsChecked).ToList();
        long safe = rows.Where(r => r.Level == SafetyLevel.Safe && !r.Item.IsBlocked).Sum(r => r.Item.Bytes);
        Summary.Text = picked.Count == 0
            ? $"Nothing selected  ·  safe to free right now: {Format.Bytes(safe)}"
            : $"{picked.Count} item(s) selected · about {Format.Bytes(picked.Sum(r => r.Item.Bytes))}";
    }

    async void Measure_Click(object sender, RoutedEventArgs e) => await MeasureAsync();

    void SelectSafe_Click(object sender, RoutedEventArgs e)
    {
        foreach (var r in rows)
            r.IsChecked = r.Level == SafetyLevel.Safe && r.Item.Bytes > 0 && r.Item.Kind is CleanupKind.Files or CleanupKind.Command;
    }

    async void Clean_Click(object sender, RoutedEventArgs e)
    {
        var picked = rows.Where(r => r.IsChecked).Select(r => r.Item).ToList();
        if (picked.Count == 0) { Dialogs.Info("Tick at least one item first."); return; }
        await CleanupRunner.RunAsync(picked);
    }
}
