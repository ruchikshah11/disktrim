using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using DiskTrim.UI;

namespace DiskTrim.Controls;

/// <summary>
/// Squarified treemap of a scanned folder: every block's area is its size, its colour its safety level.
/// Folders show their contents nested up to <see cref="MaxDepth"/> levels; tiny items are grouped into one block.
/// </summary>
public sealed class TreemapElement : FrameworkElement
{
    public sealed record Block(Rect Rect, string Label, DirNode? Folder, FileEntry? File, int Depth, bool Nested, long Size, SafetyLevel Level)
    {
        public bool IsGroup => Folder is null && File is null;
        public string? Path => Folder?.FullPath ?? File?.FullPath;
    }

    const int MaxDepth = 3;
    const double Header = 18;
    const double MinBlockArea = 30;
    const int MaxItemsPerFolder = 400;

    readonly List<Block> blocks = [];
    readonly Dictionary<string, SafetyLevel> levels = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<(SafetyLevel, int, bool), Brush> fills = [];
    readonly DrawingGroup cache = new();
    DirNode? root;
    Size laidOutFor;
    bool dirty = true;
    Block? hovered, selected;

    static readonly Pen Grid = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0x55, 0, 0, 0)), 1));
    static readonly Pen HoverPen = Freeze(new Pen(Brushes.White, 1.5));
    static readonly Pen SelectPen = Freeze(new Pen(Brushes.White, 3));
    static readonly Typeface Face = new("Segoe UI");
    static readonly Typeface FaceBold = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    public event Action<Block>? BlockSelected;
    public event Action<Block?>? BlockHovered;
    public event Action<DirNode>? ZoomRequested;

    public DirNode? Root
    {
        get => root;
        set
        {
            root = value;
            hovered = selected = null;
            dirty = true;
            InvalidateVisual();
        }
    }

    /// <summary>Call when the scan changed underneath (files deleted).</summary>
    public void Refresh()
    {
        dirty = true;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = RenderSize;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(size)); // makes the whole area hit-testable
        if (root is null || size.Width < 20 || size.Height < 20) return;

        if (dirty || laidOutFor != size)
        {
            blocks.Clear();
            LayoutFolder(root, new Rect(size), 0);
            laidOutFor = size;
            dirty = false;
            using var ctx = cache.Open();
            DrawBlocks(ctx);
        }

        dc.DrawDrawing(cache);
        if (hovered is not null) dc.DrawRectangle(null, HoverPen, Inset(hovered.Rect, 0.75));
        if (selected is not null && blocks.Contains(selected)) dc.DrawRectangle(null, SelectPen, Inset(selected.Rect, 1.5));
    }

    void DrawBlocks(DrawingContext dc)
    {
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        foreach (var b in blocks)
        {
            dc.DrawRectangle(Fill(b), Grid, b.Rect);
            if (b.Rect.Width < 34 || b.Rect.Height < 15) continue;

            // Labels are shown whole (wrapped if needed) or not at all — never cut. Hover shows every name.
            double maxWidth = b.Rect.Width - 8;
            // A nested folder only has its header strip; a leaf can use its whole height.
            double maxHeight = b.Nested ? Header - 2 : b.Rect.Height - 4;
            var title = new FormattedText(b.Label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                b.Nested ? FaceBold : Face, 12, Brushes.White, dpi) { MaxTextWidth = maxWidth };
            if (!Fits(title, maxWidth, maxHeight)) continue;

            dc.DrawText(title, new Point(b.Rect.X + 4, b.Rect.Y + 2));
            if (!b.Nested)
            {
                var size = new FormattedText(Format.Bytes(b.Size), CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                    Face, 11, SizeBrush, dpi) { MaxTextWidth = maxWidth };
                if (Fits(size, maxWidth, maxHeight - title.Height))
                    dc.DrawText(size, new Point(b.Rect.X + 4, b.Rect.Y + 2 + title.Height));
            }
        }
    }

    Brush Fill(Block b)
    {
        var key = (b.Level, Math.Min(b.Depth, MaxDepth), b.Nested);
        if (fills.TryGetValue(key, out var brush)) return brush;
        var c = SafetyBrushes.BlockColor(b.Level);
        // Containers are darker, deeper levels lighter, so nesting stays visible.
        double mix = b.Nested ? -0.35 + 0.08 * b.Depth : -0.12 + 0.07 * b.Depth;
        var color = mix < 0 ? Blend(c, Colors.Black, -mix) : Blend(c, Colors.White, mix);
        if (b.Level == SafetyLevel.Caution) color = Blend(color, Colors.Black, 0.15); // keep white text readable on amber
        brush = Freeze(new SolidColorBrush(color));
        fills[key] = brush;
        return brush;
    }

    // ---- layout ----------------------------------------------------------------------------------

    readonly record struct Item(long Size, DirNode? Dir, FileEntry? File, int GroupCount);

    void LayoutFolder(DirNode node, Rect rect, int depth)
    {
        if (node.Size <= 0 || rect.Width < 2 || rect.Height < 2) return;

        long minSize = (long)(node.Size * MinBlockArea / (rect.Width * rect.Height));
        var candidates = node.Children.Where(c => c.Size > 0).Select(c => new Item(c.Size, c, null, 0))
            .Concat(node.Files.Where(f => f.Size > 0).Take(MaxItemsPerFolder).Select(f => new Item(f.Size, null, f, 0)))
            .OrderByDescending(i => i.Size)
            .ToList();
        var items = candidates.Where(i => i.Size >= minSize).Take(MaxItemsPerFolder).ToList();
        long rest = node.Size - items.Sum(i => i.Size);
        int restCount = (int)Math.Max(0, node.Files.Count + node.Children.Count - items.Count);
        if (rest > 0) items.Add(new Item(rest, null, null, restCount));
        items.Sort((a, b) => b.Size.CompareTo(a.Size));

        Squarify(items, rect, (item, r) =>
        {
            string label = item.Dir?.Name ?? item.File?.Name ?? $"{Format.Count(item.GroupCount)} small files";
            var level = item.Dir is not null ? LevelOf(item.Dir.FullPath)
                : item.File is not null ? LevelOf(item.File.FullPath)
                : LevelOf(node.FullPath);
            bool nest = item.Dir is { IsLink: false } d && depth + 1 < MaxDepth && r.Width > 70 && r.Height > 46
                        && (d.Children.Count + d.Files.Count) > 0;
            blocks.Add(new Block(r, label, item.Dir, item.File, depth, nest, item.Size, level));
            if (nest)
                LayoutFolder(item.Dir!, new Rect(r.X + 2, r.Y + Header, Math.Max(0, r.Width - 4), Math.Max(0, r.Height - Header - 2)), depth + 1);
        });
    }

    SafetyLevel LevelOf(string path)
    {
        if (!levels.TryGetValue(path, out var level)) levels[path] = level = SafetyRules.Classify(path).Level;
        return level;
    }

    /// <summary>Squarified layout (Bruls, Huizing, van Wijk). Items must be sorted by size, largest first.</summary>
    static void Squarify(List<Item> items, Rect rect, Action<Item, Rect> place)
    {
        double total = items.Sum(i => (double)i.Size);
        if (total <= 0) return;
        double scale = rect.Width * rect.Height / total;
        var r = rect;
        int i = 0;
        while (i < items.Count && r.Width > 0.5 && r.Height > 0.5)
        {
            double side = Math.Min(r.Width, r.Height);
            int j = i;
            double rowSum = 0, worst = double.MaxValue;
            while (j < items.Count)
            {
                double a = items[j].Size * scale;
                double sum = rowSum + a;
                double maxA = items[i].Size * scale;
                double w = Math.Max(side * side * maxA / (sum * sum), sum * sum / (side * side * a));
                if (j > i && w > worst) break;
                worst = w;
                rowSum = sum;
                j++;
            }

            double thickness = rowSum / side;
            bool vertical = r.Width >= r.Height; // the row is a column along the left edge
            double offset = 0;
            for (int k = i; k < j; k++)
            {
                double len = items[k].Size * scale / thickness;
                var cell = vertical
                    ? new Rect(r.X, r.Y + offset, thickness, len)
                    : new Rect(r.X + offset, r.Y, len, thickness);
                place(items[k], cell);
                offset += len;
            }
            r = vertical
                ? new Rect(r.X + thickness, r.Y, Math.Max(0, r.Width - thickness), r.Height)
                : new Rect(r.X, r.Y + thickness, r.Width, Math.Max(0, r.Height - thickness));
            i = j;
        }
    }

    // ---- mouse -----------------------------------------------------------------------------------

    Block? HitTest(Point p)
    {
        for (int i = blocks.Count - 1; i >= 0; i--)
            if (blocks[i].Rect.Contains(p)) return blocks[i];
        return null;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var b = HitTest(e.GetPosition(this));
        if (b == hovered) return;
        hovered = b;
        BlockHovered?.Invoke(b);
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        hovered = null;
        BlockHovered?.Invoke(null);
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        var b = HitTest(e.GetPosition(this));
        if (b is null) return;
        if (e.ClickCount == 2)
        {
            var target = b.Folder ?? b.File?.Dir;
            if (target is not null && target != root && !target.IsLink) ZoomRequested?.Invoke(target);
            return;
        }
        selected = b;
        BlockSelected?.Invoke(b);
        InvalidateVisual();
    }

    // ---- helpers ---------------------------------------------------------------------------------

    static readonly Brush SizeBrush = Freeze(new SolidColorBrush(Color.FromArgb(0xD0, 0xFF, 0xFF, 0xFF)));

    /// <summary>True when the text is fully visible: no word broken past the width, no line below the height.</summary>
    static bool Fits(FormattedText text, double maxWidth, double maxHeight) =>
        maxWidth > 0 && maxHeight > 0 && text.Height <= maxHeight && text.WidthIncludingTrailingWhitespace <= maxWidth + 0.5
        && text.MinWidth <= maxWidth + 0.5;

    static Rect Inset(Rect r, double by) =>
        r.Width > 2 * by && r.Height > 2 * by ? new Rect(r.X + by, r.Y + by, r.Width - 2 * by, r.Height - 2 * by) : r;

    static Color Blend(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    static T Freeze<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }
}
