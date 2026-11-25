using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Media;
using AudioTastic.App.Skins;

namespace AudioTastic.App.Ui.Controls;

/// <summary>
/// TL-04: what screen readers and keyboard users get from the custom-drawn skin controls: plain names per action
/// (the skin's tooltip becomes the help text), the shortcut key from <see cref="KeyMap"/>, roles and patterns through
/// the peers below, and a focus outline in the skin's <c>focus</c> colour (falling back to <c>text</c>, then white),
/// shown when a control is reached with Tab or the arrow keys.
/// </summary>
internal static class Accessible
{
    private static readonly Dictionary<string, string> Names = new(StringComparer.Ordinal)
    {
        ["menu"] = "Main menu",
        ["miniPlayer"] = "Mini player",
        ["expand"] = "Full player",
        ["minimize"] = "Minimise",
        ["close"] = "Close",
        ["hide"] = "Close window",
        ["previous"] = "Previous track",
        ["play"] = "Play",
        ["pause"] = "Pause",
        ["playPause"] = "Play or pause",
        ["stop"] = "Stop",
        ["next"] = "Next track",
        ["open"] = "Open files",
        ["volume"] = "Volume",
        ["seek"] = "Position",
        ["shuffle"] = "Shuffle",
        ["repeat"] = "Repeat",
        ["crossfade"] = "Crossfading",
        ["equalizer"] = "Equalizer window",
        ["playlist"] = "Playlist window",
        ["cast"] = "Cast to a device",
        ["mute"] = "Mute",
        ["plAdd"] = "Add files",
        ["plRemove"] = "Remove",
        ["plSelect"] = "Select",
        ["plSort"] = "Sort",
        ["plList"] = "Playlists",
        ["eqOn"] = "Equalizer on",
        ["eqAuto"] = "Automatic equalizer presets",
        ["eqParam"] = "Parametric editor",
        ["eqPresets"] = "Equalizer presets",
        ["eqDsp"] = "DSP chain",
    };

    /// <summary>Extra shortcut keys for actions that share one (play/pause is X or C).</summary>
    private static readonly Dictionary<string, string> Keys = new(StringComparer.Ordinal)
    {
        ["playPause"] = "X, C",
        ["expand"] = "Ctrl+W",
        ["hide"] = string.Empty,
        ["menu"] = "Alt+F",
    };

    /// <summary>The spoken name of a skin element: by its action, else its tooltip without the key hint, else its type.</summary>
    public static string NameFor(SkinElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (element.Bind is { } bind && Names.TryGetValue(bind, out var name))
        {
            return name;
        }
        if (element.Tooltip is { } tip)
        {
            var paren = tip.IndexOf(" (", StringComparison.Ordinal);
            return paren > 0 ? tip[..paren] : tip;
        }
        return element.Bind ?? element.Type;
    }

    /// <summary>Name, help text (the tooltip) and shortcut key of a skin control.</summary>
    public static void Describe(Control control, SkinElement element)
    {
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(element);
        AutomationProperties.SetName(control, NameFor(element));
        if (element.Tooltip is { } tip)
        {
            AutomationProperties.SetHelpText(control, tip);
        }
        var key = element.Bind is { } bind ? Keys.GetValueOrDefault(bind) ?? KeyMap.GestureFor(bind) : null;
        if (!string.IsNullOrEmpty(key))
        {
            AutomationProperties.SetAcceleratorKey(control, key);
        }
    }

    /// <summary>The focus outline for a skin control, in the skin's colours.</summary>
    public static ITemplate<Control> FocusOutline(Skin skin, double cornerRadius = 3)
    {
        ArgumentNullException.ThrowIfNull(skin);
        return FocusOutline(skin.Color("@focus", skin.Color("@text", Colors.White)), cornerRadius);
    }

    /// <summary>A focus outline in <paramref name="color"/>.</summary>
    public static ITemplate<Control> FocusOutline(Color color, double cornerRadius = 3) =>
        new FuncTemplate<Control>(() => new Border
        {
            BorderBrush = new SolidColorBrush(color),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(cornerRadius),
            Margin = new Thickness(-1.5),
            IsHitTestVisible = false,
        });

    /// <summary>Tells screen readers a pattern value changed (only when a reader has asked for the control).</summary>
    public static void Changed(Control control, AutomationProperty property, object? oldValue, object? newValue)
    {
        if (!Equals(oldValue, newValue) && ControlAutomationPeer.FromElement(control) is { } peer)
        {
            peer.RaisePropertyChangedEvent(property, oldValue, newValue);
        }
    }

    /// <summary>
    /// TL-04: a number box (NumericUpDown) carries its label on itself; its text box and up/down buttons get it too,
    /// so a screen reader says "Scroll speed, edit" and "Increase Scroll speed" instead of just "edit" and "button".
    /// Registered once for every NumericUpDown in the app.
    /// </summary>
    public static void NameNumberBoxes()
    {
        Avalonia.Controls.Primitives.TemplatedControl.TemplateAppliedEvent.AddClassHandler<NumericUpDown>((box, e) =>
        {
            var label = AutomationProperties.GetName(box);
            if (!string.IsNullOrEmpty(label) && e.NameScope.Find<Control>("PART_TextBox") is { } text)
            {
                AutomationProperties.SetName(text, label);
            }
        });
        // The up/down buttons sit in the number box's spinner, one template deeper.
        Avalonia.Controls.Primitives.TemplatedControl.TemplateAppliedEvent.AddClassHandler<ButtonSpinner>((spinner, e) =>
        {
            var label = spinner.TemplatedParent is Control parent ? AutomationProperties.GetName(parent) : null;
            if (string.IsNullOrEmpty(label))
            {
                return;
            }
            if (e.NameScope.Find<Control>("PART_IncreaseButton") is { } up)
            {
                AutomationProperties.SetName(up, "Increase " + label);
            }
            if (e.NameScope.Find<Control>("PART_DecreaseButton") is { } down)
            {
                AutomationProperties.SetName(down, "Decrease " + label);
            }
        });
    }

    public static string Percent(double fraction) => string.Create(CultureInfo.CurrentCulture, $"{Math.Round(fraction * 100)} %");
}

/// <summary>TL-04: a custom control that can be pressed (by a screen reader as well as the keyboard).</summary>
internal interface IAccessibleAction
{
    bool CanInvoke { get; }

    void InvokeAction();

    /// <summary>On/off for toggles, null for plain buttons.</summary>
    bool? IsOn { get; }
}

/// <summary>TL-04: a custom slider for screen readers (values in its own units).</summary>
internal interface IAccessibleRange
{
    double Minimum { get; }

    double Maximum { get; }

    double Value { get; }

    double SmallChange { get; }

    double LargeChange { get; }

    bool IsReadOnly { get; }

    /// <summary>The value as words ("1:23 of 5:59"), or null for the number.</summary>
    string? ValueText { get; }

    void SetValueFromAutomation(double value);
}

/// <summary>A button: Invoke pattern; its inner text and graphics are not read separately.</summary>
internal class ActionPeer(Control owner, IAccessibleAction action, AutomationControlType role = AutomationControlType.Button)
    : ControlAutomationPeer(owner), IInvokeProvider
{
    protected IAccessibleAction Action => action;

    protected override AutomationControlType GetAutomationControlTypeCore() => role;

    protected override bool IsContentElementCore() => !string.IsNullOrEmpty(GetName()); // an empty badge row is not read

    protected override bool IsControlElementCore() => !string.IsNullOrEmpty(GetName());

    protected override bool IsEnabledCore() => base.IsEnabledCore() && action.CanInvoke;

    protected override IReadOnlyList<AutomationPeer>? GetChildrenCore() => null;

    public void Invoke()
    {
        if (action.CanInvoke)
        {
            action.InvokeAction();
        }
    }
}

/// <summary>A toggle button: Toggle pattern (on/off).</summary>
internal sealed class TogglePeer(Control owner, IAccessibleAction action) : ActionPeer(owner, action), IToggleProvider
{
    public ToggleState ToggleState => Action.IsOn == true ? ToggleState.On : ToggleState.Off;

    public void Toggle() => Invoke();
}

/// <summary>A slider: RangeValue pattern, plus the value as words (Value pattern, read-only) when the control has them.</summary>
internal sealed class RangePeer(Control owner, IAccessibleRange range) : ControlAutomationPeer(owner), IRangeValueProvider, IValueProvider
{
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Slider;

    protected override bool IsContentElementCore() => true;

    protected override bool IsControlElementCore() => true;

    protected override IReadOnlyList<AutomationPeer>? GetChildrenCore() => null;

    public bool IsReadOnly => range.IsReadOnly;

    public double Minimum => range.Minimum;

    public double Maximum => range.Maximum;

    public double Value => range.Value;

    public double LargeChange => range.LargeChange;

    public double SmallChange => range.SmallChange;

    public void SetValue(double value)
    {
        if (!range.IsReadOnly)
        {
            range.SetValueFromAutomation(Math.Clamp(value, range.Minimum, range.Maximum));
        }
    }

    string? IValueProvider.Value => range.ValueText ?? range.Value.ToString("0.#", CultureInfo.CurrentCulture);

    bool IValueProvider.IsReadOnly => true;

    void IValueProvider.SetValue(string? value)
    {
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out var v))
        {
            SetValue(v);
        }
    }
}

/// <summary>Read-only text drawn by the control itself (title, badges, times): its name is the text.</summary>
internal sealed class TextPeer(Control owner, AutomationControlType role = AutomationControlType.Text) : ControlAutomationPeer(owner)
{
    protected override AutomationControlType GetAutomationControlTypeCore() => role;

    protected override bool IsContentElementCore() => !string.IsNullOrEmpty(GetName()); // an empty badge row is not read

    protected override bool IsControlElementCore() => !string.IsNullOrEmpty(GetName());

    protected override IReadOnlyList<AutomationPeer>? GetChildrenCore() => null;
}

/// <summary>A focusable, pressable border (playlist tabs): Enter or Space presses it.</summary>
internal sealed class AccessibleBorder : Border, IAccessibleAction
{
    private readonly Action _invoke;
    private readonly AutomationControlType _role;
    private readonly Func<bool>? _selected;

    public AccessibleBorder(string name, Action invoke, AutomationControlType role = AutomationControlType.Button, Func<bool>? selected = null)
    {
        _invoke = invoke;
        _role = role;
        _selected = selected;
        Focusable = true;
        AutomationProperties.SetName(this, name);
    }

    public bool CanInvoke => true;

    public bool? IsOn => _selected?.Invoke();

    public void InvokeAction() => _invoke();

    protected override AutomationPeer OnCreateAutomationPeer() => new ActionPeer(this, this, _role);

    protected override void OnKeyDown(Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key is Avalonia.Input.Key.Enter or Avalonia.Input.Key.Space && e.KeyModifiers == Avalonia.Input.KeyModifiers.None)
        {
            e.Handled = true;
            _invoke();
        }
        base.OnKeyDown(e);
    }
}
