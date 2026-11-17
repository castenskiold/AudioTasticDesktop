using System.Text.Json;
using System.Text.Json.Nodes;
using AudioTastic.Data.Settings;
using AudioTastic.Sdk.Plugins;
using Microsoft.Extensions.Logging;

namespace AudioTastic.Plugins.Host;

/// <summary>PG-04: plugin status as shown in the plugin manager.</summary>
public enum PluginStatus
{
    Enabled,
    Disabled,
    /// <summary>Failed to load, or disabled after repeated faults (see <see cref="PluginRecord.Error"/>).</summary>
    Failed,
    /// <summary>Invalid manifest or built for another SDK version.</summary>
    Incompatible,
}

/// <summary>One discovered plugin.</summary>
public sealed class PluginRecord
{
    internal PluginRecord(PluginManifest manifest, string folder, bool isBuiltIn)
    {
        Manifest = manifest;
        Folder = folder;
        IsBuiltIn = isBuiltIn;
    }

    public PluginManifest Manifest { get; }
    public string Id => Manifest.Id;
    public string Folder { get; }

    /// <summary>Shipped with the player (can be disabled, not uninstalled).</summary>
    public bool IsBuiltIn { get; }

    public PluginStatus Status { get; internal set; }

    /// <summary>Why the plugin is Failed or Incompatible.</summary>
    public string? Error { get; internal set; }

    /// <summary>The plugin classes, while enabled.</summary>
    public IReadOnlyList<IPlugin> Instances { get; internal set; } = [];

    /// <summary>True when any instance offers a settings page (PG-05).</summary>
    public bool HasSettingsPage => Instances.OfType<IPluginSettingsPage>().Any();

    internal PluginLoadContext? LoadContext { get; set; }
    internal Queue<DateTime> Faults { get; } = new();

    public override string ToString() => $"{Manifest.Name} {Manifest.Version} ({Status})";
}

/// <summary>PG-05: a plugin's settings, stored as a JSON object under Settings › Plugins › Data[plugin id].</summary>
internal sealed class PluginSettingsStore(SettingsStore settings, string pluginId) : IPluginSettings
{
    public event EventHandler<string>? Changed;

    public T GetValue<T>(string key, T defaultValue)
    {
        if (!settings.Current.Plugins.Data.TryGetValue(pluginId, out var data) || data[key] is not { } node)
        {
            return defaultValue;
        }
        try
        {
            return node.Deserialize<T>() ?? defaultValue;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException)
        {
            return defaultValue;
        }
    }

    public void SetValue<T>(string key, T value)
    {
        var node = JsonSerializer.SerializeToNode(value);
        settings.Update(SettingsGroup.Plugins, s =>
        {
            if (!s.Plugins.Data.TryGetValue(pluginId, out var data))
            {
                s.Plugins.Data[pluginId] = data = new JsonObject();
            }
            data[key] = node;
        });
        Changed?.Invoke(this, key);
    }

    public void Remove(string key)
    {
        settings.Update(SettingsGroup.Plugins, s =>
        {
            if (s.Plugins.Data.TryGetValue(pluginId, out var data))
            {
                data.Remove(key);
            }
        });
        Changed?.Invoke(this, key);
    }
}

internal sealed class PluginContext(string pluginId, string pluginFolder, string dataFolder, IPluginSettings settings,
    IPlayerApi player, ILogger logger) : IPluginContext
{
    public string PluginId => pluginId;
    public string PluginFolder => pluginFolder;

    public string DataFolder
    {
        get
        {
            Directory.CreateDirectory(dataFolder);
            return dataFolder;
        }
    }

    public IPluginSettings Settings => settings;
    public IPlayerApi Player => player;
    public ILogger Logger => logger;
}
