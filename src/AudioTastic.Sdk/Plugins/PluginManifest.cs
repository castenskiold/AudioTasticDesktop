using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AudioTastic.Sdk.Plugins;

/// <summary>The SDK's own version and the compatibility rule for plugins (PG-02, PG-03).</summary>
public static class SdkInfo
{
    /// <summary>
    /// Version of these contracts. While the SDK is 0.x (until P8) a plugin needs the same 0.minor; from 1.0 the same
    /// major and a minor not newer than the player's.
    /// </summary>
    public const string Version = "0.4.0";

    public static bool IsCompatible(string pluginSdkVersion)
    {
        if (!System.Version.TryParse(pluginSdkVersion, out var plugin) || !System.Version.TryParse(Version, out var host))
        {
            return false;
        }
        return host.Major == 0
            ? plugin.Major == 0 && plugin.Minor == host.Minor
            : plugin.Major == host.Major && plugin.Minor <= host.Minor;
    }
}

/// <summary>
/// PG-02: plugin.json, at the root of a plugin folder or .atplugin package.
/// <code>
/// { "id": "com.example.hello", "name": "Hello", "version": "1.0.0", "author": "Me",
///   "sdkVersion": "0.4.0", "entryAssembly": "Hello.dll", "types": ["general"] }
/// </code>
/// </summary>
public sealed partial class PluginManifest
{
    public const string FileName = "plugin.json";

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>Unique, stable id in reverse-domain style (letters, digits, '.', '-', '_').</summary>
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string? Author { get; set; }
    public string? Description { get; set; }
    public string? Website { get; set; }
    public string? License { get; set; }

    /// <summary>The AudioTastic.Sdk version the plugin was built against.</summary>
    public string SdkVersion { get; set; } = string.Empty;

    /// <summary>The assembly (relative to the plugin folder) containing the plugin classes.</summary>
    public string EntryAssembly { get; set; } = string.Empty;

    public List<PluginType> Types { get; set; } = [];

    /// <summary>Problems that make the manifest unusable; empty when valid.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (!IdPattern().IsMatch(Id))
        {
            errors.Add("id is missing or contains characters other than letters, digits, '.', '-' and '_'");
        }
        if (string.IsNullOrWhiteSpace(Name))
        {
            errors.Add("name is missing");
        }
        if (!System.Version.TryParse(Version.Split('-')[0], out _))
        {
            errors.Add("version is missing or not a version number");
        }
        if (string.IsNullOrWhiteSpace(SdkVersion))
        {
            errors.Add("sdkVersion is missing");
        }
        else if (!SdkInfo.IsCompatible(SdkVersion))
        {
            errors.Add($"built for SDK {SdkVersion}; this player has SDK {SdkInfo.Version}");
        }
        if (string.IsNullOrWhiteSpace(EntryAssembly) || EntryAssembly.Contains("..", StringComparison.Ordinal)
            || Path.IsPathRooted(EntryAssembly) || !EntryAssembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("entryAssembly must be a .dll inside the plugin folder");
        }
        if (Types.Count == 0)
        {
            errors.Add("types is empty");
        }
        return errors;
    }

    public static PluginManifest Parse(string json) =>
        JsonSerializer.Deserialize<PluginManifest>(json, JsonOptions) ?? throw new JsonException("plugin.json is empty.");

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{1,99}$")]
    private static partial Regex IdPattern();
}
