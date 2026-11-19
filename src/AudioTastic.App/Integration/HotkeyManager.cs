using AudioTastic.Data.Settings;
using AudioTastic.Platform;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;

namespace AudioTastic.App.Integration;

/// <summary>
/// SI-01: registers the global hotkeys from Settings › System integration (on/off and one hotkey per action) and runs
/// the action on the UI thread when one is pressed. Remembers which hotkeys could not be registered (another program
/// has them), for the settings page.
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    /// <summary>The actions a hotkey can run (PlayerShell action names) with their labels, in settings order.</summary>
    public static IReadOnlyList<(string Action, string Label)> Actions { get; } =
    [
        ("playPause", "Play / Pause"),
        ("play", "Play"),
        ("pause", "Pause"),
        ("stop", "Stop"),
        ("previous", "Previous track"),
        ("next", "Next track"),
        ("volumeUp", "Volume up"),
        ("volumeDown", "Volume down"),
        ("mute", "Mute"),
        ("seekBack", "Back 5 seconds"),
        ("seekForward", "Forward 5 seconds"),
        ("showHide", "Show / hide AudioTastic"),
    ];

    /// <summary>Actions that repeat while the keys are held.</summary>
    private static readonly HashSet<string> Repeating = ["volumeUp", "volumeDown", "seekBack", "seekForward"];

    private readonly IHotkeyService _hotkeys;
    private readonly SettingsStore _settings;
    private readonly ILogger<HotkeyManager> _logger;
    private Action<string>? _run;
    private bool _suspended;

    public HotkeyManager(IPlatformServices platform, SettingsStore settings, ILogger<HotkeyManager> logger)
    {
        _hotkeys = platform.Hotkeys;
        _settings = settings;
        _logger = logger;
    }

    public bool IsSupported => _hotkeys.IsSupported;

    /// <summary>Actions whose hotkey is invalid or taken by another program.</summary>
    public IReadOnlyCollection<string> Failed { get; private set; } = [];

    /// <summary>Raised (UI thread) after the hotkeys were registered again.</summary>
    public event EventHandler? Changed;

    /// <summary>Starts listening; <paramref name="run"/> gets the action name on the UI thread.</summary>
    public void Start(Action<string> run)
    {
        if (!_hotkeys.IsSupported || _run is not null)
        {
            return;
        }
        _run = run;
        _hotkeys.Pressed += OnPressed;
        _settings.Changed += OnSettingsChanged;
        Apply();
    }

    /// <summary>
    /// While a hotkey field in the settings has the focus, the global hotkeys are off, so pressing a combination that
    /// is already registered reaches the field instead of running the action.
    /// </summary>
    public void Suspend(bool suspended)
    {
        if (_suspended != suspended)
        {
            _suspended = suspended;
            Apply();
        }
    }

    /// <summary>The hotkeys to register for <paramref name="settings"/> (action → hotkey) and the actions with invalid text.</summary>
    public static (Dictionary<string, Hotkey> Hotkeys, List<string> Invalid) Plan(SystemIntegrationSettings settings)
    {
        var hotkeys = new Dictionary<string, Hotkey>(StringComparer.Ordinal);
        var invalid = new List<string>();
        if (!settings.GlobalHotkeys)
        {
            return (hotkeys, invalid);
        }
        foreach (var (action, _) in Actions)
        {
            if (!settings.Hotkeys.TryGetValue(action, out var text) || string.IsNullOrWhiteSpace(text))
            {
                continue;
            }
            if (Hotkey.Parse(text, Repeating.Contains(action)) is { } hotkey)
            {
                hotkeys[action] = hotkey;
            }
            else
            {
                invalid.Add(action);
            }
        }
        return (hotkeys, invalid);
    }

    private void Apply()
    {
        var (hotkeys, invalid) = Plan(_settings.Current.SystemIntegration);
        var failed = _hotkeys.Register(_suspended ? new Dictionary<string, Hotkey>() : hotkeys);
        if (!_suspended)
        {
            Failed = [.. invalid, .. failed];
            foreach (var action in failed)
            {
                _logger.LogWarning("Global hotkey {Hotkey} for {Action} is in use by another program", hotkeys[action], action);
            }
            if (hotkeys.Count > 0)
            {
                _logger.LogInformation("Global hotkeys: {Count} registered", hotkeys.Count - failed.Count);
            }
        }
        Dispatcher.UIThread.Post(() => Changed?.Invoke(this, EventArgs.Empty));
    }

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        if (e.Group == SettingsGroup.SystemIntegration)
        {
            Apply();
        }
    }

    private void OnPressed(object? sender, string action)
    {
        _logger.LogInformation("Global hotkey: {Action}", action);
        Dispatcher.UIThread.Post(() => _run?.Invoke(action));
    }

    public void Dispose()
    {
        if (_run is null)
        {
            return;
        }
        _hotkeys.Pressed -= OnPressed;
        _settings.Changed -= OnSettingsChanged;
        _hotkeys.Dispose(); // unregisters everything
    }
}
