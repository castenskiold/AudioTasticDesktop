using AudioTastic.Core.Audio;
using AudioTastic.Core.Audio.Beats;
using AudioTastic.Core.Playback;
using AudioTastic.Core.Playlists;
using AudioTastic.Data;
using AudioTastic.Data.Settings;
using AudioTastic.Sdk.Audio;
using Microsoft.Extensions.Logging.Abstractions;

namespace AudioTastic.Analysis.Tests;

/// <summary>
/// P8 exit criterion (XF-02): a beat-matched transition keeps the beats within 10 ms over the whole overlap. Two
/// synthetic dance tracks (140 and 146 BPM, so the incoming one is time-stretched) are analysed with the real
/// analyser and mixed by the real engine; each deck is rendered alone and every kick in the overlap is compared.
/// </summary>
[Collection(nameof(TimingTests))] // CPU-heavy
public sealed class BeatMatchTests
{
    private const int Rate = 44_100;

    [Fact]
    public async Task Beat_matched_kicks_stay_within_10_ms_and_the_incoming_track_fades_in()
    {
        var outgoing = DanceTrack.Create(140, Rate, introBeats: 16, mainBeats: 120, breakBeats: 0, outroBeats: 16, offset: 0.2);
        var incoming = DanceTrack.Create(146, Rate, introBeats: 16, mainBeats: 120, breakBeats: 0, outroBeats: 16, offset: 0.35);
        // The engine checks that files exist: empty placeholder files stand for the in-memory tracks.
        var dir = Directory.CreateTempSubdirectory("audiotastic-tracks-").FullName;
        var outPath = Path.Combine(dir, "out.mem");
        var inPath = Path.Combine(dir, "in.mem");
        File.WriteAllBytes(outPath, []);
        File.WriteAllBytes(inPath, []);
        var beats = new Dictionary<string, BeatInfo>
        {
            [outPath] = BeatAnalysisTests.Analyze(outgoing.Samples, Rate),
            [inPath] = BeatAnalysisTests.Analyze(incoming.Samples, Rate),
        };
        var xf = new CrossfadingSettings
        {
            Enabled = true,
            Mode = CrossfadeMode.BeatMatch,
            FadeInSeconds = 2,
            OverlapSeconds = 6,
            EndBehaviour = CrossfadeEnd.Cut,
            MixBeforeBeatEndSeconds = 10,
            TempoReturn = TempoReturnMode.Gradual,
            TempoReturnSeconds = 2,
        };
        var sources = new Dictionary<string, float[]> { [outPath] = outgoing.Samples, [inPath] = incoming.Samples };
        var plan = CrossfadePlanner.Plan(xf, new CrossfadeTrack(Duration(outgoing), beats[outPath]), new CrossfadeTrack(Duration(incoming), beats[inPath]), 0);
        Assert.Equal(CrossfadeMode.BeatMatch, plan.Mode);
        Assert.InRange(plan.IncomingSpeed, 0.95, 0.97); // 146 -> 140 BPM

        var startAt = plan.MixStart - 14;
        var seconds = 14 + plan.OutgoingTail + 1;
        var outOnly = await RenderAsync(xf, sources, beats, outPath, inPath, startAt, seconds, muteIncoming: true);
        var inOnly = await RenderAsync(xf, sources, beats, outPath, inPath, startAt, seconds, muteIncoming: false);

        // Every outgoing beat in the full-volume overlap: the incoming kick must be within 10 ms of the outgoing one.
        var period = 60 / beats[outPath].Bpm;
        var from = 14 + plan.IncomingFadeLength;
        var worst = 0.0;
        var compared = 0;
        var diffs = new List<double>();
        for (var k = beats[outPath].BeatAtOrAfter(startAt + from); beats[outPath].BeatTime(k) < startAt + from + plan.Overlap - period; k++)
        {
            var expected = beats[outPath].BeatTime(k) - startAt;
            var a = KickTime(outOnly, expected, period);
            var b = KickTime(inOnly, expected, period);
            worst = Math.Max(worst, Math.Abs(b - a));
            diffs.Add(b - a);
            compared++;
        }
        Assert.True(compared >= 10, $"only {compared} beats compared");
        Assert.True(worst <= 0.010, $"worst kick misalignment {worst * 1000:F1} ms (per beat: {string.Join(" ", diffs.Select(d => (d * 1000).ToString("F1", System.Globalization.CultureInfo.InvariantCulture)))})");

        // The incoming track fades in: quiet at the mix start, full level after the fade-in.
        var early = Rms(inOnly, 14, 0.3);
        var late = Rms(inOnly, 14 + plan.IncomingFadeLength + 0.5, 0.3);
        Assert.True(early < late * 0.3, $"incoming level {early:F3} at the mix start, {late:F3} after the fade-in");
    }

    private static double Duration(DanceTrack track) => track.Samples.Length / 2.0 / Rate;

    /// <summary>
    /// The kick within a quarter beat of <paramref name="near"/> (s): where the short-term energy (1 ms windows) first
    /// reaches half its maximum. Energy, not single samples: time-stretching can duplicate a click's fine structure,
    /// but the kick's energy (what is heard) stays put.
    /// </summary>
    private static double KickTime(float[] stereo, double near, double period)
    {
        var window = Rate / 1000;
        var from = Math.Max(0, (int)((near - (period / 4)) * Rate));
        var to = Math.Min((stereo.Length / 2) - window, (int)((near + (period / 4)) * Rate));
        var energies = new List<double>();
        for (var i = from; i < to; i += window / 4)
        {
            double e = 0;
            for (var j = i; j < i + window; j++)
            {
                e += stereo[j * 2] * stereo[j * 2];
            }
            energies.Add(e);
        }
        var half = energies.Max() / 2;
        var k = energies.FindIndex(e => e >= half);
        return (from + (k * (window / 4)) + (window / 2)) / (double)Rate;
    }

    private static double Rms(float[] stereo, double at, double length)
    {
        var a = (int)(at * Rate) * 2;
        var n = (int)(length * Rate) * 2;
        double sum = 0;
        for (var i = a; i < a + n && i < stereo.Length; i++)
        {
            sum += stereo[i] * stereo[i];
        }
        return Math.Sqrt(sum / n);
    }

    private static async Task<float[]> RenderAsync(CrossfadingSettings xf, Dictionary<string, float[]> sources, Dictionary<string, BeatInfo> beats,
        string outPath, string inPath, double startAt, double seconds, bool muteIncoming)
    {
        var dir = Directory.CreateTempSubdirectory("audiotastic-beatmatch-").FullName;
        var paths = AppPaths.Resolve(new AppPathsContext(AppPathsContext.CurrentOs, dir, dir, null, null, [AppPaths.PortableSwitch])).EnsureCreated();
        var settings = new SettingsStore(paths);
        settings.Update(SettingsGroup.Playback, s =>
        {
            s.Playback.Volume = 100;
            s.Playback.Fades = new FadeSettings { OnPause = false, OnSeek = false, OnStop = false };
        });
        settings.Update(SettingsGroup.Crossfading, s => s.Crossfading = xf);
        using var device = new OfflineDevice();
        using var engine = new PlaybackEngine([new MemoryFactory(sources)], device, settings, NullLogger<PlaybackEngine>.Instance)
        {
            BeatProvider = new FixedBeats(beats),
        };
        device.Graph = engine.Graph;
        var playlist = new Playlist("mix", [new PlaylistEntry(outPath), new PlaylistEntry(inPath)]);
        await engine.ResumeSessionAsync(playlist, playlist.Entries[0], TimeSpan.FromSeconds(startAt), play: false);
        device.Capture = true;
        await engine.PauseAsync(); // resume
        var muted = false;
        var deadline = Environment.TickCount64 + 60_000;
        while (device.CapturedFrames < seconds * Rate && Environment.TickCount64 < deadline)
        {
            if (!muted && engine.Crossfade is { Mixing: false })
            {
                var playing = engine.Graph.DeckA.IsPlaying ? engine.Graph.DeckA : engine.Graph.DeckB;
                var armed = ReferenceEquals(playing, engine.Graph.DeckA) ? engine.Graph.DeckB : engine.Graph.DeckA;
                var target = muteIncoming ? armed : playing;
                target.Gain.TargetGain = 0;
                target.Gain.SnapToTarget();
                muted = true;
            }
            await Task.Delay(2, TestContext.Current.CancellationToken);
        }
        Assert.True(muted, "the crossfade was never planned");
        await engine.StopAsync();
        return device.Take();
    }

    private sealed class FixedBeats(Dictionary<string, BeatInfo> beats) : IBeatInfoProvider
    {
        public BeatInfo? TryGet(PlaylistEntry entry) => beats.GetValueOrDefault(entry.Path);
    }

    private sealed class MemoryFactory(Dictionary<string, float[]> sources) : IAudioSourceFactory
    {
        public string Name => "Memory";
        public IReadOnlyCollection<string> Extensions => [".mem"];
        public bool CanOpen(string path) => sources.ContainsKey(path);
        public IAudioSource Open(string path) => new MemorySource(sources[path]);
    }

    private sealed class MemorySource(float[] stereo) : IAudioSource
    {
        public AudioStreamInfo Info { get; } = new("pcm_f32le", Rate, 2, 32, 0, true);
        public AudioFormat Format => new(Rate, 2);
        public long? LengthFrames => stereo.Length / 2;
        public long PositionFrames { get; private set; }
        public bool CanSeek => true;
        public IReadOnlyDictionary<string, string> Tags { get; } = new Dictionary<string, string>();

        public int Read(Span<float> buffer)
        {
            var frames = (int)Math.Min(buffer.Length / 2, (stereo.Length / 2) - PositionFrames);
            stereo.AsSpan((int)(PositionFrames * 2), frames * 2).CopyTo(buffer);
            PositionFrames += frames;
            return frames;
        }

        public void Seek(long frame) => PositionFrames = Math.Clamp(frame, 0, stereo.Length / 2);

        public void Dispose()
        {
        }
    }

    /// <summary>Renders as fast as the decks can decode (never ahead of them) and keeps the audio; nothing while paused.</summary>
    private sealed class OfflineDevice : IAudioOutput
    {
        private const int Block = 256;
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
            _thread = new Thread(Loop) { IsBackground = true };
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
            var buffer = new float[Block * 2];
            while (_running)
            {
                if (Graph is { } g && (g.Paused || !g.DeckA.CanRender(Block) || !g.DeckB.CanRender(Block)))
                {
                    Thread.Sleep(1);
                    continue;
                }
                _renderer!.Render(buffer);
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
}
