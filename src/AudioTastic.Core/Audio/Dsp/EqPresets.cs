using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AudioTastic.Data.Settings;

namespace AudioTastic.Core.Audio.Dsp;

/// <summary>EQ-02: a named equalizer setting.</summary>
public sealed record EqPreset(string Name, EqualizerSettings Settings)
{
    [JsonIgnore]
    public bool IsBuiltIn { get; init; }
}

/// <summary>
/// EQ-02: built-in presets plus user presets (presets/equalizer.json in the data folder), and import of Winamp
/// .eqf preset files and EQ library files (.q1). Thread-safe.
/// </summary>
public sealed class EqPresetStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    private readonly string _file;
    private readonly Lock _lock = new();
    private List<EqPreset>? _user;

    public EqPresetStore(string presetsFolder)
    {
        ArgumentException.ThrowIfNullOrEmpty(presetsFolder);
        _file = Path.Combine(presetsFolder, "equalizer.json");
    }

    public static IReadOnlyList<EqPreset> BuiltIn { get; } =
    [
        Graphic("Flat", 0, 0, 0, 0, 0, 0, 0, 0, 0, 0),
        Graphic("Classical", 0, 0, 0, 0, 0, 0, -4.5, -4.5, -4.5, -6),
        Graphic("Club", 0, 0, 5, 3, 3, 3, 2, 0, 0, 0),
        Graphic("Dance", 6, 4.5, 1.5, 0, 0, -3.5, -4.5, -4.5, 0, 0),
        Graphic("Full bass", 6, 6, 6, 3.5, 1, -2.5, -5, -6, -6.5, -6.5),
        Graphic("Full bass and treble", 4.5, 3.5, 0, -4.5, -3, 1, 5, 6.5, 7, 7),
        Graphic("Full treble", -6, -6, -6, -2.5, 1.5, 6.5, 9.5, 9.5, 9.5, 10),
        Graphic("Headphones", 3, 6.5, 3.5, -2, -1.5, 1, 3, 6, 8, 9),
        Graphic("Live", -3, 0, 2.5, 3, 3, 3, 2.5, 1.5, 1.5, 1.5),
        Graphic("Pop", -1, 3, 4.5, 5, 3.5, 0, -1.5, -1.5, -1, -1),
        Graphic("Reggae", 0, 0, -0.5, -4, 0, 4, 4, 0, 0, 0),
        Graphic("Rock", 5, 3, -3.5, -5, -2, 2.5, 5.5, 7, 7, 7),
        Graphic("Soft", 3, 1, -1, -1.5, -1, 1, 4.5, 5.5, 6.5, 7),
        Graphic("Vocal", -2, -3, -3, 1.5, 4, 4, 3, 1.5, 0, -1.5),
    ];

    /// <summary>Raised after user presets changed.</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<EqPreset> User
    {
        get
        {
            lock (_lock)
            {
                return [.. Load()];
            }
        }
    }

    /// <summary>User presets first, then built-in ones.</summary>
    public IReadOnlyList<EqPreset> All => [.. User, .. BuiltIn];

    /// <summary>A user preset of that name, else a built-in one (names compare case-insensitively).</summary>
    public EqPreset? Find(string name) =>
        All.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Saves (or replaces) a user preset.</summary>
    public void Save(string name, EqualizerSettings settings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(settings);
        var copy = settings.Clone();
        copy.Preset = null;
        lock (_lock)
        {
            var list = Load();
            list.RemoveAll(p => string.Equals(p.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
            list.Add(new EqPreset(name.Trim(), copy));
            Store(list);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool Delete(string name)
    {
        bool removed;
        lock (_lock)
        {
            var list = Load();
            removed = list.RemoveAll(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) > 0;
            if (removed)
            {
                Store(list);
            }
        }
        if (removed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        return removed;
    }

    public bool Rename(string name, string newName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);
        bool renamed;
        lock (_lock)
        {
            var list = Load();
            var index = list.FindIndex(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            renamed = index >= 0 && !list.Any(p => string.Equals(p.Name, newName.Trim(), StringComparison.OrdinalIgnoreCase));
            if (renamed)
            {
                list[index] = list[index] with { Name = newName.Trim() };
                Store(list);
            }
        }
        if (renamed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        return renamed;
    }

    /// <summary>
    /// Imports a Winamp .eqf preset file or EQ library (.q1) as user presets; a name that already exists gets a
    /// number. Returns the imported presets. Throws <see cref="InvalidDataException"/> for other files.
    /// </summary>
    public IReadOnlyList<EqPreset> Import(string path)
    {
        var imported = EqfFile.Parse(File.ReadAllBytes(path));
        if (imported.Count == 0)
        {
            throw new InvalidDataException("The file contains no presets.");
        }
        var added = new List<EqPreset>();
        lock (_lock)
        {
            var list = Load();
            foreach (var preset in imported)
            {
                var name = string.IsNullOrWhiteSpace(preset.Name) ? Path.GetFileNameWithoutExtension(path) : preset.Name;
                var unique = name;
                for (var n = 2; list.Concat(BuiltIn).Any(p => string.Equals(p.Name, unique, StringComparison.OrdinalIgnoreCase)); n++)
                {
                    unique = $"{name} ({n})";
                }
                var entry = preset with { Name = unique };
                list.Add(entry);
                added.Add(entry);
            }
            Store(list);
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return added;
    }

    private static EqPreset Graphic(string name, params double[] bands) =>
        new(name, new EqualizerSettings { Bands = bands }) { IsBuiltIn = true };

    private List<EqPreset> Load()
    {
        if (_user is not null)
        {
            return _user;
        }
        try
        {
            _user = File.Exists(_file) ? JsonSerializer.Deserialize<List<EqPreset>>(File.ReadAllText(_file), Json) ?? [] : [];
        }
        catch (JsonException)
        {
            _user = []; // a broken file is replaced on the next save
        }
        return _user;
    }

    private void Store(List<EqPreset> list)
    {
        _user = list;
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        var temp = _file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(list, Json));
        File.Move(temp, _file, overwrite: true);
    }
}

/// <summary>
/// EQ-02: Winamp's binary EQ format (.eqf, and the .q1 library with several entries): the header
/// "Winamp EQ library file v1.1" + 0x1A "!--", then per preset a 257-byte name and 11 bytes: ten bands and the
/// preamp, each 0–63 where 0 = +12 dB, 31.5 = 0 dB and 63 = −12 dB.
/// </summary>
public static class EqfFile
{
    public const string Signature = "Winamp EQ library file v1.1";
    private const int HeaderLength = 31;
    private const int NameLength = 257;
    private const int EntryLength = NameLength + 11;

    public static IReadOnlyList<EqPreset> Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderLength || Encoding.ASCII.GetString(data[..Signature.Length]) != Signature)
        {
            throw new InvalidDataException("Not a Winamp EQ file (.eqf or .q1).");
        }
        var presets = new List<EqPreset>();
        for (var offset = HeaderLength; offset + EntryLength <= data.Length; offset += EntryLength)
        {
            var nameBytes = data.Slice(offset, NameLength);
            var end = nameBytes.IndexOf((byte)0);
            var name = Encoding.Latin1.GetString(end >= 0 ? nameBytes[..end] : nameBytes).Trim();
            var values = data.Slice(offset + NameLength, 11);
            var bands = new double[10];
            for (var i = 0; i < bands.Length; i++)
            {
                bands[i] = ToDb(values[i]);
            }
            var settings = new EqualizerSettings { Bands = bands, PreampDb = ToDb(values[10]) };
            presets.Add(new EqPreset(name, settings));
        }
        return presets;
    }

    /// <summary>Writes presets in the .eqf/.q1 format (used by the tests; export may use it later).</summary>
    public static byte[] Write(IEnumerable<EqPreset> presets)
    {
        ArgumentNullException.ThrowIfNull(presets);
        using var stream = new MemoryStream();
        stream.Write(Encoding.ASCII.GetBytes(Signature));
        stream.Write([0x1A, (byte)'!', (byte)'-', (byte)'-']);
        foreach (var preset in presets)
        {
            var name = new byte[NameLength];
            var bytes = Encoding.Latin1.GetBytes(preset.Name);
            bytes.AsSpan(0, Math.Min(bytes.Length, NameLength - 1)).CopyTo(name);
            stream.Write(name);
            foreach (var db in preset.Settings.Bands.Take(10).Append(preset.Settings.PreampDb))
            {
                stream.WriteByte(FromDb(db));
            }
        }
        return stream.ToArray();
    }

    private static double ToDb(byte value) => Math.Round((31.5 - Math.Min(value, (byte)63)) * 12 / 31.5, 1);

    private static byte FromDb(double db) => (byte)Math.Clamp(Math.Round(31.5 - (db * 31.5 / 12)), 0, 63);
}
