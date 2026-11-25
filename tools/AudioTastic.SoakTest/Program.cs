// P1 soak test (Implementation Plan, testing strategy: "Soak tests").
//
//   dotnet run -c Release --project tools/AudioTastic.SoakTest -- [--minutes 60] [--buffer 20] [--volume 0]
//              [--file long.mp3] [--ffmpeg path/to/ffmpeg] [--mix] [--null-device]
//
// Plays one long file through FFmpeg → PlaybackEngine → MixerGraph → miniaudio (default device). Volume 0 keeps it
// silent while still exercising the whole path. Without --file, a sine MP3 of the right length is generated into
// tests/media/generated with ffmpeg. Prints a line per minute and exits with 0 when there were no underruns, no audio
// callback took longer than the device period, and memory and handle counts stayed flat.
//
// --mix (P14, 24-hour soak): instead plays a playlist of six 3-minute tracks in six formats (MP3, FLAC, Ogg Vorbis,
// AAC, Opus, WAV; generated once with ffmpeg) on repeat, with Intro/Outro crossfading (8 s) and the equalizer and
// limiter in the DSP chain, so decks load, crossfade, swap and unload all day. It also fails when playback stops or
// track changes stop happening.
using System.Diagnostics;
using System.Globalization;
using AudioTastic.Core.Audio;
using AudioTastic.Core.Audio.Output;
using AudioTastic.Core.Playback;
using AudioTastic.Data;
using AudioTastic.Data.Settings;
using AudioTastic.Input.Ffmpeg;
using AudioTastic.Sdk.Audio;
using Microsoft.Extensions.Logging;

var minutes = Arg("--minutes", 60);
var bufferMs = Arg("--buffer", 20);
var volume = Arg("--volume", 0);
var file = ArgString("--file");
var ffmpegExe = ArgString("--ffmpeg") ?? "ffmpeg";
var mix = args.Contains("--mix");
// --null-device: miniaudio's null backend, which takes audio at real-time pace without a sound card (a PC reached over
// Remote Desktop has no device while the session is disconnected). Exercises everything except the driver.
var nullDevice = args.Contains("--null-device");
var repo = FindRepoRoot();

var mixTracks = mix ? GenerateMix() : [];
file ??= mix ? mixTracks[0] : Generate(minutes);
Console.WriteLine(mix
    ? $"Soak (mix): {minutes} min, buffer {bufferMs} ms, volume {volume}, {mixTracks.Count} tracks with crossfading and DSP"
    : $"Soak: {minutes} min, buffer {bufferMs} ms, volume {volume}, file {file}");

var dataDir = Directory.CreateTempSubdirectory("audiotastic-soak-").FullName;
var paths = AppPaths.Resolve(new AppPathsContext(AppPathsContext.CurrentOs, dataDir, dataDir, null, null, [AppPaths.PortableSwitch])).EnsureCreated();
using var loggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Information));
var settings = new SettingsStore(paths, loggerFactory.CreateLogger<SettingsStore>());
settings.Update(SettingsGroup.Output, s => s.Output.BufferMilliseconds = bufferMs);
settings.Update(SettingsGroup.Playback, s => s.Playback.Volume = volume);
if (mix)
{
    settings.Update(SettingsGroup.Playback, s => s.Playback.Repeat = RepeatMode.Playlist);
    settings.Update(SettingsGroup.Crossfading, s =>
    {
        s.Crossfading.Enabled = true;
        s.Crossfading.Mode = CrossfadeMode.IntroOutro;
        s.Crossfading.OverlapSeconds = 8;
    });
}

using var device = new MiniaudioOutput(loggerFactory.CreateLogger<MiniaudioOutput>(), useNullBackend: nullDevice);
using var timed = new TimingOutput(device);
using var engine = new PlaybackEngine([new FfmpegSourceFactory()], timed, settings, loggerFactory.CreateLogger<PlaybackEngine>());

var trackChanges = 0;
engine.TrackChanged += (_, _) => Interlocked.Increment(ref trackChanges);
if (mix)
{
    var eq = new AudioTastic.Core.Audio.Dsp.EqualizerStage { Enabled = true };
    eq.Apply(new EqualizerSettings { PreampDb = -3, Bands = [3, 2, 1, 0, -1, -1, 0, 1, 2, 3] });
    var limiter = new AudioTastic.Core.Audio.Dsp.LimiterStage { Enabled = true };
    limiter.Apply(new LimiterSettings());
    engine.Graph.Dsp.SetStages([eq, limiter]);
    var playlist = new AudioTastic.Core.Playlists.Playlist("Soak", mixTracks.Select(t => new AudioTastic.Core.Playlists.PlaylistEntry(t)));
    await engine.PlayAsync(playlist, playlist.Entries[0]);
}
else
{
    await engine.OpenAsync([file]);
}
var period = TimeSpan.FromSeconds((double)device.PeriodFrames / device.Format!.Value.SampleRate);
Console.WriteLine($"Device: {device.DeviceName}, {device.Format}, period {device.PeriodFrames} frames ({period.TotalMilliseconds:0.0} ms)");

var process = Process.GetCurrentProcess();
var clock = Stopwatch.StartNew();
long? baselineHeap = null, baselineWorkingSet = null;
int? baselineHandles = null;
var failures = new List<string>();
var lastChanges = 0;
var quietMinutes = 0;
var outageMinutes = 0;
var pausedWithDevice = 0;
// The first use of a code path (the first crossfade, the first track of a new format) is compiled by the runtime on
// the audio thread and can make one callback late; long runs judge lateness after a 10-minute warm-up. Underruns
// count from the start.
var warmUpMinutes = minutes > 20 ? 10 : 0;
long lateAtWarmUp = 0;
for (var minute = 1; minute <= minutes; minute++)
{
    await Task.Delay(TimeSpan.FromMinutes(1));
    process.Refresh();
    var heap = GC.GetTotalMemory(forceFullCollection: minute == 1 || minute == minutes);
    var ws = process.WorkingSet64;
    var handles = process.HandleCount;
    if (minute == 1)
    {
        (baselineHeap, baselineWorkingSet, baselineHandles) = (heap, ws, handles);
    }
    var late = timed.LateRenders(period);
    if (minute == warmUpMinutes)
    {
        lateAtWarmUp = late;
    }
    Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
        $"[{minute,3} min] state {engine.State}, pos {engine.Position:hh\\:mm\\:ss}, underruns {engine.Underruns}, " +
        $"callbacks {timed.Renders}, slowest {timed.MaxRender.TotalMilliseconds:0.000} ms, late {late}, " +
        $"heap {heap / 1048576.0:0.0} MB, ws {ws / 1048576.0:0.0} MB, handles {handles}, gen2 {GC.CollectionCount(2)}" +
        $"{(mix ? $", tracks {trackChanges}" : string.Empty)}"));
    // P14: with no audio device at all (a Remote Desktop disconnect) the engine pauses and continues by itself when one
    // appears: that is an outage to report, not a failure. Paused with a device for two minutes is.
    if (engine.State == PlaybackState.Paused && device.GetDevices().Count == 0)
    {
        outageMinutes++;
        Console.WriteLine($"[{minute,3} min] no audio device: waiting (outage minutes {outageMinutes})");
        continue;
    }
    if (engine.State == PlaybackState.Paused && ++pausedWithDevice <= 2)
    {
        continue; // a device just came back: give the engine a moment
    }
    if (engine.State != PlaybackState.Playing)
    {
        failures.Add($"playback stopped after {minute} min ({engine.State})");
        break;
    }
    pausedWithDevice = 0;
    if (mix)
    {
        // Tracks are 3 minutes long: five minutes without a track change means the playlist got stuck.
        quietMinutes = trackChanges == lastChanges ? quietMinutes + 1 : 0;
        lastChanges = trackChanges;
        if (quietMinutes >= 5)
        {
            failures.Add($"no track change for 5 minutes (at {minute} min)");
            break;
        }
    }
}

var drift = mix ? 0 : Math.Abs((engine.Position - clock.Elapsed).TotalSeconds); // a playlist has no single position
if (engine.Underruns > 0) failures.Add($"{engine.Underruns} underrun(s)");
var lateAfterWarmUp = timed.LateRenders(period) - lateAtWarmUp;
if (lateAfterWarmUp > 0) failures.Add($"{lateAfterWarmUp} audio callback(s) slower than the period after the warm-up");
if (lateAtWarmUp > 0) Console.WriteLine($"Warm-up: {lateAtWarmUp} late callback(s) in the first {warmUpMinutes} min (first use of code paths), no underrun from them.");
if (GC.GetTotalMemory(true) - baselineHeap > 20 * 1048576) failures.Add("managed heap grew by more than 20 MB");
if (process.HandleCount - baselineHandles > 50) failures.Add("handle count grew by more than 50");
Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
    $"Done. Position vs wall clock drift {drift:0.00} s; working set {baselineWorkingSet / 1048576.0:0.0} → {process.WorkingSet64 / 1048576.0:0.0} MB."));
if (outageMinutes > 0) Console.WriteLine($"Audio device outages: {outageMinutes} min without any device (playback paused and continued by itself).");
Console.WriteLine(failures.Count == 0 ? "SOAK PASSED" : "SOAK FAILED: " + string.Join("; ", failures));
return failures.Count == 0 ? 0 : 1;

List<string> GenerateMix()
{
    var dir = Path.Combine(repo, "tests", "media", "generated", "soak-mix");
    Directory.CreateDirectory(dir);
    (string Ext, string[] Codec)[] formats =
    [
        ("mp3", ["-c:a", "libmp3lame", "-b:a", "192k"]), ("flac", ["-c:a", "flac"]), ("ogg", ["-c:a", "libvorbis", "-q:a", "5"]),
        ("m4a", ["-c:a", "aac", "-b:a", "192k"]), ("opus", ["-c:a", "libopus", "-b:a", "128k"]), ("wav", ["-c:a", "pcm_s16le"]),
    ];
    var tracks = new List<string>();
    for (var i = 0; i < formats.Length; i++)
    {
        var (ext, codec) = formats[i];
        var path = Path.Combine(dir, $"track{i + 1}.{ext}");
        tracks.Add(path);
        if (File.Exists(path))
        {
            continue;
        }
        Console.WriteLine($"Generating {path} with {ffmpegExe} ...");
        // A beat (sine bursts) at a different pitch per track, 3 minutes, so crossfades join different material.
        var freq = 110 * (i + 2);
        string[] arguments =
        [
            "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi",
            "-i", $"sine=frequency={freq}:sample_rate=44100:beep_factor=4", "-t", "180", "-ac", "2", .. codec, path,
        ];
        using var p = Process.Start(new ProcessStartInfo(ffmpegExe, arguments))!;
        p.WaitForExit();
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException("ffmpeg failed; pass --ffmpeg.");
        }
    }
    return tracks;
}

string Generate(int mins)
{
    var dir = Path.Combine(repo, "tests", "media", "generated");
    Directory.CreateDirectory(dir);
    var path = Path.Combine(dir, $"soak-{mins}min.mp3");
    if (File.Exists(path))
    {
        return path;
    }
    Console.WriteLine($"Generating {path} with {ffmpegExe} ...");
    var seconds = (mins * 60) + 30;
    using var p = Process.Start(new ProcessStartInfo(ffmpegExe,
        ["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "sine=frequency=220:sample_rate=44100",
         "-t", seconds.ToString(CultureInfo.InvariantCulture), "-ac", "2", "-c:a", "libmp3lame", "-b:a", "192k", path]))!;
    p.WaitForExit();
    if (p.ExitCode != 0)
    {
        throw new InvalidOperationException("ffmpeg failed; pass --file or --ffmpeg.");
    }
    return path;
}

int Arg(string name, int fallback) =>
    ArgString(name) is { } v ? int.Parse(v, CultureInfo.InvariantCulture) : fallback;

string? ArgString(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static string FindRepoRoot()
{
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
    {
        if (File.Exists(Path.Combine(dir.FullName, "AudioTastic.slnx")))
        {
            return dir.FullName;
        }
    }
    return Directory.GetCurrentDirectory();
}

/// <summary>Decorates the real output and times every audio callback (allocation-free).</summary>
internal sealed class TimingOutput(IAudioOutput inner) : IAudioOutput, IAudioRenderer
{
    private IAudioRenderer? _renderer;
    private long _renders;
    private long _maxTicks;
    private readonly long[] _histogram = new long[64]; // render time in 0.25 ms buckets

    public long Renders => Interlocked.Read(ref _renders);
    public TimeSpan MaxRender => TimeSpan.FromSeconds((double)Interlocked.Read(ref _maxTicks) / Stopwatch.Frequency);

    public long LateRenders(TimeSpan period)
    {
        var bucket = Math.Min(_histogram.Length - 1, (int)(period.TotalMilliseconds * 4));
        long late = 0;
        for (var i = bucket; i < _histogram.Length; i++)
        {
            late += Interlocked.Read(ref _histogram[i]);
        }
        return late;
    }

    public void Render(Span<float> output)
    {
        var start = Stopwatch.GetTimestamp();
        _renderer!.Render(output);
        var ticks = Stopwatch.GetTimestamp() - start;
        Interlocked.Increment(ref _renders);
        var ms = (double)ticks * 1000 / Stopwatch.Frequency;
        Interlocked.Increment(ref _histogram[Math.Min(_histogram.Length - 1, (int)(ms * 4))]);
        long max;
        while (ticks > (max = Interlocked.Read(ref _maxTicks)) && Interlocked.CompareExchange(ref _maxTicks, ticks, max) != max)
        {
        }
    }

    public string BackendName => inner.BackendName;
    public bool IsOpen => inner.IsOpen;
    public bool IsRunning => inner.IsRunning;
    public string? DeviceName => inner.DeviceName;
    public AudioFormat? Format => inner.Format;

    public event EventHandler? DevicesChanged
    {
        add => inner.DevicesChanged += value;
        remove => inner.DevicesChanged -= value;
    }

    public event EventHandler<AudioOutputLostEventArgs>? OutputLost
    {
        add => inner.OutputLost += value;
        remove => inner.OutputLost -= value;
    }

    public IReadOnlyList<AudioDeviceInfo> GetDevices() => inner.GetDevices();

    public void Open(AudioDeviceInfo? device, AudioFormat format, OutputOptions options, IAudioRenderer renderer)
    {
        _renderer = renderer;
        inner.Open(device, format, options, this);
    }

    public void StartOutput() => inner.StartOutput();
    public void StopOutput() => inner.StopOutput();
    public void Close() => inner.Close();
    public void Dispose()
    {
    }
}
