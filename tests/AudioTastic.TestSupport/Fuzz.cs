using System.Diagnostics;
using System.Text;

namespace AudioTastic.TestSupport;

/// <summary>
/// P14 fuzzing: deterministic mutations of valid inputs (seeded, so a failure can be reproduced) and a runner that
/// reports every input that made the code under test throw something other than its documented refusals, or take
/// longer than the time limit.
/// </summary>
public static class Fuzz
{
    private static readonly string[] Tokens =
    [
        "\"", "'", "<", ">", "&", "\0", "\r", "\n", "\t", "#", "=", ":", "-", "99999999999", "-1", "00:00:00", "99:99:99",
        "\u00FF\u00FE", "\uFFFD", "😀", "../../../../", "C:\\", "file:///", "http://", "<![CDATA[", "]]>", "&#xFFFF;", "%00",
        "INDEX 01 99:99:99", "TRACK 01 AUDIO", "FILE \"x.wav\" WAVE", "[99:99.99]", "[offset:-99999]", "#EXTINF:-1,",
        "File1=", "NumberOfEntries=999999", "<track>", "<location>", "<smil>", "<seq>", "<media src=\"",
    ];

    /// <summary><paramref name="count"/> mutations of <paramref name="seed"/> (the first is the seed itself).</summary>
    public static IEnumerable<byte[]> Mutations(byte[] seed, int count, int randomSeed = 1)
    {
        ArgumentNullException.ThrowIfNull(seed);
        var random = new Random(randomSeed);
        yield return seed;
        for (var i = 1; i < count; i++)
        {
            var data = new List<byte>(seed);
            var edits = 1 + random.Next(6);
            for (var e = 0; e < edits; e++)
            {
                switch (random.Next(7))
                {
                    case 0 when data.Count > 0: // flip bits
                        data[random.Next(data.Count)] ^= (byte)(1 << random.Next(8));
                        break;
                    case 1 when data.Count > 0: // random byte
                        data[random.Next(data.Count)] = (byte)random.Next(256);
                        break;
                    case 2: // cut off at a random point
                    {
                        var cut = random.Next(data.Count + 1);
                        data.RemoveRange(cut, data.Count - cut);
                        break;
                    }
                    case 3: // insert a nasty token
                        data.InsertRange(random.Next(data.Count + 1), Encoding.UTF8.GetBytes(Tokens[random.Next(Tokens.Length)]));
                        break;
                    case 4 when data.Count > 1: // repeat a slice
                    {
                        var start = random.Next(data.Count);
                        var length = Math.Min(data.Count - start, 1 + random.Next(64));
                        var slice = data.GetRange(start, length);
                        for (var r = random.Next(1, 8); r > 0; r--)
                        {
                            data.InsertRange(start, slice);
                        }
                        break;
                    }
                    case 5 when data.Count > 1: // delete a slice
                    {
                        var start = random.Next(data.Count);
                        data.RemoveRange(start, Math.Min(data.Count - start, 1 + random.Next(32)));
                        break;
                    }
                    default: // random garbage
                    {
                        var garbage = new byte[1 + random.Next(32)];
                        random.NextBytes(garbage);
                        data.InsertRange(random.Next(data.Count + 1), garbage);
                        break;
                    }
                }
            }
            yield return [.. data];
        }
    }

    /// <summary>
    /// Runs <paramref name="target"/> on every input. Exceptions of the <paramref name="refusals"/> types are the
    /// code saying "not valid" and are fine; anything else, or a call over <paramref name="limit"/>, is listed. Returns
    /// the list (empty = passed) with the input's index and its first bytes, so the case can be replayed.
    /// </summary>
    public static IReadOnlyList<string> Run(IEnumerable<byte[]> inputs, Action<byte[]> target, Type[] refusals, TimeSpan? limit = null)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(refusals);
        var max = limit ?? TimeSpan.FromSeconds(10); // catches hangs; generous because the suite runs tests in parallel
        var problems = new List<string>();
        var index = 0;
        foreach (var input in inputs)
        {
            var clock = Stopwatch.StartNew();
            try
            {
                target(input);
            }
#pragma warning disable CA1031 // Every exception is classified, not swallowed.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                if (!refusals.Any(t => t.IsInstanceOfType(ex)))
                {
                    problems.Add($"#{index}: {ex.GetType().Name}: {ex.Message} [{Preview(input)}]\n{ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}");
                }
            }
            if (clock.Elapsed > max)
            {
                problems.Add($"#{index}: took {clock.Elapsed.TotalSeconds:0.0} s [{Preview(input)}]");
            }
            index++;
        }
        return problems;
    }

    private static string Preview(byte[] input) =>
        Convert.ToHexString(input.AsSpan(0, Math.Min(24, input.Length))) + (input.Length > 24 ? $"… ({input.Length} bytes)" : string.Empty);
}
