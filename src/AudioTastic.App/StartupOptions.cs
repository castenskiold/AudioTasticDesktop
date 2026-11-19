using System.Globalization;
using System.CommandLine;

namespace AudioTastic.App;

/// <summary>SI-06 / SI-08: what to do with files given on the command line.</summary>
public enum FileAction
{
    /// <summary>Files opened from the OS (double-click): Settings › System integration decides (play or enqueue).</summary>
    Default,

    /// <summary>--open: add the files and play the first ("Open in AudioTastic").</summary>
    Open,

    /// <summary>--enqueue: only add the files ("Enqueue in AudioTastic").</summary>
    Enqueue,
}

/// <summary>SI-08: a player command from the command line: play, pause, playPause, stop, next, previous, volume, seek, quit.</summary>
public sealed record ControlCommand(string Name, double? Value = null);

/// <summary>
/// SI-08: the parsed command line. Files plus --open/--enqueue, player commands (--play, --next, --volume 50, …),
/// --portable (handled by AppPaths, TL-05) and the hidden --crash-test (TL-03).
/// </summary>
public sealed record StartupOptions(bool CrashTest, IReadOnlyList<string> Files, FileAction Action = FileAction.Default,
    IReadOnlyList<ControlCommand>? Controls = null)
{
    /// <summary>
    /// P10 installers: remove everything AudioTastic wrote to the system (file types, context menu, Start menu
    /// shortcut, app identity) and exit without starting. The Windows uninstaller runs it. Settings and playlists stay.
    /// </summary>
    public bool Unregister { get; init; }

    /// <summary>Throws an exception on the UI thread after start-up, to test crash reporting (TL-03).</summary>
    public const string CrashTestSwitch = "--crash-test";

    public IReadOnlyList<ControlCommand> Controls { get; init; } = Controls ?? [];

    /// <summary>True when the only request is to quit: nothing to do when the player is not running.</summary>
    public bool OnlyQuit => Files.Count == 0 && Controls.Count > 0 && Controls.All(c => c.Name == "quit");

    /// <summary>Parses <paramref name="args"/>; throws <see cref="ArgumentException"/> on errors (tests and tools).</summary>
    public static StartupOptions Parse(IReadOnlyList<string> args)
    {
        var result = CommandLine.Parse(args);
        return result.Options ?? throw new ArgumentException(string.Join(Environment.NewLine, result.Result.Errors.Select(e => e.Message)));
    }
}

/// <summary>
/// SI-08: the command line, parsed with System.CommandLine. Options are written --name; on Windows /name works too
/// for the names below (never elsewhere, where /… is an absolute path). --help and --version print to the console
/// the program was started from.
/// </summary>
public static class CommandLine
{
    /// <summary>The parse result, and the options when the command line asks to start or control the player.</summary>
    /// <param name="Result">System.CommandLine's result; when <c>Result.Action</c> is set (help, version or errors),
    /// invoke it instead of starting.</param>
    public sealed record Parsed(ParseResult Result, StartupOptions? Options);

    private static readonly Argument<string[]> FilesArgument = new("files")
    {
        Description = "Audio files, folders or playlists to open (added to the active playlist).",
        Arity = ArgumentArity.ZeroOrMore,
    };

    private static readonly Option<bool> OpenOption = new("--open") { Description = "Add the files and play the first one." };
    private static readonly Option<bool> EnqueueOption = new("--enqueue") { Description = "Only add the files to the end of the playlist." };
    private static readonly Option<bool> PlayOption = new("--play") { Description = "Play (or resume)." };
    private static readonly Option<bool> PauseOption = new("--pause") { Description = "Pause." };
    private static readonly Option<bool> PlayPauseOption = new("--playpause") { Description = "Pause when playing, else play." };
    private static readonly Option<bool> StopOption = new("--stop") { Description = "Stop." };
    private static readonly Option<bool> NextOption = new("--next") { Description = "Next track." };
    private static readonly Option<bool> PreviousOption = new("--prev", "--previous") { Description = "Previous track." };
    private static readonly Option<double?> VolumeOption = new("--volume")
    {
        Description = "Set the volume (0-100).", HelpName = "N", CustomParser = r => Number(r, "--volume", 0, 100),
    };

    private static readonly Option<double?> SeekOption = new("--seek")
    {
        Description = "Jump to this many seconds into the track.", HelpName = "s", CustomParser = r => Number(r, "--seek", 0, double.MaxValue),
    };
    private static readonly Option<bool> QuitOption = new("--quit") { Description = "Close the running player." };
    private static readonly Option<bool> PortableOption = new("--portable") { Description = "Keep settings and data next to the program." };
    private static readonly Option<bool> CrashTestOption = new(StartupOptions.CrashTestSwitch) { Hidden = true };
    private static readonly Option<bool> UnregisterOption = new("--unregister")
    {
        Description = "Remove AudioTastic's file types, context menu entries and Start menu shortcut, then exit (used by the uninstaller).",
    };

    private static readonly RootCommand Root = CreateRoot();

    private static readonly string[] SlashNames =
        ["open", "enqueue", "play", "pause", "playpause", "stop", "next", "prev", "previous", "volume", "seek", "quit", "portable", "unregister"];

    /// <summary>Numbers are read the same in every language setting: "12.5", never "12,5" (Danish Windows read "12.5" as 125).</summary>
    private static double? Number(System.CommandLine.Parsing.ArgumentResult result, string option, double min, double max)
    {
        var text = result.Tokens.Count > 0 ? result.Tokens[0].Value : "";
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value >= min && value <= max)
        {
            return value;
        }
        result.AddError(max == double.MaxValue
            ? $"{option} needs a number of 0 or more, e.g. {option} 12.5 (got '{text}')."
            : $"{option} needs a number from {min} to {max} (got '{text}').");
        return null;
    }

    private static RootCommand CreateRoot()
    {
        // Anything that looks like a switch but is not one is a mistake, not a file name.
        FilesArgument.Validators.Add(r =>
        {
            foreach (var token in r.Tokens.Where(t => t.Value.StartsWith("--", StringComparison.Ordinal)))
            {
                r.AddError($"Unknown option '{token.Value}'. See --help.");
            }
        });
        var root = new RootCommand(
            "AudioTastic: a Winamp-style audio player. When AudioTastic is already running, the files and commands go to the running player." +
            (OperatingSystem.IsWindows() ? " Options may also be written /name, e.g. /next or /volume 50." : ""))
        {
            FilesArgument, OpenOption, EnqueueOption, PlayOption, PauseOption, PlayPauseOption, StopOption, NextOption, PreviousOption,
            VolumeOption, SeekOption, QuitOption, PortableOption, UnregisterOption, CrashTestOption,
        };
        return root;
    }

    public static Parsed Parse(IReadOnlyList<string> args)
    {
        var tokens = args.Select(Normalise).ToArray();
        // No response files: a file name starting with '@' is a file, not a list of arguments.
        var result = Root.Parse(tokens, new ParserConfiguration { ResponseFileTokenReplacer = null });
        if (result.Action is not null || result.Errors.Count > 0)
        {
            return new Parsed(result, null);
        }
        var action = result.GetValue(EnqueueOption) ? FileAction.Enqueue
            : result.GetValue(OpenOption) ? FileAction.Open
            : FileAction.Default;
        var controls = new List<ControlCommand>();
        void Add(Option<bool> option, string name)
        {
            if (result.GetValue(option))
            {
                controls.Add(new ControlCommand(name));
            }
        }
        // Fixed order: transport first, then volume and position, quit last.
        Add(PlayOption, "play");
        Add(PauseOption, "pause");
        Add(PlayPauseOption, "playPause");
        Add(StopOption, "stop");
        Add(PreviousOption, "previous");
        Add(NextOption, "next");
        if (result.GetValue(VolumeOption) is { } volume)
        {
            controls.Add(new ControlCommand("volume", volume));
        }
        if (result.GetValue(SeekOption) is { } seek)
        {
            controls.Add(new ControlCommand("seek", seek));
        }
        Add(QuitOption, "quit");
        var files = (result.GetValue(FilesArgument) ?? []).Select(Path.GetFullPath).ToArray();
        return new Parsed(result, new StartupOptions(result.GetValue(CrashTestOption), files, action, controls)
        {
            Unregister = result.GetValue(UnregisterOption),
        });
    }

    /// <summary>"/next" → "--next" on Windows for the known names; everything else is left alone.</summary>
    private static string Normalise(string arg)
    {
        if (OperatingSystem.IsWindows() && arg.Length > 1 && arg[0] == '/'
            && SlashNames.FirstOrDefault(n => n.Equals(arg[1..], StringComparison.OrdinalIgnoreCase)) is { } name)
        {
            return "--" + name;
        }
        return arg;
    }
}
