namespace AudioTastic.Platform;

/// <summary>SI-01: modifier keys of a global hotkey.</summary>
[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Windows = 8,
}

/// <summary>
/// SI-01: a global hotkey such as "Ctrl+Alt+Home". <see cref="Key"/> is one of <see cref="KeyNames"/>, the
/// OS-independent key names; the OS implementation maps them to its own key codes.
/// </summary>
/// <param name="Repeat">True when holding the keys repeats the action (volume, seeking).</param>
public sealed record Hotkey(HotkeyModifiers Modifiers, string Key, bool Repeat = false)
{
    /// <summary>Every key a hotkey may use.</summary>
    public static IReadOnlyList<string> KeyNames { get; } =
    [
        .. Enumerable.Range('A', 26).Select(c => ((char)c).ToString()),
        .. Enumerable.Range(0, 10).Select(d => d.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        .. Enumerable.Range(1, 24).Select(n => "F" + n.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        "Home", "End", "PageUp", "PageDown", "Up", "Down", "Left", "Right", "Insert", "Delete", "Space", "Pause",
        .. Enumerable.Range(0, 10).Select(d => "NumPad" + d.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        "Add", "Subtract", "Multiply", "Divide", "Plus", "Minus", "Comma", "Period",
    ];

    /// <summary>"Ctrl+Alt+Home" style text: Ctrl, Alt, Shift, Win in that order, then the key.</summary>
    public override string ToString()
    {
        var parts = new List<string>(5);
        if (Modifiers.HasFlag(HotkeyModifiers.Control))
        {
            parts.Add("Ctrl");
        }
        if (Modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            parts.Add("Alt");
        }
        if (Modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            parts.Add("Shift");
        }
        if (Modifiers.HasFlag(HotkeyModifiers.Windows))
        {
            parts.Add("Win");
        }
        parts.Add(Key);
        return string.Join('+', parts);
    }

    /// <summary>
    /// Parses "Ctrl+Alt+Home" (case-insensitive, any modifier order). Returns null for an unknown key, a missing key,
    /// or a hotkey without Ctrl, Alt or Win: Shift alone would take over normal typing.
    /// </summary>
    public static Hotkey? Parse(string? text, bool repeat = false)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        var modifiers = HotkeyModifiers.None;
        string? key = null;
        foreach (var raw in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (raw.ToUpperInvariant())
            {
                case "CTRL" or "CONTROL":
                    modifiers |= HotkeyModifiers.Control;
                    break;
                case "ALT":
                    modifiers |= HotkeyModifiers.Alt;
                    break;
                case "SHIFT":
                    modifiers |= HotkeyModifiers.Shift;
                    break;
                case "WIN" or "WINDOWS" or "META" or "CMD":
                    modifiers |= HotkeyModifiers.Windows;
                    break;
                default:
                    if (key is not null)
                    {
                        return null; // two keys
                    }
                    key = KeyNames.FirstOrDefault(k => k.Equals(raw, StringComparison.OrdinalIgnoreCase));
                    if (key is null)
                    {
                        return null;
                    }
                    break;
            }
        }
        if (key is null || (modifiers & (HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Windows)) == 0)
        {
            return null;
        }
        return new Hotkey(modifiers, key, repeat);
    }
}

/// <summary>
/// SI-01: system-wide hotkeys that work while another program has the focus. The OS tells AudioTastic only about the
/// registered combinations (on Windows through RegisterHotKey), and refuses one that another program already uses.
/// </summary>
public interface IHotkeyService : IDisposable
{
    /// <summary>True when this OS build supports global hotkeys (Windows so far).</summary>
    bool IsSupported { get; }

    /// <summary>A registered hotkey was pressed; the argument is its id. Raised on a background thread.</summary>
    event EventHandler<string>? Pressed;

    /// <summary>
    /// Replaces every registered hotkey with <paramref name="hotkeys"/> (id → hotkey; empty unregisters all).
    /// Returns the ids that could not be registered (taken by another program, or an unknown key).
    /// </summary>
    IReadOnlyList<string> Register(IReadOnlyDictionary<string, Hotkey> hotkeys);
}

/// <summary>Global hotkeys for systems that have none yet (macOS and Linux until they get their own).</summary>
public sealed class NoHotkeys : IHotkeyService
{
    public static NoHotkeys Instance { get; } = new();

    public bool IsSupported => false;

    public event EventHandler<string>? Pressed
    {
        add { }
        remove { }
    }

    public IReadOnlyList<string> Register(IReadOnlyDictionary<string, Hotkey> hotkeys) => [.. hotkeys.Keys];

    public void Dispose()
    {
    }
}
