using System.Text.Json;

namespace ReLaunch.Core;

/// <summary>Plain settings persisted as JSON in the data directory.</summary>
public sealed class ServiceSettings
{
    /// <summary>Optional HTTP API on 127.0.0.1:5480 for debugging. Off by default; changing it needs a service restart.</summary>
    public bool DebugApi { get; set; }
    public string RelayUrl { get; set; } = "";
    /// <summary>PC key, DPAPI (machine scope) protected and base64 encoded.</summary>
    public string? PcKeyProtected { get; set; }
    public bool StartWithWindows { get; set; } = true;

    public static ServiceSettings Load(string? path = null)
    {
        path ??= DataPaths.Settings;
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<ServiceSettings>(File.ReadAllText(path), Json.Options) ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            // Fall through to defaults on a damaged file.
        }
        return new ServiceSettings();
    }

    public void Save(string? path = null)
    {
        path ??= DataPaths.Settings;
        File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions(Json.Options) { WriteIndented = true }));
    }
}
