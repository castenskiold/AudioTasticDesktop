using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;

namespace AudioTastic.App.Ui.Controls;

/// <summary>
/// UI-01 / UI-03: lays out skin elements at their design rectangles. When the window is larger or smaller than the
/// skin's design size (resizable windows), an element anchored to both left and right stretches, one anchored only
/// to the right moves with the right edge; the same for top/bottom. Default anchor: left top.
/// </summary>
internal sealed class AnchorPanel(double designWidth, double designHeight) : Panel
{
    private static readonly ConditionalWeakTable<Control, Placement> Placements = [];

    private sealed record Placement(Rect Rect, bool Left, bool Right, bool Top, bool Bottom);

    /// <summary>Sets <paramref name="control"/>'s design rectangle and anchors ("left right top bottom").</summary>
    public static void Place(Control control, Rect rect, string? anchor)
    {
        var words = (anchor ?? "left top").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var left = words.Contains("left");
        var right = words.Contains("right");
        var top = words.Contains("top");
        var bottom = words.Contains("bottom");
        Placements.AddOrUpdate(control, new Placement(rect, left || !right, right, top || !bottom, bottom));
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var size = new Size(
            double.IsInfinity(availableSize.Width) ? designWidth : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? designHeight : availableSize.Height);
        foreach (var child in Children)
        {
            child.Measure(RectFor(child, size).Size);
        }
        return size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (var child in Children)
        {
            child.Arrange(RectFor(child, finalSize));
        }
        return finalSize;
    }

    private Rect RectFor(Control child, Size size)
    {
        if (!Placements.TryGetValue(child, out var p))
        {
            return new Rect(size);
        }
        var dx = size.Width - designWidth;
        var dy = size.Height - designHeight;
        var x = p.Rect.X;
        var w = p.Rect.Width;
        if (p.Left && p.Right)
        {
            w += dx;
        }
        else if (p.Right)
        {
            x += dx;
        }
        var y = p.Rect.Y;
        var h = p.Rect.Height;
        if (p.Top && p.Bottom)
        {
            h += dy;
        }
        else if (p.Bottom)
        {
            y += dy;
        }
        return new Rect(x, y, Math.Max(0, w), Math.Max(0, h));
    }
}
