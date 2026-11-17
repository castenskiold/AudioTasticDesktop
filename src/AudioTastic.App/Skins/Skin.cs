using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Media.Imaging;
using Avalonia.Svg.Skia;

namespace AudioTastic.App.Skins;

/// <summary>
/// UI-03: a validated, loaded skin. Resolves colours ('#hex', '@name', 'transparent'), fonts (the skin's font files
/// are registered as an Avalonia font collection) and images (SVG, or PNG with an optional @2x variant), caching
/// what it loads. Use on the UI thread.
/// </summary>
public sealed class Skin : IDisposable
{
    private readonly ISkinSource _source;
    private readonly Dictionary<string, SvgSource?> _svgs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Bitmap?> _bitmaps = new(StringComparer.OrdinalIgnoreCase);
    private readonly Uri _fontKey = new($"fonts:skin-{Guid.NewGuid():N}");
    private bool? _hasFonts; // registered lazily on first font use (needs a running Avalonia platform)

    internal Skin(string id, SkinDefinition definition, ISkinSource source)
    {
        Id = id;
        Definition = definition;
        _source = source;
    }

    public string Id { get; }
    public SkinDefinition Definition { get; }
    public string Name => Definition.Name;

    /// <summary>The window definition, or null when the skin does not define it.</summary>
    public SkinWindowDefinition? Window(string name) => name switch
    {
        "main" => Definition.Windows.Main,
        "mini" => Definition.Windows.Mini,
        "playlist" => Definition.Windows.Playlist,
        _ => null,
    };

    public Color Color(string? value, Color fallback)
    {
        if (string.IsNullOrEmpty(value))
        {
            return fallback;
        }
        if (value == "transparent")
        {
            return Colors.Transparent;
        }
        if (value.StartsWith('@'))
        {
            return Definition.Colors.TryGetValue(value[1..], out var named) ? Color(named, fallback) : fallback;
        }
        return Avalonia.Media.Color.TryParse(value, out var color) ? color : fallback;
    }

    public IBrush? Brush(string? value) =>
        string.IsNullOrEmpty(value) ? null : new SolidColorBrush(Color(value, Colors.Transparent));

    public FontFamily FontFamily(string? fontName)
    {
        if (fontName is null || !Definition.Fonts.TryGetValue(fontName, out var font))
        {
            return Avalonia.Media.FontFamily.Default;
        }
        _hasFonts ??= RegisterFonts();
        return _hasFonts == true && font.Files.Count > 0
            ? new FontFamily($"{_fontKey}#{font.Family}")
            : new FontFamily(font.Family);
    }

    public FontWeight FontWeight(string? fontName) =>
        fontName is not null && Definition.Fonts.TryGetValue(fontName, out var font)
            ? font.Weight switch
            {
                "light" => Avalonia.Media.FontWeight.Light,
                "medium" => Avalonia.Media.FontWeight.Medium,
                "semibold" => Avalonia.Media.FontWeight.SemiBold,
                "bold" => Avalonia.Media.FontWeight.Bold,
                _ => Avalonia.Media.FontWeight.Normal,
            }
            : Avalonia.Media.FontWeight.Normal;

    public double FontSize(SkinElement element, double fallback = 8) =>
        element.FontSize ?? (element.Font is not null && Definition.Fonts.TryGetValue(element.Font, out var f) ? f.Size : null) ?? fallback;

    public static bool IsSvg(string path) => path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase);

    /// <summary>The SVG at <paramref name="path"/>, or null if it cannot be read.</summary>
    public SvgSource? Svg(string path)
    {
        if (!_svgs.TryGetValue(path, out var svg))
        {
            try
            {
                using var stream = _source.Open(path);
                svg = SvgSource.LoadFromStream(stream, null);
            }
#pragma warning disable CA1031 // A broken image must not break the window; it is simply not drawn.
            catch (Exception)
#pragma warning restore CA1031
            {
                svg = null;
            }
            _svgs[path] = svg;
        }
        return svg;
    }

    /// <summary>The PNG at <paramref name="path"/>; the @2x variant when <paramref name="scale"/> ≥ 1.5 and it exists.</summary>
    public Bitmap? Bitmap(string path, double scale)
    {
        var hiRes = Path.ChangeExtension(path, null) + "@2x" + Path.GetExtension(path);
        var chosen = scale >= 1.5 && _source.Exists(hiRes) ? hiRes : path;
        if (!_bitmaps.TryGetValue(chosen, out var bitmap))
        {
            try
            {
                using var stream = _source.Open(chosen);
                bitmap = new Bitmap(stream);
            }
#pragma warning disable CA1031 // A broken image must not break the window; it is simply not drawn.
            catch (Exception)
#pragma warning restore CA1031
            {
                bitmap = null;
            }
            _bitmaps[chosen] = bitmap;
        }
        return bitmap;
    }

    public void Dispose()
    {
        if (_hasFonts == true)
        {
            FontManager.Current.RemoveFontCollection(_fontKey);
        }
        foreach (var bitmap in _bitmaps.Values)
        {
            bitmap?.Dispose();
        }
        _source.Dispose();
    }

    private bool RegisterFonts()
    {
        var files = Definition.Fonts.Values.SelectMany(f => f.Files).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (files.Length == 0)
        {
            return false;
        }
        var collection = new SkinFontCollection(_fontKey);
        foreach (var file in files)
        {
            using var stream = _source.Open(file);
            collection.TryAddGlyphTypeface(stream, out _);
        }
        FontManager.Current.AddFontCollection(collection);
        return true;
    }

    private sealed class SkinFontCollection(Uri key) : FontCollectionBase
    {
        public override Uri Key { get; } = key;
    }
}
