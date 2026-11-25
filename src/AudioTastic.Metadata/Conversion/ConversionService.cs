using AudioTastic.Core.Audio;
using AudioTastic.Core.Playback;
using AudioTastic.Data.Settings;
using AudioTastic.Sdk.Audio;
using AudioTastic.Sdk.Metadata;
using Microsoft.Extensions.Logging;

namespace AudioTastic.Metadata.Conversion;

/// <summary>TL-01: one track to convert: a file, or part of one (a CUE or CD track) with its own tags.</summary>
/// <param name="Path">The original.</param>
/// <param name="Start">Start of a part of the file (CUE track), or null.</param>
/// <param name="End">End of the part, or null for the end of the file.</param>
/// <param name="Tags">Tags that win over the file's own (a CUE track's title and number; a CD track's lookup).</param>
public sealed record ConversionItem(string Path, TimeSpan? Start = null, TimeSpan? End = null, TrackTags? Tags = null)
{
    public bool IsPart => Start is not null;
}

/// <summary>TL-01: how to convert.</summary>
/// <param name="Encode">Format and quality. <c>BitsPerSample</c> 0 = like the source.</param>
/// <param name="Destination">The output folder; null = next to each original.</param>
/// <param name="Pattern">File name pattern (<see cref="ConversionNaming"/>).</param>
public sealed record ConversionOptions(EncodeSettings Encode, string? Destination, string Pattern,
    ExistingFileAction IfExists = ExistingFileAction.Skip, bool CopyArt = true, int ParallelJobs = 0)
{
    /// <summary>The Convert dialog's saved choices as options.</summary>
    public static ConversionOptions From(ConversionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var format = Enum.TryParse<EncodeFormat>(settings.Format, ignoreCase: true, out var f) ? f : EncodeFormat.Flac;
        var bitrate = format switch
        {
            EncodeFormat.Mp3 => settings.Mp3BitrateKbps,
            EncodeFormat.Aac => settings.AacBitrateKbps,
            EncodeFormat.Opus => settings.OpusBitrateKbps,
            EncodeFormat.Vorbis => settings.VorbisBitrateKbps,
            _ => 320,
        };
        var encode = new EncodeSettings(format, settings.BitsPerSample, bitrate) { Vbr = settings.Mp3Vbr, VbrQuality = settings.Mp3VbrQuality };
        var destination = settings.SameFolder ? null
            : string.IsNullOrWhiteSpace(settings.Destination) ? Environment.GetFolderPath(Environment.SpecialFolder.MyMusic) : settings.Destination;
        return new ConversionOptions(encode, destination, settings.Pattern, settings.IfExists, settings.CopyArt, settings.ParallelJobs);
    }
}

public enum ConversionState
{
    Waiting,
    Running,
    Done,
    Skipped,
    Failed,
    Cancelled,
}

/// <summary>TL-01: one file of a batch. Properties change on worker threads.</summary>
public sealed class ConversionJob
{
    private volatile ConversionState _state;
    private double _progress;

    internal ConversionJob(ConversionItem item) => Item = item;

    public ConversionItem Item { get; }

    public ConversionState State
    {
        get => _state;
        internal set => _state = value;
    }

    /// <summary>0–1 while running.</summary>
    public double Progress
    {
        get => Volatile.Read(ref _progress);
        internal set => Volatile.Write(ref _progress, value);
    }

    /// <summary>The written file (also set for skipped jobs: the file that was already there).</summary>
    public string? Output { get; internal set; }

    /// <summary>Why the job failed or was skipped.</summary>
    public string? Message { get; internal set; }

    public bool IsFinished => State is ConversionState.Done or ConversionState.Skipped or ConversionState.Failed or ConversionState.Cancelled;
}

/// <summary>TL-01: a running batch. <see cref="JobChanged"/> fires on worker threads when a job's state changes.</summary>
public sealed class ConversionBatch : IDisposable
{
    private readonly CancellationTokenSource _cancel = new();

    internal ConversionBatch(IReadOnlyList<ConversionJob> jobs) => Jobs = jobs;

    public IReadOnlyList<ConversionJob> Jobs { get; }

    /// <summary>Completes when every job has finished (never faults).</summary>
    public Task Completion { get; internal set; } = Task.CompletedTask;

    public event EventHandler<ConversionJob>? JobChanged;

    internal CancellationToken Token => _cancel.Token;

    /// <summary>Stops the running jobs (their partial files are removed) and skips the waiting ones.</summary>
    public void Cancel() => _cancel.Cancel();

    internal void Raise(ConversionJob job) => JobChanged?.Invoke(this, job);

    public void Dispose() => _cancel.Dispose();
}

/// <summary>
/// TL-01: converts files with the decoder and encoder plugins: decode (a part for CUE/CD tracks) → resample or
/// downmix when the encoder needs it → encode into a temporary file → copy tags and cover → rename into place.
/// Several files run in parallel. Also used by CD ripping (FM-11).
/// </summary>
public sealed class ConversionService(IEnumerable<IAudioSourceFactory> decoders, IEnumerable<IAudioEncoderFactory> encoders, TagService tags,
    ArtService art, ILogger<ConversionService> logger)
{
    private const int ChunkFrames = 8192;
    private readonly Lock _namesLock = new();
    private readonly HashSet<string> _reserved = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    /// <summary>The formats the installed encoders can write.</summary>
    public IReadOnlyCollection<EncodeFormat> Formats => [.. encoders.SelectMany(e => e.Formats).Distinct().Order()];

    /// <summary>Starts converting <paramref name="items"/>; returns at once.</summary>
    public ConversionBatch Start(IEnumerable<ConversionItem> items, ConversionOptions options)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(options);
        var batch = new ConversionBatch([.. items.Select(i => new ConversionJob(i))]);
        var parallel = options.ParallelJobs > 0 ? options.ParallelJobs : Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
        logger.LogInformation("Converting {Count} file(s) to {Format}, {Parallel} at a time", batch.Jobs.Count, options.Encode.Format, parallel);
        batch.Completion = Task.Run(async () =>
        {
            try
            {
                await Parallel.ForEachAsync(batch.Jobs, new ParallelOptions { MaxDegreeOfParallelism = parallel },
                    (job, _) =>
                    {
                        Run(job, options, batch);
                        return ValueTask.CompletedTask;
                    }).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Completion never faults; each job reports its own failure.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                logger.LogError(ex, "Conversion batch failed");
            }
        });
        return batch;
    }

    /// <summary>Converts one item synchronously (tests and the CD ripper's own queue).</summary>
    public ConversionJob Convert(ConversionItem item, ConversionOptions options, CancellationToken cancellationToken = default)
    {
        using var batch = new ConversionBatch([new ConversionJob(item)]);
        using var link = cancellationToken.Register(batch.Cancel);
        Run(batch.Jobs[0], options, batch);
        return batch.Jobs[0];
    }

    /// <summary>The tags used for naming and writing: the item's own first, then the file's.</summary>
    public TrackTags? TagsFor(ConversionItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var file = item.Path.Contains("://", StringComparison.Ordinal) ? null : tags.Read(item.Path);
        return item.Tags?.Merge(file) ?? file;
    }

    private static void Set(ConversionJob job, ConversionBatch batch, ConversionState state, string? message = null)
    {
        job.State = state;
        job.Message = message;
        batch.Raise(job);
    }

    private void Run(ConversionJob job, ConversionOptions options, ConversionBatch batch)
    {
        var item = job.Item;
        if (batch.Token.IsCancellationRequested)
        {
            Set(job, batch, ConversionState.Cancelled);
            return;
        }
        Set(job, batch, ConversionState.Running);
        string? temp = null;
        string? output = null;
        IAudioSource? source = null;
        try
        {
            var encoder = encoders.FirstOrDefault(e => e.Formats.Contains(options.Encode.Format))
                ?? throw new AudioSourceException($"No encoder for {options.Encode.Format} is installed.");
            var trackTags = TagsFor(item);
            var opened = Open(item.Path) ?? throw new AudioSourceException("No decoder can open this file.");
            source = item.IsPart ? new RangeAudioSource(opened, item.Start!.Value, item.End) : opened; // each wrapper disposes what it wraps
            var settings = options.Encode.BitsPerSample > 0 ? options.Encode
                : options.Encode with { BitsPerSample = opened.Info.BitsPerSample > 16 ? 24 : 16 };
            var target = encoder.InputFormatFor(settings, source.Format);
            if (target.SampleRate != source.Format.SampleRate)
            {
                source = new ResamplingSource(source, target.SampleRate);
            }

            output = Reserve(item, trackTags, options, out var skip);
            job.Output = output;
            if (skip)
            {
                Set(job, batch, ConversionState.Skipped, "Already exists.");
                return;
            }
            var folder = Path.GetDirectoryName(output)!;
            Directory.CreateDirectory(folder);
            temp = Path.Combine(folder, $".{Path.GetFileNameWithoutExtension(output)}.{Guid.NewGuid():N}.converting{Path.GetExtension(output)}");

            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16))
            using (var encode = encoder.Create(settings, target, file))
            {
                var channels = source.Format.Channels;
                var buffer = new float[ChunkFrames * channels];
                var mapped = channels == target.Channels ? null : new float[ChunkFrames * target.Channels];
                var total = source.LengthFrames ?? 0;
                long done = 0;
                int frames;
                while ((frames = source.Read(buffer)) > 0)
                {
                    batch.Token.ThrowIfCancellationRequested();
                    if (mapped is null)
                    {
                        encode.Write(buffer.AsSpan(0, frames * channels));
                    }
                    else
                    {
                        ChannelMapper.Map(buffer, channels, mapped, target.Channels, frames);
                        encode.Write(mapped.AsSpan(0, frames * target.Channels));
                    }
                    done += frames;
                    job.Progress = total > 0 ? Math.Min(1.0, (double)done / total) : 0;
                }
                encode.Finish();
            }
            TagCopier.Copy(item, temp, trackTags, options.CopyArt ? art.Find(item.Path)?.Picture : null, options.CopyArt, logger);
            File.Move(temp, output, overwrite: true);
            temp = null;
            job.Progress = 1;
            logger.LogInformation("Converted {Source} to {Output}", item.Path, output);
            Set(job, batch, ConversionState.Done);
        }
        catch (OperationCanceledException)
        {
            Set(job, batch, ConversionState.Cancelled);
        }
        catch (Exception ex) when (ex is AudioSourceException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Could not convert {Source}", item.Path);
            Set(job, batch, ConversionState.Failed, ex.Message);
        }
        finally
        {
            source?.Dispose();
            if (temp is not null)
            {
                try
                {
                    File.Delete(temp);
                }
                catch (IOException)
                {
                }
            }
            if (output is not null)
            {
                lock (_namesLock)
                {
                    _reserved.Remove(output);
                }
            }
        }
    }

    /// <summary>
    /// The output path for an item, reserved so parallel jobs never write the same name. <paramref name="skip"/> is
    /// set when the file exists and the rule is Skip. Never the original itself.
    /// </summary>
    private string Reserve(ConversionItem item, TrackTags? trackTags, ConversionOptions options, out bool skip)
    {
        var folder = options.Destination ?? Path.GetDirectoryName(Path.GetFullPath(item.Path)) ?? ".";
        var name = Path.Combine(folder, ConversionNaming.Render(options.Pattern, trackTags, item.Path));
        var extension = ConversionNaming.ExtensionOf(options.Encode.Format);
        var source = item.Path.Contains("://", StringComparison.Ordinal) ? null : Path.GetFullPath(item.Path);
        lock (_namesLock)
        {
            skip = false;
            for (var n = 1; ; n++)
            {
                var candidate = Path.GetFullPath(n == 1 ? name + extension : $"{name} ({n}){extension}");
                var isSource = source is not null && string.Equals(candidate, source, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
                var taken = _reserved.Contains(candidate) || File.Exists(candidate) || isSource;
                if (!taken)
                {
                    _reserved.Add(candidate);
                    return candidate;
                }
                if (isSource)
                {
                    if (options.IfExists != ExistingFileAction.Number)
                    {
                        throw new InvalidOperationException("The converted file would replace the original. Choose another folder or name pattern.");
                    }
                    continue;
                }
                switch (options.IfExists)
                {
                    case ExistingFileAction.Skip when !_reserved.Contains(candidate):
                        skip = true;
                        return candidate;
                    case ExistingFileAction.Overwrite when !_reserved.Contains(candidate):
                        _reserved.Add(candidate);
                        return candidate;
                    case ExistingFileAction.Number:
                        continue;
                    default:
                        continue; // another job of this batch has the name (two tracks with the same tags): number it
                }
            }
        }
    }

    private IAudioSource? Open(string path)
    {
        foreach (var factory in decoders.Where(f => f.CanOpen(path)))
        {
            try
            {
                return factory.Open(path);
            }
            catch (AudioSourceException ex)
            {
                logger.LogDebug(ex, "{Factory} could not open {Path} for conversion", factory.Name, path);
            }
        }
        return null;
    }
}
