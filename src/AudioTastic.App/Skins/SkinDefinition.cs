using System.Text.Json;
using System.Text.Json.Serialization;

namespace AudioTastic.App.Skins;

/// <summary>UI-03: skin.json, format 1 (docs/skins/skin-format.md, docs/skins/skin.schema.json).</summary>
public sealed class SkinDefinition
{
    public const int CurrentFormat = 1;

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public int Format { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Author { get; set; }
    public string? Version { get; set; }
    public string? Description { get; set; }
    public string? Preview { get; set; }
    public Dictionary<string, string> Colors { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, SkinFontDefinition> Fonts { get; set; } = new(StringComparer.Ordinal);
    public SkinWindows Windows { get; set; } = new();
}

public sealed class SkinFontDefinition
{
    public string Family { get; set; } = string.Empty;
    public List<string> Files { get; set; } = [];
    public string? Weight { get; set; }
    public double? Size { get; set; }
}

public sealed class SkinWindows
{
    public SkinWindowDefinition? Main { get; set; }
    public SkinWindowDefinition? Mini { get; set; }
    public SkinWindowDefinition? Playlist { get; set; }
}

public sealed class SkinWindowDefinition
{
    public double Width { get; set; }
    public double Height { get; set; }
    public string? Background { get; set; }
    public string? Border { get; set; }
    public double BorderWidth { get; set; }
    public double CornerRadius { get; set; }

    /// <summary>UI-01: the user can resize the window (playlist, Art &amp; Lyrics); elements use <see cref="SkinElement.Anchor"/>.</summary>
    public bool Resizable { get; set; }
    public double? MinWidth { get; set; }
    public double? MinHeight { get; set; }
    public List<SkinElement> Elements { get; set; } = [];
}

/// <summary>Visual states of buttons and toggles.</summary>
public enum SkinState
{
    Normal,
    Hover,
    Pressed,
    Active,
    Alternate,
    Disabled,
}

/// <summary>A value per <see cref="SkinState"/>; missing states fall back (alternate → active → normal).</summary>
public sealed class SkinStates
{
    public string? Normal { get; set; }
    public string? Hover { get; set; }
    public string? Pressed { get; set; }
    public string? Active { get; set; }
    public string? Alternate { get; set; }
    public string? Disabled { get; set; }

    public string? Get(SkinState state) => state switch
    {
        SkinState.Hover => Hover ?? Normal,
        SkinState.Pressed => Pressed ?? Hover ?? Normal,
        SkinState.Active => Active ?? Normal,
        SkinState.Alternate => Alternate ?? Active ?? Normal,
        SkinState.Disabled => Disabled ?? Normal,
        _ => Normal,
    };

    [JsonIgnore]
    public IEnumerable<string> All => new[] { Normal, Hover, Pressed, Active, Alternate, Disabled }.OfType<string>();
}

public sealed class SkinElement
{
    public string Type { get; set; } = string.Empty;
    public string? Bind { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public string? Tooltip { get; set; }
    public string? Text { get; set; }
    public string? Fill { get; set; }
    public string? Stroke { get; set; }
    public double? StrokeWidth { get; set; }
    public double? CornerRadius { get; set; }
    public string? Image { get; set; }
    public SkinStates? Images { get; set; }
    public double Padding { get; set; }
    public string? Color { get; set; }
    public SkinStates? Colors { get; set; }
    public SkinStates? Fills { get; set; }
    public SkinStates? Strokes { get; set; }
    public string? Font { get; set; }
    public double? FontSize { get; set; }
    public double? LetterSpacing { get; set; }
    public string? Align { get; set; }
    public string? TrackColor { get; set; }
    public string? FillColor { get; set; }
    public string? ThumbColor { get; set; }
    public double? ThumbSize { get; set; }
    public double? TrackThickness { get; set; }
    public double? Speed { get; set; }
    public double? Spacing { get; set; }

    /// <summary>Which window edges the element keeps its distance to: "left top" (default), "left right top bottom" stretches.</summary>
    public string? Anchor { get; set; }

    /// <summary>Playlist list: row height, and colours for selection, the playing entry and secondary text.</summary>
    public double? RowHeight { get; set; }
    public string? SelectionColor { get; set; }
    public string? PlayingColor { get; set; }
    public string? MutedColor { get; set; }

    /// <summary>Image for <paramref name="state"/>: per-state images first, then <see cref="Image"/>.</summary>
    public string? ImageFor(SkinState state) => Images?.Get(state) ?? Image;

    public override string ToString() => Bind is null ? Type : $"{Type}:{Bind}";
}
