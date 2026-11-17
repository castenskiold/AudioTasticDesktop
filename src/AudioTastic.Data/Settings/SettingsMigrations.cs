using System.Text.Json.Nodes;

namespace AudioTastic.Data.Settings;

/// <summary>
/// Upgrades settings JSON written by older versions. Each step takes the document from version N to N+1.
/// </summary>
internal static class SettingsMigrations
{
    private static readonly Dictionary<int, Action<JsonObject>> Steps = new()
    {
        // 0 -> 1: files written before versioning existed had no schemaVersion; nothing else changed.
        [0] = _ => { },
    };

    /// <summary>Migrates <paramref name="root"/> in place up to <see cref="AppSettings.CurrentSchemaVersion"/>.</summary>
    /// <returns>The version the document had before migrating.</returns>
    public static int MigrateToCurrent(JsonObject root)
    {
        var original = root["schemaVersion"]?.GetValue<int>() ?? 0;
        if (original > AppSettings.CurrentSchemaVersion)
        {
            throw new SettingsVersionException(original);
        }

        for (var v = original; v < AppSettings.CurrentSchemaVersion; v++)
        {
            if (!Steps.TryGetValue(v, out var step))
            {
                throw new InvalidOperationException($"No settings migration from version {v}.");
            }
            step(root);
            root["schemaVersion"] = v + 1;
        }
        return original;
    }
}

/// <summary>Thrown when settings were written by a newer AudioTastic version.</summary>
public sealed class SettingsVersionException(int version)
    : Exception($"Settings file has schema version {version}, newer than this build supports ({AppSettings.CurrentSchemaVersion}).")
{
    public int Version { get; } = version;
}
