using Avalonia.Input;

namespace AudioTastic.App.Ui;

/// <summary>
/// The one place that maps keys to player actions (Feature Specification 2.4, Winamp defaults). Actions are the
/// names <see cref="PlayerShell.Execute"/> understands. Shortcuts for features that do not exist yet are listed so
/// they are reserved; the shell ignores actions it cannot perform.
/// </summary>
public static class KeyMap
{
    private static readonly Dictionary<(Key Key, KeyModifiers Modifiers), string> Map = new()
    {
        [(Key.Z, KeyModifiers.None)] = "previous",
        [(Key.X, KeyModifiers.None)] = "play",
        [(Key.C, KeyModifiers.None)] = "pause",
        [(Key.V, KeyModifiers.None)] = "stop",
        [(Key.B, KeyModifiers.None)] = "next",
        [(Key.L, KeyModifiers.None)] = "open",
        [(Key.J, KeyModifiers.None)] = "search",
        [(Key.S, KeyModifiers.None)] = "shuffle",
        [(Key.R, KeyModifiers.None)] = "repeat",
        [(Key.Up, KeyModifiers.None)] = "volumeUp",
        [(Key.Down, KeyModifiers.None)] = "volumeDown",
        [(Key.Left, KeyModifiers.None)] = "seekBack",
        [(Key.Right, KeyModifiers.None)] = "seekForward",
        [(Key.W, KeyModifiers.Control)] = "miniPlayer",
        [(Key.D, KeyModifiers.Control)] = "doubleSize",
        [(Key.P, KeyModifiers.Control)] = "settings",
        // Reserved for later phases: Q queue (PB-09), Shift+V stop after current (PB-09),
        // [ A-B repeat (PB-08), Ctrl+L open URL (FM-10), Alt+G/E/L windows (EQ, PL, AL), Ctrl+E tags, Alt+3 file info.
        [(Key.G, KeyModifiers.Alt)] = "equalizer",
        [(Key.E, KeyModifiers.Alt)] = "playlist",
    };

    /// <summary>The action for a key press, or null.</summary>
    public static string? ActionFor(Key key, KeyModifiers modifiers) =>
        Map.TryGetValue((key, modifiers), out var action) ? action : null;

    /// <summary>The gesture text for an action (for menus), or null.</summary>
    public static string? GestureFor(string action)
    {
        foreach (var ((key, modifiers), value) in Map)
        {
            if (value == action)
            {
                return new KeyGesture(key, modifiers).ToString();
            }
        }
        return null;
    }
}
