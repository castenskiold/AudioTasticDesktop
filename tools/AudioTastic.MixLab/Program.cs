using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using AudioTastic.Analysis;
using AudioTastic.Core.Audio.Beats;
using AudioTastic.Input.Ffmpeg;

// P8 offline harness.
//   analyze <folder or files...> [--labels labels.json]
//       Beat analysis of each file. labels.json: { "<file name>": { "bpm": 145.0, "firstBeat": 0.12 } } (firstBeat optional).
//       BPM counts as correct within 0.5 BPM, also at half or double tempo (exit criterion: >= 90 % correct).
//   mix ... (added with the crossfade engine)
var culture = CultureInfo.InvariantCulture;
if (args.Length == 0)
{
    Console.WriteLine("usage: analyze <folder|files...> [--labels labels.json]");
    return 2;
}
switch (args[0])
{
    case "analyze":
        return Analyze(args[1..]);
    default:
        Console.Error.WriteLine($"unknown command {args[0]}");
        return 2;
}

int Analyze(string[] rest)
{
    var labelsPath = Option(ref rest, "--labels");
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
        var info = AnalyzeFile(file);
        var name = Path.GetFileName(file);
        var line = string.Create(culture,
            $"{name,-45} {info.Bpm,8:0.000} BPM  first beat {info.FirstBeat,7:0.000} s  confidence {info.Confidence,5:0.00}  phrase +{info.PhraseOffset}  " +
            $"beat {info.BeatStart:0.0}-{info.BeatEnd:0.0} s  " +
            $"breaks {string.Join(", ", info.Breaks.Select(b => $"{b.Start:0.0}-{b.End:0.0}"))}  ({timer.Elapsed.TotalSeconds:0.0} s)");
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

static BeatInfo AnalyzeFile(string path)
{
    using var source = new FfmpegSourceFactory().Open(path);
    var run = new BeatAnalyzer.Run(source.Format);
    var buffer = new float[8192 * source.Format.Channels];
    int frames;
    while ((frames = source.Read(buffer)) > 0)
    {
        run.Process(buffer.AsSpan(0, frames * source.Format.Channels));
    }
    return run.Detect();
}

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

internal sealed record Label(double Bpm, double? FirstBeat);
