using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using AudioTastic.Analysis;
using AudioTastic.Core.Audio;
using AudioTastic.Core.Audio.Beats;
using AudioTastic.Core.Playback;
using AudioTastic.Core.Playlists;
using AudioTastic.Data;
using AudioTastic.Data.Settings;
using AudioTastic.Input.Ffmpeg;
using AudioTastic.Sdk.Audio;
using Microsoft.Extensions.Logging.Abstractions;

// P8 offline harness.
//   analyze <folder|files...> [--labels labels.json]
//       Beat analysis of each file. labels.json: { "<file name>": { "bpm": 145.0 } }. BPM counts as correct within
//       0.5 BPM, also at half or double tempo (exit criterion: >= 90 % correct).
//   mix <outgoing> <incoming> [--mode bm|io] [--fadein s] [--bass on|off] [--overlap s] [--before s] [--after s] [--end fade|cut] [--fade s]
//       [--limit %] [--return gradual|break] [--out mix.wav]
//       Renders the transition through the real engine, offline. For beat matching it also renders each deck alone and
//       measures the beat alignment over the overlap (exit criterion: within 10 ms).
//   clicks <file> [--from s] [--seconds n] [--out file.wav]
//       The track with a click on every analysed beat (higher click on phrase starts), to hear the grid.
var culture = CultureInfo.InvariantCulture;
if (args.Length == 0)
{
    Console.WriteLine("usage: analyze <folder|files...> [--labels labels.json] | mix <outgoing> <incoming> [options] | clicks <file> [options]");
    return 2;
}
switch (args[0])
{
    case "analyze":
        return Analyze(args[1..]);
    case "mix":
        return await MixAsync(args[1..]);
    case "clicks":
        return Clicks(args[1..]);
    default:
        Console.Error.WriteLine($"unknown command {args[0]}");
        return 2;
}

int Analyze(string[] rest)
{
    var labelsPath = Option(ref rest, "--labels");
    var showWindows = rest.Contains("--windows");
    rest = [.. rest.Where(a => a != "--windows")];
    var files = rest.SelectMany(p => Directory.Exists(p)
            ? Directory.EnumerateFiles(p).Where(f => new FfmpegSourceFactory().CanOpen(f)).Order(StringComparer.OrdinalIgnoreCase)
            : (IEnumerable<string>)[p])
        .ToList();
    var labels = labelsPath is not null && File.Exists(labelsPath)
        ? JsonSerializer.Deserialize<Dictionary<string, Label>>(File.ReadAllText(labelsPath), new JsonSerializerOptions(JsonSerializerDefaults.Web))!
        : [];
    var correct = 0;
    var labelled = 0;
    foreach (var file in files)
    {
        var timer = Stopwatch.StartNew();
        var (info, run) = AnalyzeFileWithRun(file);
        var name = Path.GetFileName(file);
        var (kickMedian, kickSpread) = KickOffset(info, run);
        var line = string.Create(culture,
            $"{name,-45} {info.Bpm,8:0.000} BPM  first beat {info.FirstBeat,7:0.000} s  confidence {info.Confidence,5:0.00}  phrase +{info.PhraseOffset}  " +
            $"kick {kickMedian * 1000:+0.0;-0.0} ms (spread {kickSpread * 1000:0.0})  beat {info.BeatStart:0.0}-{info.BeatEnd:0.0} s  " +
            $"breaks {string.Join(", ", info.Breaks.Select(b => $"{b.Start:0.0}-{b.End:0.0}"))}  ({timer.Elapsed.TotalSeconds:0.0} s)");
        if (showWindows)
        {
            // Per 32 beats: where the kick sits against the grid (ms).
            var parts = new List<string>();
            for (var t = info.BeatTime(info.BeatAtOrAfter(info.BeatStart)); t + (32 * info.Period) <= info.BeatEnd; t += 32 * info.Period)
            {
                if (BeatDetector.KickAttack(run.ClickEnvelope, run.ThumpEnvelope, run.EnvelopeRate, info.Period, t, t, t + (32 * info.Period)) is { } a)
                {
                    parts.Add(string.Create(culture, $"{t:0}s:{Wrap(a - t, info.Period) * 1000:+0;-0}"));
                }
            }
            Console.WriteLine("    " + string.Join("  ", parts));
        }
        if (labels.TryGetValue(name, out var label))
        {
            labelled++;
            var ok = Math.Abs(info.Bpm - label.Bpm) <= 0.5 || Math.Abs((info.Bpm * 2) - label.Bpm) <= 0.5 || Math.Abs((info.Bpm / 2) - label.Bpm) <= 0.5;
            correct += ok ? 1 : 0;
            line += string.Create(culture, $"  label {label.Bpm:0.00}: {(ok ? "OK" : "WRONG")}");
        }
        Console.WriteLine(line);
    }
    if (labelled > 0)
    {
        Console.WriteLine(string.Create(culture, $"BPM correct on {correct} of {labelled} labelled tracks ({100.0 * correct / labelled:0} %)."));
        return correct * 10 >= labelled * 9 ? 0 : 1;
    }
    return 0;
}

async Task<int> MixAsync(string[] rest)
{
    var output = Option(ref rest, "--out") ?? "mix.wav";
    var xf = new CrossfadingSettings
    {
        Enabled = true,
        Mode = Option(ref rest, "--mode") == "io" ? CrossfadeMode.IntroOutro : CrossfadeMode.BeatMatch,
        OverlapSeconds = Number(Option(ref rest, "--overlap"), 16),
        FadeInSeconds = Number(Option(ref rest, "--fadein"), 8),
        BassFadeIn = Option(ref rest, "--bass") != "off",
        MixBeforeBeatEndSeconds = Number(Option(ref rest, "--before"), 30),
        MixInAfterBeatStartSeconds = Number(Option(ref rest, "--after"), 0),
        EndBehaviour = Option(ref rest, "--end") == "cut" ? CrossfadeEnd.Cut : CrossfadeEnd.FadeOut,
        FadeOutSeconds = Number(Option(ref rest, "--fade"), 8),
        TempoLimitPercent = Number(Option(ref rest, "--limit"), 8),
        TempoReturn = Option(ref rest, "--return") == "break" ? TempoReturnMode.CutAtNextBreak : TempoReturnMode.Gradual,
    };
    if (rest.Length < 2)
    {
        Console.Error.WriteLine("mix needs <outgoing> <incoming>");
        return 2;
    }
    var (first, second) = (Path.GetFullPath(rest[0]), Path.GetFullPath(rest[1]));
    Console.WriteLine("Analysing...");
    var beats = new Dictionary<string, BeatInfo>(StringComparer.OrdinalIgnoreCase) { [first] = AnalyzeFile(first), [second] = AnalyzeFile(second) };
    foreach (var (path, info) in beats)
    {
        Console.WriteLine(string.Create(culture, $"  {Path.GetFileName(path)}: {info.Bpm:0.00} BPM, beat {info.BeatStart:0.0}-{info.BeatEnd:0.0} s"));
    }
    var outDuration = Duration(first);
    var inDuration = Duration(second);
    var plan = CrossfadePlanner.Plan(xf, new CrossfadeTrack(outDuration, beats[first]), new CrossfadeTrack(inDuration, beats[second]), 0);
    if (plan.Mode == CrossfadeMode.BeatMatch && plan.OutgoingBpm is { } bpmOut)
    {
        var (shift, differences) = new CrossfadeAligner([new FfmpegSourceFactory()]).Measure(new CrossfadeAlignment(first, plan.MixStart, 1, second, plan.IncomingStart,
            plan.IncomingSpeed, plan.IncomingFadeLength, plan.Overlap, 60 / bpmOut));
        Console.WriteLine("Aligner, incoming transients against the outgoing ones (ms; whole overlap, first half, second half): " + string.Join(" ", differences.Select(d => (d * 1000).ToString("+0.0;-0.0", culture))));
        Console.WriteLine(shift is { } s ? string.Create(culture, $"Aligner: incoming entry moved {s * 1000:+0.0;-0.0} ms") : "Aligner: no clear answer; the grids decide");
    }
    var fallbackText = plan.Fallback is null ? string.Empty : $" (fallback: {plan.Fallback})";
    Console.WriteLine(string.Create(culture,
        $"Plan: {plan.Mode} at {plan.MixStart:0.000} s, incoming from {plan.IncomingStart:0.000} s at {plan.IncomingSpeed:0.0000}x ({plan.IncomingBpm:0.00} -> {plan.MatchedBpm:0.00} BPM), overlap {plan.Overlap:0.0} s, fade {plan.OutgoingFadeLength:0.0} s{fallbackText}"));
    var lead = 25.0;
    if (Environment.GetEnvironmentVariable("MIXLAB_PLAN_ONLY") is { Length: > 0 })
    {
        return 0; // diagnostics: the plan and the aligner only
    }
    var startAt = Math.Max(0, plan.MixStart - lead);
    var mixOffset = plan.MixStart - startAt; // output seconds into the render (outgoing speed 1)
    var renderSeconds = mixOffset + plan.OutgoingTail + 4;

    var both = await RenderAsync(xf, first, second, beats, startAt, renderSeconds, Mute.None);
    WriteWav(output, both.Samples, both.Rate);
    Console.WriteLine(string.Create(culture, $"Wrote {output}: {renderSeconds:0} s from {startAt:0.0} s of the outgoing track; the mix starts at {mixOffset:0.0} s into the file."));
    if (both.Underruns > 0)
    {
        Console.WriteLine($"WARNING: {both.Underruns} underruns while rendering.");
    }
    if (plan.Mode != CrossfadeMode.BeatMatch)
    {
        return 0;
    }

    // XF-02 exit criterion: render each deck alone and compare their beats over the overlap.
    var outgoingOnly = await RenderAsync(xf, first, second, beats, startAt, renderSeconds, Mute.Incoming);
    var incomingOnly = await RenderAsync(xf, first, second, beats, startAt, renderSeconds, Mute.Outgoing);
    if (Environment.GetEnvironmentVariable("MIXLAB_SOLOS") is { Length: > 0 })
    {
        // Diagnostics: the two decks rendered alone (their sum should be the mix).
        WriteWav(Path.ChangeExtension(output, null) + " (outgoing only).wav", outgoingOnly.Samples, outgoingOnly.Rate);
        WriteWav(Path.ChangeExtension(output, null) + " (incoming only).wav", incomingOnly.Samples, incomingOnly.Rate);
    }
    var outPeriod = 60 / plan.OutgoingBpm!.Value;
    // Measured over the overlap at full volume (after the fade-in, when both kicks are clearly there).
    var measureFrom = mixOffset + plan.IncomingFadeLength;
    var lags = Alignment(outgoingOnly.Samples, incomingOnly.Samples, both.Rate, measureFrom, plan.Overlap, outPeriod);
    // Where each deck's kicks should be in the render: the outgoing grid from the render start, the incoming grid from
    // the mix start (its entry point is a beat), stretched by its speed.
    var outBeats = beats[first];
    var expectedOut = outBeats.BeatTime(outBeats.BeatAtOrAfter(startAt + mixOffset)) - startAt;
    var expectedIn = mixOffset;
    var runA = Envelopes(outgoingOnly.Samples, both.Rate);
    var runB = Envelopes(incomingOnly.Samples, both.Rate);
    foreach (var (time, lag) in lags)
    {
        var t = measureFrom + time;
        var a = BeatDetector.KickAttack(runA.ClickEnvelope, runA.ThumpEnvelope, runA.EnvelopeRate, outPeriod, t, t, t + (8 * outPeriod));
        var b = BeatDetector.KickAttack(runB.ClickEnvelope, runB.ThumpEnvelope, runB.EnvelopeRate, outPeriod, t, t, t + (8 * outPeriod));
        Console.WriteLine(string.Create(culture,
            $"  {time,6:0.0} s into the overlap: incoming beats {Wrap(lag, outPeriod) * 1000,6:+0.0;-0.0} ms from the outgoing ones " +
            $"(outgoing kick {Wrap((a ?? double.NaN) - expectedOut, outPeriod) * 1000:+0.0;-0.0} ms, incoming kick {Wrap((b ?? double.NaN) - expectedIn, outPeriod) * 1000:+0.0;-0.0} ms from where they should be)"));
    }
    lags = [.. lags.Select(l => (l.Time, Wrap(l.Lag, outPeriod)))];
    var worst = lags.Count == 0 ? double.NaN : lags.Max(l => Math.Abs(l.Lag));
    Console.WriteLine(string.Create(culture, $"Worst beat misalignment over the overlap: {worst * 1000:0.0} ms (criterion: within 10 ms)."));
    return worst <= 0.010 ? 0 : 1;
}

async Task<Render> RenderAsync(CrossfadingSettings xf, string first, string second, Dictionary<string, BeatInfo> beats,
    double startAt, double seconds, Mute mute)
{
    var dir = Directory.CreateTempSubdirectory("audiotastic-mixlab-").FullName;
    try
    {
        var paths = AppPaths.Resolve(new AppPathsContext(AppPathsContext.CurrentOs, dir, dir, null, null, [AppPaths.PortableSwitch])).EnsureCreated();
        var settings = new SettingsStore(paths);
        settings.Update(SettingsGroup.Playback, s =>
        {
            s.Playback.Volume = 100;
            s.Playback.Fades = new FadeSettings { OnPause = false, OnSeek = false, OnStop = false };
            s.Playback.ResumeSession = ResumeMode.Off;
        });
        settings.Update(SettingsGroup.Crossfading, s => s.Crossfading = xf);
        using var device = new OfflineOutput();
        using var engine = new PlaybackEngine([new FfmpegSourceFactory()], device, settings, NullLogger<PlaybackEngine>.Instance)
        {
            BeatProvider = new FixedBeats(beats),
            // MIXLAB_SHIFT_MS: listening tests, the incoming kicks moved by a fixed amount (output ms) after the alignment.
            CrossfadeAligner = Environment.GetEnvironmentVariable("MIXLAB_SHIFT_MS") is { Length: > 0 } shiftMs
                ? new FixedShift(double.Parse(shiftMs, CultureInfo.InvariantCulture) / 1000, new CrossfadeAligner([new FfmpegSourceFactory()]))
                : new CrossfadeAligner([new FfmpegSourceFactory()]),
        };
        device.Graph = engine.Graph;
        var playlist = new Playlist("mix", [new PlaylistEntry(first), new PlaylistEntry(second)]);
        await engine.ResumeSessionAsync(playlist, playlist.Entries[0], TimeSpan.FromSeconds(startAt), play: false);
        device.Capture = true;
        await engine.PauseAsync(); // resume: play
        var muted = false;
        while (device.CapturedFrames < seconds * device.Rate)
        {
            if (!muted && mute != Mute.None && engine.Crossfade is { Mixing: false })
            {
                // The incoming deck is armed: silence one side for the alignment measurement.
                var playing = engine.Graph.DeckA.IsPlaying ? engine.Graph.DeckA : engine.Graph.DeckB;
                var armed = ReferenceEquals(playing, engine.Graph.DeckA) ? engine.Graph.DeckB : engine.Graph.DeckA;
                var target = mute == Mute.Incoming ? armed : playing;
                target.Gain.TargetGain = 0;
                target.Gain.SnapToTarget();
                muted = true;
            }
            await Task.Delay(2);
        }
        await engine.StopAsync();
        return new Render(device.Take(), device.Rate, engine.Graph.DeckA.Underruns + engine.Graph.DeckB.Underruns);
    }
    finally
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

// The decks' kick attacks (the analyser's own method: click and thump envelopes averaged over 8 beats), per 8 beats
// of the overlap; the difference is the misalignment.
List<(double Time, double Lag)> Alignment(float[] a, float[] b, int rate, double from, double length, double period)
{
    var runA = Envelopes(a, rate);
    var runB = Envelopes(b, rate);
    var result = new List<(double, double)>();
    for (var t = from; t + (8 * period) <= from + length; t += 8 * period)
    {
        var attackA = BeatDetector.KickAttack(runA.ClickEnvelope, runA.ThumpEnvelope, runA.EnvelopeRate, period, t, t, t + (8 * period));
        var attackB = BeatDetector.KickAttack(runB.ClickEnvelope, runB.ThumpEnvelope, runB.EnvelopeRate, period, t, t, t + (8 * period));
        if (attackA is { } x && attackB is { } y)
        {
            result.Add((t - from, y - x));
        }
    }
    return result;
}

// A time difference folded to the nearest beat: (-period/2, period/2].
static double Wrap(double d, double period) => d - (Math.Round(d / period) * period);

static BeatAnalyzer.Run Envelopes(float[] stereo, int rate)
{
    var run = new BeatAnalyzer.Run(new AudioFormat(rate, 2));
    run.Process(stereo);
    return run;
}

int Clicks(string[] rest)
{
    var output = Option(ref rest, "--out") ?? "clicks.wav";
    var from = Number(Option(ref rest, "--from"), 0);
    var seconds = Number(Option(ref rest, "--seconds"), 60);
    var shift = Number(Option(ref rest, "--shift-ms"), 0) / 1000; // listening tests: clicks moved by this much
    var path = rest[0];
    var info = AnalyzeFile(path);
    info = info with { FirstBeat = info.FirstBeat + shift };
    using var source = new FfmpegSourceFactory().Open(path);
    var rate = source.Format.SampleRate;
    var channels = source.Format.Channels;
    source.Seek((long)(from * rate));
    var frames = (int)(seconds * rate);
    var samples = new float[frames * channels];
    var read = 0;
    while (read < frames)
    {
        var n = source.Read(samples.AsSpan(read * channels));
        if (n == 0)
        {
            break;
        }
        read += n;
    }
    var stereo = new float[frames * 2];
    for (var f = 0; f < frames; f++)
    {
        stereo[f * 2] = samples[f * channels] * 0.7f;
        stereo[(f * 2) + 1] = samples[(f * channels) + (channels > 1 ? 1 : 0)] * 0.7f;
    }
    for (var k = info.BeatAtOrAfter(from); info.BeatTime(k) < from + seconds; k++)
    {
        // With the per-section correction, as the engine uses it.
        var beat = info.BeatTime(k) + info.LocalOffset(info.BeatTime(k));
        var at = (int)((beat - from) * rate);
        if (at < 0)
        {
            continue;
        }
        var hz = info.IsPhraseStart(k) ? 2000.0 : 1000.0;
        for (var i = 0; i < rate / 50 && at + i < frames; i++)
        {
            var v = (float)(0.35 * Math.Sin(2 * Math.PI * hz * i / rate) * (1 - (i / (rate / 50.0))));
            stereo[(at + i) * 2] += v;
            stereo[((at + i) * 2) + 1] += v;
        }
    }
    WriteWav(output, stereo, rate);
    Console.WriteLine(string.Create(culture, $"Wrote {output}: {seconds:0} s from {from:0} s with a click on every beat ({info.Bpm:0.00} BPM), a higher click on phrase starts."));
    return 0;
}

static BeatInfo AnalyzeFile(string path)
{
    // A "nudgeMs" in labels.json next to the file moves its grid, like the app's per-file adjustment.
    var info = AnalyzeFileWithRun(path).Info;
    var labels = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "labels.json");
    if (File.Exists(labels) &&
        JsonSerializer.Deserialize<Dictionary<string, Label>>(File.ReadAllText(labels), new JsonSerializerOptions(JsonSerializerDefaults.Web)) is { } all &&
        all.TryGetValue(Path.GetFileName(path), out var label) && label.NudgeMs is { } nudge)
    {
        info = info.Nudged(nudge / 1000);
    }
    return info;
}

static (BeatInfo Info, BeatAnalyzer.Run Run) AnalyzeFileWithRun(string path)
{
    using var source = new FfmpegSourceFactory().Open(path);
    var run = new BeatAnalyzer.Run(source.Format);
    var buffer = new float[8192 * source.Format.Channels];
    int frames;
    while ((frames = source.Read(buffer)) > 0)
    {
        run.Process(buffer.AsSpan(0, frames * source.Format.Channels));
    }
    return (run.Detect(), run);
}

// Diagnostic: the kick attack per 32 beats over the drum section relative to the grid (median and spread, seconds).
// Near zero with a small spread means the grid sits on the kicks all through the track.
static (double Median, double Spread) KickOffset(BeatInfo info, BeatAnalyzer.Run run)
{
    var offsets = new List<double>();
    for (var t = info.BeatTime(info.BeatAtOrAfter(info.BeatStart)); t + (32 * info.Period) <= info.BeatEnd; t += 32 * info.Period)
    {
        if (BeatDetector.KickAttack(run.ClickEnvelope, run.ThumpEnvelope, run.EnvelopeRate, info.Period, t, t, t + (32 * info.Period)) is { } attack)
        {
            var d = attack - t;
            offsets.Add(d - (Math.Round(d / info.Period) * info.Period));
        }
    }
    if (offsets.Count == 0)
    {
        return (double.NaN, double.NaN);
    }
    offsets.Sort();
    return (offsets[offsets.Count / 2], offsets[offsets.Count * 9 / 10] - offsets[offsets.Count / 10]);
}

static double Duration(string path)
{
    using var source = new FfmpegSourceFactory().Open(path);
    return source.LengthFrames!.Value / (double)source.Format.SampleRate;
}

static void WriteWav(string path, float[] stereo, int rate)
{
    using var w = new BinaryWriter(File.Create(path));
    var data = stereo.Length * 2;
    w.Write("RIFF"u8);
    w.Write(36 + data);
    w.Write("WAVEfmt "u8);
    w.Write(16);
    w.Write((short)1);
    w.Write((short)2);
    w.Write(rate);
    w.Write(rate * 4);
    w.Write((short)4);
    w.Write((short)16);
    w.Write("data"u8);
    w.Write(data);
    foreach (var v in stereo)
    {
        w.Write((short)Math.Round(Math.Clamp(v, -1f, 1f) * 32767));
    }
}

static double Number(string? text, double fallback) =>
    text is not null && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

static string? Option(ref string[] args, string name)
{
    var i = Array.IndexOf(args, name);
    if (i < 0 || i + 1 >= args.Length)
    {
        return null;
    }
    var value = args[i + 1];
    args = [.. args[..i], .. args[(i + 2)..]];
    return value;
}

internal sealed record Label(double Bpm, double? NudgeMs = null);

internal sealed record Render(float[] Samples, int Rate, long Underruns);

internal enum Mute
{
    None,
    Incoming,
    Outgoing,
}

/// <summary>Beat analysis computed up front.</summary>
/// <summary>Moves the incoming kicks by a fixed amount (output seconds; positive = later) on top of the aligner.</summary>
internal sealed class FixedShift(double seconds, ICrossfadeAligner aligner) : ICrossfadeAligner
{
    public double? Refine(CrossfadeAlignment alignment) => (aligner.Refine(alignment) ?? 0) - (seconds * alignment.IncomingSpeed);
}

internal sealed class FixedBeats(Dictionary<string, BeatInfo> beats) : IBeatInfoProvider
{
    public BeatInfo? TryGet(PlaylistEntry entry) => beats.GetValueOrDefault(Path.GetFullPath(entry.Path));
}

/// <summary>
/// An output device that renders as fast as the decks can decode (never ahead of them, so there are no underruns) and
/// keeps what it renders.
/// </summary>
internal sealed class OfflineOutput : IAudioOutput
{
    private const int BlockFrames = 256;
    private readonly List<float> _captured = [];
    private readonly Lock _lock = new();
    private IAudioRenderer? _renderer;
    private Thread? _thread;
    private volatile bool _running;
    private volatile bool _capture;

    public MixerGraph? Graph { get; set; }

    public bool Capture
    {
        get => _capture;
        set => _capture = value;
    }

    public int Rate => Format?.SampleRate ?? 44_100;

    public long CapturedFrames
    {
        get
        {
            lock (_lock)
            {
                return _captured.Count / 2;
            }
        }
    }

    public string BackendName => "Offline";
    public bool IsOpen { get; private set; }
    public bool IsRunning => _running;
    public string? DeviceName => "Offline";
    public AudioFormat? Format { get; private set; }

    public event EventHandler? DevicesChanged
    {
        add { }
        remove { }
    }

    public event EventHandler<AudioOutputLostEventArgs>? OutputLost
    {
        add { }
        remove { }
    }

    public IReadOnlyList<AudioDeviceInfo> GetDevices() => [];

    public void Open(AudioDeviceInfo? device, AudioFormat format, OutputOptions options, IAudioRenderer renderer)
    {
        Close();
        _renderer = renderer;
        Format = format;
        IsOpen = true;
    }

    public void StartOutput()
    {
        if (_running)
        {
            return;
        }
        _running = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "Offline output" };
        _thread.Start();
    }

    public void StopOutput()
    {
        _running = false;
        if (_thread is { } t && t != Thread.CurrentThread)
        {
            t.Join();
        }
        _thread = null;
    }

    public void Close()
    {
        StopOutput();
        IsOpen = false;
    }

    public void Dispose() => Close();

    public float[] Take()
    {
        lock (_lock)
        {
            return [.. _captured];
        }
    }

    private void Loop()
    {
        var buffer = new float[BlockFrames * 2];
        // Until the incoming deck is armed, at most 4x real time: the engine plans and aligns the transition on other
        // threads, which in real time has 10 s or more before the mix start.
        var clock = Stopwatch.StartNew();
        long rendered = 0;
        var armed = false;
        while (_running)
        {
            armed |= Graph?.Next is not null;
            if (Graph is { } g && (g.Paused || !g.DeckA.CanRender(BlockFrames) || !g.DeckB.CanRender(BlockFrames) ||
                (!armed && rendered > 4 * clock.Elapsed.TotalSeconds * Rate)))
            {
                // Paused: nothing to capture (every render starts exactly at playback start); otherwise let the
                // decoders catch up.
                Thread.Sleep(1);
                continue;
            }
            _renderer!.Render(buffer);
            rendered += BlockFrames;
            if (_capture)
            {
                lock (_lock)
                {
                    _captured.AddRange(buffer);
                }
            }
        }
    }
}
