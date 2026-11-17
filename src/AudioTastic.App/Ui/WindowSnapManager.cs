using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace AudioTastic.App.Ui;

/// <summary>
/// UI-01: moves borderless player windows with the pointer, snapping to other windows and screen edges within
/// 10 px, and moves windows docked to the main window along with it. Dragging is done here (not by the OS) so
/// snapping works the same on Windows, macOS and Linux/X11.
/// </summary>
public sealed class WindowSnapManager
{
    public const int SnapDistance = 10;

    private readonly List<Window> _windows = [];
    private Window? _main;
    private Control? _source;
    private Window? _dragged;
    private PixelPoint _startPointer;
    private List<(Window Window, PixelPoint Start)> _group = [];

    /// <summary>Raised when a drag ends (positions should be saved).</summary>
    public event EventHandler? DragCompleted;

    public void Register(Window window, bool isMain = false)
    {
        if (!_windows.Contains(window))
        {
            _windows.Add(window);
        }
        if (isMain)
        {
            _main = window;
        }
    }

    public void Unregister(Window window) => _windows.Remove(window);

    public void BeginDrag(Control source, PointerPressedEventArgs e)
    {
        if (TopLevel.GetTopLevel(source) is not Window window)
        {
            return;
        }
        _source = source;
        _dragged = window;
        _startPointer = window.PointToScreen(e.GetPosition(window));
        _group = [(window, window.Position)];
        if (window == _main)
        {
            var others = Visible().Where(w => w != window).ToList();
            foreach (var index in SnapGeometry.DockedGroup(Bounds(window), others.Select(Bounds).ToList()))
            {
                _group.Add((others[index], others[index].Position));
            }
        }
        e.Pointer.Capture(source);
        source.PointerMoved += OnMoved;
        source.PointerReleased += OnReleased;
        source.PointerCaptureLost += OnCaptureLost;
        e.Handled = true;
    }

    /// <summary>The window's bounds in screen pixels.</summary>
    public static PixelRect Bounds(Window window) =>
        new(window.Position, PixelSize.FromSize(window.ClientSize, window.DesktopScaling));

    private IEnumerable<Window> Visible() => _windows.Where(w => w.IsVisible && w.WindowState == WindowState.Normal);

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (_dragged is null)
        {
            return;
        }
        var pointer = _dragged.PointToScreen(e.GetPosition(_dragged));
        var delta = pointer - _startPointer;
        var start = _group[0].Start;
        var size = Bounds(_dragged).Size;
        var proposed = new PixelRect(new PixelPoint(start.X + delta.X, start.Y + delta.Y), size);

        var inGroup = _group.Select(g => g.Window).ToHashSet();
        var targets = Visible().Where(w => !inGroup.Contains(w)).Select(Bounds);
        var screens = _dragged.Screens.All.Select(s => s.WorkingArea);
        var distance = (int)Math.Round(SnapDistance * _dragged.DesktopScaling);
        var snap = SnapGeometry.Snap(proposed, targets, screens, distance);

        foreach (var (window, origin) in _group)
        {
            window.Position = new PixelPoint(origin.X + delta.X + snap.X, origin.Y + delta.Y + snap.Y);
        }
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        e.Pointer.Capture(null);
        End();
    }

    private void OnCaptureLost(object? sender, PointerCaptureLostEventArgs e) => End();

    private void End()
    {
        if (_source is not null)
        {
            _source.PointerMoved -= OnMoved;
            _source.PointerReleased -= OnReleased;
            _source.PointerCaptureLost -= OnCaptureLost;
        }
        var wasDragging = _dragged is not null;
        _source = null;
        _dragged = null;
        _group = [];
        if (wasDragging)
        {
            DragCompleted?.Invoke(this, EventArgs.Empty);
        }
    }
}
