using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;

namespace AudioTastic.Platform.Windows;

/// <summary>
/// SI-03 on Windows: ITaskbarList3/4 thumbnail toolbar (previous, play/pause, next) and progress on the taskbar button.
/// The buttons can only be added once the taskbar has created the window's button ("TaskbarButtonCreated", which
/// also comes again after Explorer restarts). Clicks arrive as WM_COMMAND with THBN_CLICKED.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsTaskbar : ITaskbar
{
    private const uint WmCommand = 0x0111;
    private const int ThbnClicked = 0x1800;
    private const uint ThbIcon = 0x2;
    private const uint ThbTooltip = 0x4;
    private const uint ThbFlags = 0x8;
    private const int TbpfNoProgress = 0;
    private const int TbpfNormal = 0x2;
    private const int TbpfPaused = 0x8;
    private const ulong ProgressScale = 1000;

    private static readonly Guid TaskbarListClass = new("56FDF344-FD6D-11d0-958A-006097C9A090");

    private readonly uint _buttonCreatedMessage = RegisterWindowMessageW("TaskbarButtonCreated");
    private readonly List<nint> _windows = [];
    private readonly HashSet<nint> _withButtons = [];
    private ITaskbarList3? _taskbar;
    private nint _previous, _play, _pause, _next;
    private bool _playing;
    private (int Flags, ulong Value) _progress = (TbpfNoProgress, 0);

    public bool IsSupported => true;

    public event EventHandler<TaskbarButton>? ButtonClicked;

    public void Attach(nint windowHandle, TaskbarImages images)
    {
        if (windowHandle == 0 || _windows.Contains(windowHandle))
        {
            return;
        }
        if (_previous == 0)
        {
            _previous = Icon(images.Previous);
            _play = Icon(images.Play);
            _pause = Icon(images.Pause);
            _next = Icon(images.Next);
        }
        _windows.Add(windowHandle);
        AddButtons(windowHandle); // fails quietly until the taskbar button exists; TaskbarButtonCreated retries
    }

    public bool HandleMessage(nint windowHandle, uint message, nint wParam, nint lParam)
    {
        if (message == _buttonCreatedMessage && _windows.Contains(windowHandle))
        {
            _withButtons.Remove(windowHandle); // a new taskbar button (e.g. Explorer restarted) needs them again
            AddButtons(windowHandle);
            return false;
        }
        if (message == WmCommand && (((long)wParam >> 16) & 0xFFFF) == ThbnClicked && _windows.Contains(windowHandle))
        {
            var id = (int)((long)wParam & 0xFFFF);
            if (id is >= 0 and <= 2)
            {
                ButtonClicked?.Invoke(this, (TaskbarButton)id);
                return true;
            }
        }
        return false;
    }

    public void SetPlaying(bool playing)
    {
        if (_playing == playing)
        {
            return;
        }
        _playing = playing;
        foreach (var window in _withButtons)
        {
            var buttons = Buttons();
            Try(() => Taskbar().ThumbBarUpdateButtons(window, (uint)buttons.Length, buttons));
        }
    }

    public void SetProgress(TaskbarProgress state, double fraction)
    {
        var flags = state switch
        {
            TaskbarProgress.Normal => TbpfNormal,
            TaskbarProgress.Paused => TbpfPaused,
            _ => TbpfNoProgress,
        };
        var value = (ulong)Math.Round(Math.Clamp(fraction, 0, 1) * ProgressScale);
        if (_progress == (flags, value))
        {
            return;
        }
        var stateChanged = _progress.Flags != flags;
        _progress = (flags, value);
        foreach (var window in _windows)
        {
            Try(() =>
            {
                if (stateChanged)
                {
                    Taskbar().SetProgressState(window, flags);
                }
                if (flags != TbpfNoProgress)
                {
                    Taskbar().SetProgressValue(window, value, ProgressScale);
                }
            });
        }
    }

    private void AddButtons(nint window)
    {
        if (_withButtons.Contains(window))
        {
            return;
        }
        var buttons = Buttons();
        // Adding works once per taskbar button; when an earlier add already took, update them instead.
        if (Try(() => Taskbar().ThumbBarAddButtons(window, (uint)buttons.Length, buttons))
            || Try(() => Taskbar().ThumbBarUpdateButtons(window, (uint)buttons.Length, buttons)))
        {
            _withButtons.Add(window);
            Try(() =>
            {
                Taskbar().SetProgressState(window, _progress.Flags);
                if (_progress.Flags != TbpfNoProgress)
                {
                    Taskbar().SetProgressValue(window, _progress.Value, ProgressScale);
                }
            });
        }
    }

    private ThumbButton[] Buttons() =>
    [
        Button(TaskbarButton.Previous, _previous, "Previous"),
        Button(TaskbarButton.PlayPause, _playing ? _pause : _play, _playing ? "Pause" : "Play"),
        Button(TaskbarButton.Next, _next, "Next"),
    ];

    private static ThumbButton Button(TaskbarButton id, nint icon, string tip)
    {
        var button = new ThumbButton { Mask = ThbIcon | ThbTooltip | ThbFlags, Id = (uint)id, Icon = icon, Flags = 0 };
        tip.AsSpan().CopyTo(button.Tip);
        return button;
    }

    /// <summary>The shell's taskbar object; throws (so the caller reports it) when Windows does not provide it.</summary>
    private ITaskbarList3 Taskbar()
    {
        if (_taskbar is null)
        {
            var classId = TaskbarListClass;
            var interfaceId = typeof(ITaskbarList3).GUID;
            var result = CoCreateInstance(in classId, 0, 1, in interfaceId, out var pointer);
            if (result < 0)
            {
                throw new InvalidOperationException($"TaskbarList is not available (HRESULT 0x{result:X8}, apartment {Thread.CurrentThread.GetApartmentState()})");
            }
            try
            {
                var taskbar = (ITaskbarList3)new StrategyBasedComWrappers().GetOrCreateObjectForComInstance(pointer, CreateObjectFlags.None);
                taskbar.HrInit();
                _taskbar = taskbar;
            }
            finally
            {
                Marshal.Release(pointer);
            }
        }
        return _taskbar;
    }

    /// <summary>The last failed taskbar call, for the log (null when the last call worked).</summary>
    public string? LastFailure { get; private set; }

    /// <summary>Runs a taskbar call; false when the shell refused it (no taskbar button yet, Explorer restarting).</summary>
    private bool Try(Action call)
    {
        try
        {
            call();
            LastFailure = null;
            return true;
        }
#pragma warning disable CA1031 // Reported through LastFailure; the taskbar is optional.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LastFailure = $"{ex.GetType().Name} 0x{ex.HResult:X8}: {ex.Message}";
            return false;
        }
    }

    /// <summary>An HICON from straight BGRA pixels (32-bit colour with alpha, an empty mask).</summary>
    private static unsafe nint Icon(TaskbarImage image)
    {
        fixed (byte* pixels = image.Bgra)
        {
            var color = CreateBitmap(image.Width, image.Height, 1, 32, pixels);
            var mask = CreateBitmap(image.Width, image.Height, 1, 1, null);
            try
            {
                var info = new IconInfo { IsIcon = 1, Mask = mask, Color = color };
                return CreateIconIndirect(in info);
            }
            finally
            {
                DeleteObject(color);
                DeleteObject(mask);
            }
        }
    }

    public void Dispose()
    {
        foreach (var icon in new[] { _previous, _play, _pause, _next })
        {
            if (icon != 0)
            {
                DestroyIcon(icon);
            }
        }
        _previous = _play = _pause = _next = 0;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct ThumbButton
    {
        public uint Mask;
        public uint Id;
        public uint Bitmap;
        public nint Icon;
        public TipBuffer Tip;
        public uint Flags;
    }

    [System.Runtime.CompilerServices.InlineArray(260)]
    internal struct TipBuffer
    {
        private char _first;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        public int IsIcon;
        public uint HotspotX;
        public uint HotspotY;
        public nint Mask;
        public nint Color;
    }

    /// <summary>
    /// ITaskbarList4, which extends ITaskbarList3 (same slots, plus SetTabProperties). Asked for by the ITaskbarList4 IID
    /// because Windows 11 26H2 (build 26300) answers E_NOINTERFACE for ITaskbarList3 while ITaskbarList4 works.
    /// </summary>
    [GeneratedComInterface]
    [Guid("C43DC798-95D1-4BEA-9030-BB99E2983A1A")]
    internal partial interface ITaskbarList3
    {
        void HrInit();
        void AddTab(nint window);
        void DeleteTab(nint window);
        void ActivateTab(nint window);
        void SetActiveAlt(nint window);
        void MarkFullscreenWindow(nint window, int fullscreen);
        void SetProgressValue(nint window, ulong completed, ulong total);
        void SetProgressState(nint window, int flags);
        void RegisterTab(nint tab, nint mdi);
        void UnregisterTab(nint tab);
        void SetTabOrder(nint tab, nint insertBefore);
        void SetTabActive(nint tab, nint mdi, uint reserved);
        void ThumbBarAddButtons(nint window, uint count, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] ThumbButton[] buttons);
        void ThumbBarUpdateButtons(nint window, uint count, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] ThumbButton[] buttons);
        void ThumbBarSetImageList(nint window, nint imageList);
        void SetOverlayIcon(nint window, nint icon, [MarshalAs(UnmanagedType.LPWStr)] string? description);
        void SetThumbnailTooltip(nint window, [MarshalAs(UnmanagedType.LPWStr)] string? tip);
        void SetThumbnailClip(nint window, nint clip);
        void SetTabProperties(nint tab, uint flags);
    }

    [LibraryImport("ole32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int CoCreateInstance(in Guid classId, nint outer, uint context, in Guid interfaceId, out nint instance);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint RegisterWindowMessageW(string name);

    [LibraryImport("gdi32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static unsafe partial nint CreateBitmap(int width, int height, uint planes, uint bitsPerPixel, void* bits);

    [LibraryImport("gdi32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(nint handle);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint CreateIconIndirect(in IconInfo info);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(nint icon);
}
