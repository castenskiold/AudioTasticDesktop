using System.Text.Json;
using System.Text.RegularExpressions;
using NJsonSchema;

namespace AudioTastic.App.Skins;

/// <summary>A skin that cannot be used, with every problem found.</summary>
public sealed class SkinException(string location, IReadOnlyList<string> errors)
    : Exception($"Skin '{location}' is invalid: {string.Join("; ", errors)}")
{
    public string Location { get; } = location;
    public IReadOnlyList<string> Errors { get; } = errors;
}

/// <summary>
/// UI-03: reads skin.json from an <see cref="ISkinSource"/>, validates it against docs/skins/skin.schema.json
/// (embedded) and checks what the schema cannot: referenced files exist, colour and font names resolve, and
/// each element's <c>bind</c> is valid for its type.
/// </summary>
public static partial class SkinLoader
{
    public const string SkinFile = "skin.json";

    /// <summary>Allowed <c>bind</c> values per element type; types not listed take no bind.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Binds = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["button"] = ["previous", "play", "pause", "playPause", "stop", "next", "open", "menu", "minimize", "close", "miniPlayer", "expand",
            "hide", "plAdd", "plRemove", "plSelect", "plSort", "plList", "eqPresets", "eqDsp"],
        ["toggle"] = ["shuffle", "repeat", "mute", "equalizer", "playlist", "crossfade", "cast", "eqOn", "eqAuto", "eqParam"],
        ["slider"] = ["volume", "seek"],
        ["eqSlider"] = ["preamp", "band0", "band1", "band2", "band3", "band4", "band5", "band6", "band7", "band8", "band9"],
    };

    /// <summary>Element types whose <c>bind</c> is optional, with the allowed values.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> OptionalBinds = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["time"] = ["duration"],
        ["indicators"] = ["state"], // P6: the second row with OUT, speed, A-B, STOP AFTER, SLEEP
    };

    private static readonly Lazy<JsonSchema> Schema = new(() =>
    {
        using var stream = typeof(SkinLoader).Assembly.GetManifestResourceStream("AudioTastic.Skins.skin.schema.json")
            ?? throw new InvalidOperationException("Embedded skin schema missing.");
        using var reader = new StreamReader(stream);
        return JsonSchema.FromJsonAsync(reader.ReadToEnd()).GetAwaiter().GetResult();
    });

    /// <summary>Loads and validates; throws <see cref="SkinException"/> listing all problems.</summary>
    public static SkinDefinition Load(ISkinSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.Exists(SkinFile))
        {
            throw new SkinException(source.Location, [$"{SkinFile} is missing at the root of the skin."]);
        }
        string json;
        using (var reader = new StreamReader(source.Open(SkinFile)))
        {
            json = reader.ReadToEnd();
        }

        var errors = new List<string>();
        try
        {
            errors.AddRange(Schema.Value.Validate(json).Select(e => $"{(string.IsNullOrEmpty(e.Path) ? "#" : e.Path)}: {e.Kind}"));
        }
        catch (Newtonsoft.Json.JsonException ex)
        {
            throw new SkinException(source.Location, [$"{SkinFile} is not valid JSON: {ex.Message}"]);
        }
        if (errors.Count > 0)
        {
            throw new SkinException(source.Location, errors);
        }

        SkinDefinition definition;
        try
        {
            definition = JsonSerializer.Deserialize<SkinDefinition>(json, SkinDefinition.JsonOptions)
                ?? throw new SkinException(source.Location, [$"{SkinFile} is empty."]);
        }
        catch (JsonException ex)
        {
            throw new SkinException(source.Location, [$"{SkinFile}: {ex.Message}"]);
        }

        CheckReferences(definition, source, errors);
        if (errors.Count > 0)
        {
            throw new SkinException(source.Location, errors);
        }
        return definition;
    }

    private static void CheckReferences(SkinDefinition skin, ISkinSource source, List<string> errors)
    {
        void File(string? path, string where)
        {
            if (path is null || source.Exists(path))
            {
                return;
            }
            errors.Add($"{where}: file '{path}' not found");
        }

        void Color(string? value, string where)
        {
            if (value is not null && value.StartsWith('@') && !skin.Colors.ContainsKey(value[1..]))
            {
                errors.Add($"{where}: colour '{value}' is not defined in 'colors'");
            }
        }

        void States(SkinStates? states, string where, Action<string?, string> check)
        {
            foreach (var value in states?.All ?? [])
            {
                check(value, where);
            }
        }

        File(skin.Preview, "preview");
        foreach (var (name, font) in skin.Fonts)
        {
            foreach (var file in font.Files)
            {
                File(file, $"fonts.{name}");
            }
        }

        foreach (var (windowName, window) in new[] { ("main", skin.Windows.Main), ("mini", skin.Windows.Mini), ("playlist", skin.Windows.Playlist), ("equalizer", skin.Windows.Equalizer), ("art", skin.Windows.Art) })
        {
            if (window is null)
            {
                continue;
            }
            Color(window.Background, $"windows.{windowName}.background");
            Color(window.Border, $"windows.{windowName}.border");
            for (var i = 0; i < window.Elements.Count; i++)
            {
                var e = window.Elements[i];
                var where = $"windows.{windowName}.elements[{i}] ({e})";
                if (Binds.TryGetValue(e.Type, out var allowed))
                {
                    if (e.Bind is null || !allowed.Contains(e.Bind, StringComparer.Ordinal))
                    {
                        errors.Add($"{where}: bind must be one of {string.Join(", ", allowed)}");
                    }
                }
                else if (OptionalBinds.TryGetValue(e.Type, out var optional))
                {
                    if (e.Bind is not null && !optional.Contains(e.Bind, StringComparer.Ordinal))
                    {
                        errors.Add($"{where}: bind must be empty or one of {string.Join(", ", optional)}");
                    }
                }
                else if (e.Bind is not null)
                {
                    errors.Add($"{where}: '{e.Type}' elements take no bind");
                }
                if (e.Type == "image" && e.Image is null && e.Images is null)
                {
                    errors.Add($"{where}: image elements need 'image'");
                }
                if (e.Font is not null && !skin.Fonts.ContainsKey(e.Font))
                {
                    errors.Add($"{where}: font '{e.Font}' is not defined in 'fonts'");
                }
                File(e.Image, where);
                States(e.Images, where, File);
                foreach (var c in new[] { e.Fill, e.Stroke, e.Color, e.TrackColor, e.FillColor, e.ThumbColor, e.SelectionColor, e.PlayingColor, e.MutedColor })
                {
                    Color(c, where);
                }
                States(e.Colors, where, Color);
                States(e.Fills, where, Color);
                States(e.Strokes, where, Color);
            }
        }
    }

    /// <summary>Safe file/folder names for installed skins.</summary>
    [GeneratedRegex(@"^[\w\- .()]+$")]
    internal static partial Regex SafeName();
}
