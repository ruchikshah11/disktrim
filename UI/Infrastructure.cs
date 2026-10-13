using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace DiskTrim.UI;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
}

/// <summary>Badge and treemap colours per safety level; readable in light and dark theme.</summary>
public static class SafetyBrushes
{
    static readonly Dictionary<SafetyLevel, Color> Base = new()
    {
        [SafetyLevel.Safe] = Color.FromRgb(0x2D, 0xA4, 0x4E),
        [SafetyLevel.Caution] = Color.FromRgb(0xD2, 0x99, 0x22),
        [SafetyLevel.Review] = Color.FromRgb(0x38, 0x8B, 0xFD),
        [SafetyLevel.Keep] = Color.FromRgb(0xE5, 0x53, 0x4B),
    };

    static readonly Dictionary<SafetyLevel, Brush> Fg = Base.ToDictionary(kv => kv.Key, kv => Freeze(new SolidColorBrush(kv.Value)));
    static readonly Dictionary<SafetyLevel, Brush> Bg = Base.ToDictionary(kv => kv.Key,
        kv => Freeze(new SolidColorBrush(Color.FromArgb(0x30, kv.Value.R, kv.Value.G, kv.Value.B))));

    public static Brush Foreground(SafetyLevel level) => Fg[level];
    public static Brush Background(SafetyLevel level) => Bg[level];
    public static Color BlockColor(SafetyLevel level) => Base[level];

    public static Brush Freeze(Brush b)
    {
        b.Freeze();
        return b;
    }
}

public sealed class LevelToBrushConverter : IValueConverter
{
    public bool Foreground { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is SafetyLevel l ? (Foreground ? SafetyBrushes.Foreground(l) : SafetyBrushes.Background(l)) : Brushes.Transparent;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class LevelToTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is SafetyLevel l ? SafetyVerdict.LevelText(l) : "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public static class Dialogs
{
    static Window? Owner => Application.Current?.MainWindow is { IsLoaded: true } w ? w : null;

    public static bool Confirm(string title, string text, bool warning = false)
    {
        var icon = warning ? MessageBoxImage.Warning : MessageBoxImage.Question;
        var result = Owner is { } o
            ? MessageBox.Show(o, text, title, MessageBoxButton.YesNo, icon, MessageBoxResult.No)
            : MessageBox.Show(text, title, MessageBoxButton.YesNo, icon, MessageBoxResult.No);
        return result == MessageBoxResult.Yes;
    }

    public static void Info(string text, string title = "DiskTrim") => Show(text, title, MessageBoxImage.Information);

    public static void Error(string text, string title = "DiskTrim") => Show(text, title, MessageBoxImage.Error);

    static void Show(string text, string title, MessageBoxImage icon)
    {
        if (Owner is { } o) MessageBox.Show(o, text, title, MessageBoxButton.OK, icon);
        else MessageBox.Show(text, title, MessageBoxButton.OK, icon);
    }

    public static string Bullets(IEnumerable<string> lines, int max = 12)
    {
        var list = lines.ToList();
        var shown = list.Take(max).Select(l => "• " + l);
        return string.Join(Environment.NewLine, shown) + (list.Count > max ? $"{Environment.NewLine}… and {list.Count - max} more" : "");
    }
}

/// <summary>Hides the console window when the window UI starts from Explorer (double-click).</summary>
public static class ConsoleWindow
{
    [DllImport("kernel32.dll")] static extern IntPtr GetConsoleWindow();
    [DllImport("kernel32.dll")] static extern uint GetConsoleProcessList(uint[] processList, uint count);
    [DllImport("kernel32.dll")] static extern bool FreeConsole();
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    public static void Detach()
    {
        // Only hide the console if it was created for us — never the terminal the user typed in.
        if (GetConsoleProcessList(new uint[2], 2) == 1 && GetConsoleWindow() is var h && h != IntPtr.Zero)
            ShowWindow(h, 0);
        FreeConsole();
    }
}
