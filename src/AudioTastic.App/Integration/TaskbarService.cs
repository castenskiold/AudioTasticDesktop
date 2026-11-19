using System.Runtime.InteropServices;
using AudioTastic.Core.Playback;
using AudioTastic.Data.Settings;
using AudioTastic.Platform;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;

namespace AudioTastic.App.Integration;

/// <summary>
/// SI-03 [Windows only]: previous / play-pause / next buttons under the taskbar preview of the player windows, and the
/// playback progress on their taskbar button (green while playing, yellow when paused; Settings â€º System
/// integration). The glyphs are drawn here, dark or light to suit the Windows theme.
/// </summary>
public sealed class TaskbarService : IDisposable
{
    private readonly ITaskbar _taskbar;
    private readonly IPlayerController _player;
    private readonly SettingsStore _settings;
    private readonly ILogger<TaskbarService> _logger;
    private Action<string>? _run;
    private PlaybackState _state;
    private TimeSpan _position;
    private TimeSpan? _length;
    private string? _lastLoggedFailure;

    public TaskbarService(IPlatformServices platform, IPlayerController player, SettingsStore settings, ILogger<TaskbarService> logger)
    {
        _taskbar = platform.Taskbar;
        _player = player;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>Hooks <paramref name="windows"/> (before they are shown) and starts following the player.</summary>
    public void Start(IReadOnlyList<Window> windows, Action<string> run)
    {
        if (!_taskbar.IsSupported || _run is not null || windows.Count == 0)
        {
            return;
        }
        _run = run;
        try
        {
            var images = DrawImages((int)Math.Round(16 * windows[0].DesktopScaling), DarkGlyphs());
            foreach (var window in windows)
            {
                Win32Properties.AddWndProcHookCallback(window, OnMessage);
                var handle = window.TryGetPlatformHandle()?.Handle ?? 0;
                _taskbar.Attach(handle, images);
                _logger.LogDebug("Taskbar: window {Handle:X} attached ({Failure})", handle, _taskbar.LastFailure ?? "buttons added");
            }
        }
#pragma warning disable CA1031 // The taskbar buttons are optional: never stop the player over them.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogWarning(ex, "Taskbar buttons are not available");
            return;
        }
        _taskbar.ButtonClicked += OnButtonClicked;
        _player.StateChanged += OnStateChanged;
        _player.PositionChanged += OnPositionChanged;
        _settings.Changed += OnSettingsChanged;
        _state = _player.State;
        Update();
    }

    /// <summary>The progress shown for a state, position and length (none while stopped, for streams, or when switched off).</summary>
    public static (TaskbarProgress State, double Fraction) ProgressFor(PlaybackState state, TimeSpan position, TimeSpan? length, bool enabled)
    {
        if (!enabled || length is not { } total || total <= TimeSpan.Zero)
        {
            return (TaskbarProgress.None, 0);
        }
        var fraction = Math.Clamp(position / total, 0, 1);
        return state switch
        {
            PlaybackState.Playing or PlaybackState.Buffering => (TaskbarProgress.Normal, fraction),
            PlaybackState.Paused => (TaskbarProgress.Paused, fraction),
            _ => (TaskbarProgress.None, 0),
        };
    }

    private nint OnMessage(nint window, uint message, nint wParam, nint lParam, ref bool handled)
    {
        if (_taskbar.HandleMessage(window, message, wParam, lParam))
        {
            handled = true;
        }
        return 0;
    }

    private void OnButtonClicked(object? sender, TaskbarButton button) => _run?.Invoke(button switch
    {
        TaskbarButton.Previous => "previous",
        TaskbarButton.Next => "next",
        _ => "playPause",
    });

    private void OnStateChanged(object? sender, PlaybackStateEventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        _state = e.State;
        Update();
    });

    private void OnPositionChanged(object? sender, PlaybackPositionEventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        _position = e.Position;
        _length = e.Duration;
        Update(); // the taskbar ignores unchanged values (1/1000 steps)
    });

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        if (e.Group == SettingsGroup.SystemIntegration)
        {
            Dispatcher.UIThread.Post(Update);
        }
    }

    private void Update()
    {
        _taskbar.SetPlaying(_state is PlaybackState.Playing or PlaybackState.Buffering);
        LogFailure();
        var (state, fraction) = ProgressFor(_state, _position, _length, _settings.Current.SystemIntegration.TaskbarProgress);
        _taskbar.SetProgress(state, fraction);
        LogFailure();
    }

    private void LogFailure()
    {
        if (_taskbar.LastFailure is { } failure && failure != _lastLoggedFailure)
        {
            _lastLoggedFailure = failure;
            _logger.LogWarning("Taskbar call failed: {Failure}", failure);
        }
    }

    /// <summary>Dark glyphs when Windows uses its light theme (the preview flyout is light then).</summary>
    private static bool DarkGlyphs() =>
        Application.Current?.PlatformSettings?.GetColorValues().ThemeVariant == PlatformThemeVariant.Light;

    /// <summary>Previous, play, pause and next glyphs, <paramref name="size"/> pixels square.</summary>
    public static TaskbarImages DrawImages(int size, bool dark)
    {
        var brush = new SolidColorBrush(dark ? Color.FromRgb(0x20, 0x20, 0x20) : Colors.White);
        TaskbarImage Draw(Action<DrawingContext> draw)
        {
            using var bitmap = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
            using (var context = bitmap.CreateDrawingContext())
            {
                using (context.PushTransform(Matrix.CreateScale(size / 16.0, size / 16.0)))
                {
                    draw(context);
                }
            }
            return new TaskbarImage(size, size, StraightPixels(bitmap, size));
        }
        void Triangle(DrawingContext c, Point a, Point b, Point d)
        {
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                g.BeginFigure(a, true);
                g.LineTo(b);
                g.LineTo(d);
                g.EndFigure(true);
            }
            c.DrawGeometry(brush, null, geometry);
        }
        return new TaskbarImages(
            Previous: Draw(c =>
            {
                c.FillRectangle(brush, new Rect(3, 3, 2, 10));
                Triangle(c, new Point(13, 3), new Point(13, 13), new Point(5.5, 8));
            }),
            Play: Draw(c => Triangle(c, new Point(4.5, 2.5), new Point(4.5, 13.5), new Point(13.5, 8))),
            Pause: Draw(c =>
            {
                c.FillRectangle(brush, new Rect(4, 3, 3, 10));
                c.FillRectangle(brush, new Rect(9, 3, 3, 10));
            }),
            Next: Draw(c =>
            {
                Triangle(c, new Point(3, 3), new Point(3, 13), new Point(10.5, 8));
                c.FillRectangle(brush, new Rect(11, 3, 2, 10));
            }));
    }

    /// <summary>The bitmap's pixels as straight BGRA (Avalonia renders premultiplied; icons want straight alpha).</summary>
    private static byte[] StraightPixels(RenderTargetBitmap bitmap, int size)
    {
        var stride = size * 4;
        var pixels = new byte[stride * size];
        var buffer = Marshal.AllocHGlobal(pixels.Length);
        try
        {
            bitmap.CopyPixels(new PixelRect(0, 0, size, size), buffer, pixels.Length, stride);
            Marshal.Copy(buffer, pixels, 0, pixels.Length);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var alpha = pixels[i + 3];
            if (alpha is > 0 and < 255)
            {
                pixels[i] = (byte)Math.Min(255, pixels[i] * 255 / alpha);
                pixels[i + 1] = (byte)Math.Min(255, pixels[i + 1] * 255 / alpha);
                pixels[i + 2] = (byte)Math.Min(255, pixels[i + 2] * 255 / alpha);
            }
        }
        return pixels;
    }

    public void Dispose()
    {
        if (_run is null)
        {
            return;
        }
        _taskbar.ButtonClicked -= OnButtonClicked;
        _player.StateChanged -= OnStateChanged;
        _player.PositionChanged -= OnPositionChanged;
        _settings.Changed -= OnSettingsChanged;
        _taskbar.Dispose();
    }
}
