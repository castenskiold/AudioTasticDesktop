// P1 soak test (Implementation Plan, testing strategy: "Soak tests").
//
//   dotnet run -c Release --project tools/AudioTastic.SoakTest -- [--minutes 60] [--buffer 20] [--volume 0]
//              [--file long.mp3] [--ffmpeg path/to/ffmpeg]
//
// Plays one long file through FFmpeg → PlaybackEngine → MixerGraph → miniaudio (default device). Volume 0 keeps it
// silent while still exercising the whole path. Without --file, a sine MP3 of the right length is generated into
// tests/media/generated with ffmpeg. Prints a line per minute and exits with 0 when there were no underruns, no audio
// callback took longer than the device period, and memory and handle counts stayed flat.
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
var repo = FindRepoRoot();

file ??= Generate(minutes);
Console.WriteLine($"Soak: {minutes} min, buffer {bufferMs} ms, volume {volume}, file {file}");

var dataDir = Directory.CreateTempSubdirectory("audiotastic-soak-").FullName;
var paths = AppPaths.Resolve(new AppPathsContext(AppPathsContext.CurrentOs, dataDir, dataDir, null, null, [AppPaths.PortableSwitch])).EnsureCreated();
using var loggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Information));
var settings = new SettingsStore(paths, loggerFactory.CreateLogger<SettingsStore>());
settings.Update(SettingsGroup.Output, s => s.Output.BufferMilliseconds = bufferMs);
settings.Update(SettingsGroup.Playback, s => s.Playback.Volume = volume);

using var device = new MiniaudioOutput(loggerFactory.CreateLogger<MiniaudioOutput>());
using var timed = new TimingOutput(device);
using var engine = new PlaybackEngine([new FfmpegSourceFactory()], timed, settings, loggerFactory.CreateLogger<PlaybackEngine>());

await engine.OpenAsync([file]);
var period = TimeSpan.FromSeconds((double)device.PeriodFrames / device.Format!.Value.SampleRate);
Console.WriteLine($"Device: {device.DeviceName}, {device.Format}, period {device.PeriodFrames} frames ({period.TotalMilliseconds:0.0} ms)");

var process = Process.GetCurrentProcess();
var clock = Stopwatch.StartNew();
long? baselineHeap = null, baselineWorkingSet = null;
int? baselineHandles = null;
var failures = new List<string>();
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
    Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
        $"[{minute,3} min] state {engine.State}, pos {engine.Position:hh\\:mm\\:ss}, underruns {engine.Underruns}, " +
        $"callbacks {timed.Renders}, slowest {timed.MaxRender.TotalMilliseconds:0.000} ms, late {late}, " +
        $"heap {heap / 1048576.0:0.0} MB, ws {ws / 1048576.0:0.0} MB, handles {handles}, gen2 {GC.CollectionCount(2)}"));
    if (engine.State != PlaybackState.Playing)
    {
        failures.Add($"playback stopped after {minute} min ({engine.State})");
        break;
    }
}

var drift = Math.Abs((engine.Position - clock.Elapsed).TotalSeconds);
if (engine.Underruns > 0) failures.Add($"{engine.Underruns} underrun(s)");
if (timed.LateRenders(period) > 0) failures.Add($"{timed.LateRenders(period)} audio callback(s) slower than the period");
if (GC.GetTotalMemory(true) - baselineHeap > 20 * 1048576) failures.Add("managed heap grew by more than 20 MB");
if (process.HandleCount - baselineHandles > 50) failures.Add("handle count grew by more than 50");
Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
    $"Done. Position vs wall clock drift {drift:0.00} s; working set {baselineWorkingSet / 1048576.0:0.0} → {process.WorkingSet64 / 1048576.0:0.0} MB."));
Console.WriteLine(failures.Count == 0 ? "SOAK PASSED" : "SOAK FAILED: " + string.Join("; ", failures));
return failures.Count == 0 ? 0 : 1;

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

    public void Open(AudioDeviceInfo? device, AudioFormat format, int bufferMilliseconds, IAudioRenderer renderer)
    {
        _renderer = renderer;
        inner.Open(device, format, bufferMilliseconds, this);
    }

    public void StartOutput() => inner.StartOutput();
    public void StopOutput() => inner.StopOutput();
    public void Close() => inner.Close();
    public void Dispose()
    {
    }
}
