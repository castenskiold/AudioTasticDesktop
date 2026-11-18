using System.IO.Compression;
using System.Text.Json.Nodes;
using AudioTastic.App.Skins;
using AudioTastic.Data;
using AudioTastic.Data.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace AudioTastic.App.Tests;

/// <summary>UI-03 skin engine and UI-02 default skin.</summary>
public sealed class SkinTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("audiotastic-skins-").FullName;
    private readonly AppPaths _paths;
    private readonly SettingsStore _settings;

    public SkinTests()
    {
        _paths = AppPaths.Resolve(new AppPathsContext(AppPathsContext.CurrentOs, _dir, _dir, null, null, [AppPaths.PortableSwitch])).EnsureCreated();
        _settings = new SettingsStore(_paths);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private SkinManager Manager() => new(_paths, _settings, NullLogger<SkinManager>.Instance);

    private static JsonObject DefaultSkinJson()
    {
        using var source = new EmbeddedSkinSource(typeof(SkinManager).Assembly);
        using var reader = new StreamReader(source.Open("skin.json"));
        return JsonNode.Parse(reader.ReadToEnd())!.AsObject();
    }

    /// <summary>Writes a skin folder: the default skin's files with <paramref name="edit"/> applied to skin.json.</summary>
    private string WriteSkinFolder(string name, Action<JsonObject>? edit = null, bool includeImages = true)
    {
        var folder = Path.Combine(_dir, "src", name);
        using var builtIn = new EmbeddedSkinSource(typeof(SkinManager).Assembly);
        var json = DefaultSkinJson();
        edit?.Invoke(json);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "skin.json"), json.ToJsonString());
        if (includeImages)
        {
            foreach (var file in builtIn.Files.Where(f => f != "skin.json"))
            {
                var target = Path.Combine(folder, file);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var input = builtIn.Open(file);
                using var output = File.Create(target);
                input.CopyTo(output);
            }
        }
        return folder;
    }

    private string Pack(string folder)
    {
        var file = Path.Combine(_dir, Path.GetFileName(folder) + ".atskin");
        ZipFile.CreateFromDirectory(folder, file);
        return file;
    }

    [Fact]
    public void Default_skin_is_valid_and_has_main_and_mini_windows()
    {
        using var source = new EmbeddedSkinSource(typeof(SkinManager).Assembly);

        var skin = SkinLoader.Load(source);

        Assert.Equal("AudioTastic", skin.Name);
        Assert.Equal(330, skin.Windows.Main!.Width);
        Assert.NotNull(skin.Windows.Mini);
        Assert.Contains(skin.Windows.Main.Elements, e => e.Bind == "play");
        Assert.Contains(skin.Windows.Mini!.Elements, e => e.Bind == "expand");
    }

    [Fact]
    public void Default_skin_has_an_equalizer_window_with_preamp_and_ten_bands()
    {
        using var source = new EmbeddedSkinSource(typeof(SkinManager).Assembly);

        var eq = SkinLoader.Load(source).Windows.Equalizer;

        Assert.NotNull(eq);
        Assert.Equal(["preamp", .. Enumerable.Range(0, 10).Select(i => $"band{i}")], eq.Elements.Where(e => e.Type == "eqSlider").Select(e => e.Bind));
        Assert.Contains(eq.Elements, e => e.Type == "eqCurve");
        Assert.Contains(eq.Elements, e => e.Type == "eqParametric");
        Assert.Equal(["eqOn", "eqAuto", "eqParam"], eq.Elements.Where(e => e.Type == "toggle").Select(e => e.Bind));
    }

    [Fact]
    public void Equalizer_slider_needs_a_known_bind()
    {
        var folder = WriteSkinFolder("bad-eq", json =>
        {
            var elements = json["windows"]!["equalizer"]!["elements"]!.AsArray();
            elements.Add(JsonNode.Parse("""{ "type": "eqSlider", "bind": "band10", "x": 0, "y": 0, "width": 10, "height": 10 }"""));
        });

        var ex = Assert.Throws<SkinException>(() => SkinLoader.Load(new DirectorySkinSource(folder)));
        Assert.Contains(ex.Errors, e => e.Contains("bind must be one of preamp", StringComparison.Ordinal));
    }

    [Fact]
    public void Default_skin_resolves_named_colours()
    {
        using var skin = Manager().Load(SkinManager.DefaultSkinId);

        Assert.Equal(Avalonia.Media.Color.Parse("#0B0B0C"), skin.Color("@bg", Avalonia.Media.Colors.Red));
        Assert.Equal(Avalonia.Media.Colors.Transparent, skin.Color("transparent", Avalonia.Media.Colors.Red));
        Assert.Equal(Avalonia.Media.Colors.Red, skin.Color("@missing", Avalonia.Media.Colors.Red));
    }

    [Fact]
    public void Missing_skin_json_is_reported()
    {
        var folder = Path.Combine(_dir, "empty");
        Directory.CreateDirectory(folder);

        var ex = Assert.Throws<SkinException>(() => SkinLoader.Load(new DirectorySkinSource(folder)));
        Assert.Contains("skin.json is missing", ex.Errors[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Invalid_json_is_reported()
    {
        var folder = Path.Combine(_dir, "broken");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "skin.json"), "{ \"format\": 1, ");

        var ex = Assert.Throws<SkinException>(() => SkinLoader.Load(new DirectorySkinSource(folder)));
        Assert.Contains("not valid JSON", ex.Errors[0], StringComparison.Ordinal);
    }

    public static TheoryData<string, string> SchemaViolations => new()
    {
        { "format", """{ "format": 2 }""" },
        { "unknown property", """{ "unexpected": true }""" },
        { "missing name", """{ "name": null }""" },
        { "bad colour", """{ "colors": { "bg": "black" } }""" },
        { "unknown element type", """{ "windows": { "main": { "width": 330, "height": 128, "elements": [ { "type": "wobble", "x": 0, "y": 0, "width": 1, "height": 1 } ] } } }""" },
        { "element without size", """{ "windows": { "main": { "width": 330, "height": 128, "elements": [ { "type": "rect", "x": 0, "y": 0 } ] } } }""" },
        { "image outside package syntax", """{ "windows": { "main": { "width": 330, "height": 128, "elements": [ { "type": "image", "image": "C:\\evil.exe", "x": 0, "y": 0, "width": 1, "height": 1 } ] } } }""" },
    };

    [Theory]
    [MemberData(nameof(SchemaViolations))]
    public void Schema_violations_are_reported(string violation, string patch)
    {
        var folder = WriteSkinFolder("schema-" + violation.Replace(' ', '-'), json =>
        {
            foreach (var (key, value) in JsonNode.Parse(patch)!.AsObject())
            {
                if (value is null)
                {
                    json.Remove(key);
                }
                else
                {
                    json[key] = value.DeepClone();
                }
            }
        });

        var ex = Assert.Throws<SkinException>(() => SkinLoader.Load(new DirectorySkinSource(folder)));

        Assert.NotEmpty(ex.Errors);
    }

    [Fact]
    public void Missing_files_unknown_colours_fonts_and_bad_binds_are_reported()
    {
        var folder = WriteSkinFolder("refs", json =>
        {
            var elements = json["windows"]!["main"]!["elements"]!.AsArray();
            elements.Add(JsonNode.Parse("""{ "type": "button", "bind": "explode", "x": 0, "y": 0, "width": 1, "height": 1 }"""));
            elements.Add(JsonNode.Parse("""{ "type": "image", "image": "images/nope.svg", "x": 0, "y": 0, "width": 1, "height": 1 }"""));
            elements.Add(JsonNode.Parse("""{ "type": "rect", "fill": "@nocolour", "x": 0, "y": 0, "width": 1, "height": 1 }"""));
            elements.Add(JsonNode.Parse("""{ "type": "text", "font": "nofont", "text": "x", "x": 0, "y": 0, "width": 1, "height": 1 }"""));
        });

        var ex = Assert.Throws<SkinException>(() => SkinLoader.Load(new DirectorySkinSource(folder)));

        Assert.Contains(ex.Errors, e => e.Contains("bind must be one of", StringComparison.Ordinal));
        Assert.Contains(ex.Errors, e => e.Contains("images/nope.svg", StringComparison.Ordinal));
        Assert.Contains(ex.Errors, e => e.Contains("@nocolour", StringComparison.Ordinal));
        Assert.Contains(ex.Errors, e => e.Contains("nofont", StringComparison.Ordinal));
    }

    [Fact]
    public void Paths_cannot_escape_a_skin_folder()
    {
        var folder = WriteSkinFolder("escape");
        var source = new DirectorySkinSource(folder);

        Assert.False(source.Exists("../../secret.svg"));
        Assert.Throws<FileNotFoundException>(() => source.Open("../skin.json"));
    }

    [Fact]
    public void Install_validates_and_lists_the_skin_and_apply_switches_and_saves()
    {
        var package = Pack(WriteSkinFolder("Neon", json => json["name"] = "Neon"));
        var manager = Manager();
        Skin? changed = null;
        manager.SkinChanged += (_, e) => changed = e.Skin;

        var id = manager.Install(package);
        var skins = manager.GetInstalledSkins();
        manager.Apply(id);

        Assert.Equal("Neon.atskin", id);
        Assert.Contains(skins, s => s.Id == id && s.Name == "Neon");
        Assert.Contains(skins, s => s.IsDefault);
        Assert.Equal("Neon", manager.Current.Name);
        Assert.Same(manager.Current, changed);
        Assert.Equal(id, new SettingsStore(_paths).Current.Interface.Skin);
    }

    [Fact]
    public void Invalid_package_is_not_installed()
    {
        var package = Pack(WriteSkinFolder("Bad", includeImages: false));

        var ex = Assert.Throws<SkinException>(() => Manager().Install(package));

        Assert.NotEmpty(ex.Errors);
        Assert.False(File.Exists(Path.Combine(_paths.SkinsFolder, "Bad.atskin")));
    }

    [Fact]
    public void Apply_of_an_invalid_skin_keeps_the_current_skin()
    {
        var folder = WriteSkinFolder("Half", includeImages: false);
        Directory.Move(folder, Path.Combine(_paths.SkinsFolder, "Half"));
        var manager = Manager();
        var before = manager.Current;

        Assert.Throws<SkinException>(() => manager.Apply("Half"));

        Assert.Same(before, manager.Current);
        Assert.Equal(SkinManager.DefaultSkinId, new SettingsStore(_paths).Current.Interface.Skin);
    }

    [Fact]
    public void Broken_configured_skin_falls_back_to_the_default_at_startup()
    {
        _settings.Update(SettingsGroup.Interface, s => s.Interface.Skin = "Gone.atskin");

        var manager = Manager();

        Assert.Equal(SkinManager.DefaultSkinId, manager.Current.Id);
        Assert.NotEmpty(manager.StartupErrors);
    }

    [Fact]
    public void Unsafe_skin_names_are_rejected()
    {
        Assert.Throws<SkinException>(() => Manager().Load("../outside"));
    }
}
