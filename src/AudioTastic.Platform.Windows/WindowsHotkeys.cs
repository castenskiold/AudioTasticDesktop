using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace AudioTastic.Platform.Windows;

/// <summary>
/// SI-01 on Windows: global hotkeys through RegisterHotKey. A background thread owns the registrations and runs a
/// message loop: Windows posts WM_HOTKEY to that thread's queue (no window needed). Unlike a low-level keyboard hook,
/// Windows only tells us about the registered combinations, and refuses one another program already registered.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsHotkeys : IHotkeyService
{
    private const uint WmHotkey = 0x0312;
    private const uint WmApp = 0x8000;
    private const uint WmQuit = 0x0012;
    private const uint ModAlt = 0x1;
    private const uint ModControl = 0x2;
    private const uint ModShift = 0x4;
    private const uint ModWin = 0x8;
    private const uint ModNoRepeat = 0x4000;
    private const uint PmNoRemove = 0;

    private readonly ConcurrentQueue<Action> _work = new();
    private readonly Dictionary<int, string> _registered = []; // only touched on the loop thread
    private readonly ManualResetEventSlim _ready = new();
    private Thread? _thread;
    private uint _threadId;
    private int _nextId = 1;

    public bool IsSupported => true;

    public event EventHandler<string>? Pressed;

    public IReadOnlyList<string> Register(IReadOnlyDictionary<string, Hotkey> hotkeys)
    {
        EnsureThread();
        var done = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _work.Enqueue(() => done.SetResult(RegisterOnLoop(hotkeys)));
        PostThreadMessageW(_threadId, WmApp, 0, 0);
        return done.Task.Wait(TimeSpan.FromSeconds(5)) ? done.Task.Result : [.. hotkeys.Keys];
    }

    /// <summary>The Windows virtual-key code for one of <see cref="Hotkey.KeyNames"/>, or null.</summary>
    public static int? VirtualKey(string name) => name switch
    {
        { Length: 1 } when name[0] is >= 'A' and <= 'Z' => name[0],
        { Length: 1 } when name[0] is >= '0' and <= '9' => name[0],
        ['F', .. var n] when int.TryParse(n, out var f) && f is >= 1 and <= 24 => 0x70 + f - 1,
        ['N', 'u', 'm', 'P', 'a', 'd', .. var d] when int.TryParse(d, out var p) && p is >= 0 and <= 9 => 0x60 + p,
        "Home" => 0x24,
        "End" => 0x23,
        "PageUp" => 0x21,
        "PageDown" => 0x22,
        "Up" => 0x26,
        "Down" => 0x28,
        "Left" => 0x25,
        "Right" => 0x27,
        "Insert" => 0x2D,
        "Delete" => 0x2E,
        "Space" => 0x20,
        "Pause" => 0x13,
        "Add" => 0x6B,
        "Subtract" => 0x6D,
        "Multiply" => 0x6A,
        "Divide" => 0x6F,
        "Plus" => 0xBB,
        "Minus" => 0xBD,
        "Comma" => 0xBC,
        "Period" => 0xBE,
        _ => null,
    };

    private List<string> RegisterOnLoop(IReadOnlyDictionary<string, Hotkey> hotkeys)
    {
        foreach (var id in _registered.Keys)
        {
            UnregisterHotKey(0, id);
        }
        _registered.Clear();
        var failed = new List<string>();
        foreach (var (name, hotkey) in hotkeys)
        {
            if (VirtualKey(hotkey.Key) is not { } vk)
            {
                failed.Add(name);
                continue;
            }
            var modifiers = (hotkey.Repeat ? 0 : ModNoRepeat)
                | (hotkey.Modifiers.HasFlag(HotkeyModifiers.Alt) ? ModAlt : 0)
                | (hotkey.Modifiers.HasFlag(HotkeyModifiers.Control) ? ModControl : 0)
                | (hotkey.Modifiers.HasFlag(HotkeyModifiers.Shift) ? ModShift : 0)
                | (hotkey.Modifiers.HasFlag(HotkeyModifiers.Windows) ? ModWin : 0);
            var id = _nextId++;
            if (RegisterHotKey(0, id, modifiers, (uint)vk))
            {
                _registered[id] = name;
            }
            else
            {
                failed.Add(name); // ERROR_HOTKEY_ALREADY_REGISTERED: another program has it
            }
        }
        return failed;
    }

    private void EnsureThread()
    {
        if (_thread is not null)
        {
            return;
        }
        _thread = new Thread(Loop) { IsBackground = true, Name = "AudioTastic hotkeys" };
        _thread.Start();
        _ready.Wait();
    }

    private void Loop()
    {
        _threadId = GetCurrentThreadId();
        PeekMessageW(out _, 0, WmApp, WmApp, PmNoRemove); // creates this thread's message queue
        _ready.Set();
        while (GetMessageW(out var message, 0, 0, 0) > 0)
        {
            if (message.Message == WmHotkey && _registered.TryGetValue((int)message.WParam, out var name))
            {
                Pressed?.Invoke(this, name);
            }
            while (_work.TryDequeue(out var work))
            {
                work();
            }
        }
        foreach (var id in _registered.Keys)
        {
            UnregisterHotKey(0, id);
        }
        _registered.Clear();
    }

    public void Dispose()
    {
        if (_thread is null)
        {
            return;
        }
        PostThreadMessageW(_threadId, WmQuit, 0, 0);
        _thread.Join(TimeSpan.FromSeconds(2));
        _thread = null;
        _ready.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public nint Window;
        public uint Message;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public int X;
        public int Y;
        public uint Private;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(nint window, int id, uint modifiers, uint virtualKey);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(nint window, int id);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int GetMessageW(out Msg message, nint window, uint filterMin, uint filterMax);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PeekMessageW(out Msg message, nint window, uint filterMin, uint filterMax, uint remove);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostThreadMessageW(uint threadId, uint message, nuint wParam, nint lParam);

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint GetCurrentThreadId();
}
