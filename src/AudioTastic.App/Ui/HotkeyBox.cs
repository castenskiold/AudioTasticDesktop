using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using AudioTastic.Platform;

namespace AudioTastic.App.Ui;

/// <summary>
/// SI-01: a field that records a hotkey: press the keys (e.g. Ctrl+Alt+Home) to set it, Backspace or Delete to clear
/// it. Tab and Escape keep their usual meaning. Needs Ctrl, Alt or Win with the key.
/// </summary>
public sealed class HotkeyBox : TextBox
{
    protected override Type StyleKeyOverride => typeof(TextBox);

    public HotkeyBox(string? hotkey)
    {
        IsReadOnly = true;
        Width = 200;
        PlaceholderText = "Press keys…";
        Text = hotkey ?? string.Empty;
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>The new hotkey text ("" when cleared), after each accepted key press.</summary>
    public event EventHandler<string>? HotkeyChanged;

    /// <summary>Raised with a short message when the keys pressed cannot be a hotkey.</summary>
    public event EventHandler<string>? Rejected;

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Tab or Key.Escape)
        {
            return;
        }
        e.Handled = true;
        if (e.Key is Key.Back or Key.Delete && e.KeyModifiers == KeyModifiers.None)
        {
            Text = string.Empty;
            HotkeyChanged?.Invoke(this, string.Empty);
            return;
        }
        if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin or Key.System)
        {
            return; // wait for the key itself
        }
        if (KeyName(e.Key) is not { } name)
        {
            Rejected?.Invoke(this, $"{e.Key} cannot be used in a hotkey.");
            return;
        }
        var modifiers = HotkeyModifiers.None;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            modifiers |= HotkeyModifiers.Control;
        }
        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt))
        {
            modifiers |= HotkeyModifiers.Alt;
        }
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            modifiers |= HotkeyModifiers.Shift;
        }
        if (e.KeyModifiers.HasFlag(KeyModifiers.Meta))
        {
            modifiers |= HotkeyModifiers.Windows;
        }
        if (Hotkey.Parse(new Hotkey(modifiers, name).ToString()) is not { } hotkey)
        {
            Rejected?.Invoke(this, "Use Ctrl, Alt or Win together with the key.");
            return;
        }
        Text = hotkey.ToString();
        HotkeyChanged?.Invoke(this, Text);
    }

    /// <summary>The OS-independent key name (<see cref="Hotkey.KeyNames"/>) for an Avalonia key, or null.</summary>
    public static string? KeyName(Key key) => key switch
    {
        >= Key.A and <= Key.Z => key.ToString(),
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        >= Key.F1 and <= Key.F24 => key.ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => "NumPad" + (char)('0' + (key - Key.NumPad0)),
        Key.Home => "Home",
        Key.End => "End",
        Key.PageUp => "PageUp",
        Key.PageDown => "PageDown",
        Key.Up => "Up",
        Key.Down => "Down",
        Key.Left => "Left",
        Key.Right => "Right",
        Key.Insert => "Insert",
        Key.Delete => "Delete",
        Key.Space => "Space",
        Key.Pause => "Pause",
        Key.Add => "Add",
        Key.Subtract => "Subtract",
        Key.Multiply => "Multiply",
        Key.Divide => "Divide",
        Key.OemPlus => "Plus",
        Key.OemMinus => "Minus",
        Key.OemComma => "Comma",
        Key.OemPeriod => "Period",
        _ => null,
    };
}
