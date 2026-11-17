using Avalonia;

namespace AudioTastic.App.Ui;

/// <summary>
/// UI-01: window snapping maths, in physical pixels. A moving window snaps its edges to the edges of other windows
/// (outside: side by side; inside: aligned) and to screen working-area edges when they are within the snap
/// distance. Windows whose edges touch form a docked group that moves with the main window.
/// </summary>
public static class SnapGeometry
{
    /// <summary>
    /// The offset to add to <paramref name="moving"/> so that its nearest edges line up with a target edge
    /// within <paramref name="distance"/>; zero on an axis where nothing is close.
    /// </summary>
    public static PixelVector Snap(PixelRect moving, IEnumerable<PixelRect> windows, IEnumerable<PixelRect> screens, int distance)
    {
        int? bestX = null, bestY = null;

        void ConsiderX(int candidate)
        {
            if (Math.Abs(candidate) <= distance && (bestX is null || Math.Abs(candidate) < Math.Abs(bestX.Value)))
            {
                bestX = candidate;
            }
        }

        void ConsiderY(int candidate)
        {
            if (Math.Abs(candidate) <= distance && (bestY is null || Math.Abs(candidate) < Math.Abs(bestY.Value)))
            {
                bestY = candidate;
            }
        }

        foreach (var w in windows)
        {
            // Only edges that overlap (or nearly) on the other axis are relevant.
            var nearVertically = moving.Y < w.Bottom + distance && moving.Bottom > w.Y - distance;
            var nearHorizontally = moving.X < w.Right + distance && moving.Right > w.X - distance;
            if (nearVertically)
            {
                ConsiderX(w.Right - moving.X);      // moving's left to w's right (side by side)
                ConsiderX(w.X - moving.Right);      // moving's right to w's left
                ConsiderX(w.X - moving.X);          // left edges aligned
                ConsiderX(w.Right - moving.Right);  // right edges aligned
            }
            if (nearHorizontally)
            {
                ConsiderY(w.Bottom - moving.Y);     // moving below w
                ConsiderY(w.Y - moving.Bottom);     // moving above w
                ConsiderY(w.Y - moving.Y);          // tops aligned
                ConsiderY(w.Bottom - moving.Bottom);
            }
        }
        foreach (var s in screens)
        {
            ConsiderX(s.X - moving.X);
            ConsiderX(s.Right - moving.Right);
            ConsiderY(s.Y - moving.Y);
            ConsiderY(s.Bottom - moving.Bottom);
        }
        return new PixelVector(bestX ?? 0, bestY ?? 0);
    }

    /// <summary>True when the two rectangles share an edge (within <paramref name="tolerance"/>) and overlap along it.</summary>
    public static bool Touch(PixelRect a, PixelRect b, int tolerance = 1)
    {
        var overlapY = a.Y < b.Bottom && b.Y < a.Bottom;
        var overlapX = a.X < b.Right && b.X < a.Right;
        return (overlapY && (Math.Abs(a.Right - b.X) <= tolerance || Math.Abs(b.Right - a.X) <= tolerance))
            || (overlapX && (Math.Abs(a.Bottom - b.Y) <= tolerance || Math.Abs(b.Bottom - a.Y) <= tolerance));
    }

    /// <summary>Indexes of <paramref name="others"/> docked to <paramref name="anchor"/>, directly or through each other.</summary>
    public static IReadOnlyList<int> DockedGroup(PixelRect anchor, IReadOnlyList<PixelRect> others)
    {
        var group = new List<int>();
        var frontier = new Queue<PixelRect>([anchor]);
        while (frontier.Count > 0)
        {
            var current = frontier.Dequeue();
            for (var i = 0; i < others.Count; i++)
            {
                if (!group.Contains(i) && Touch(current, others[i]))
                {
                    group.Add(i);
                    frontier.Enqueue(others[i]);
                }
            }
        }
        return group;
    }
}
