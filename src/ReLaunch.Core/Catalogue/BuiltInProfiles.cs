namespace ReLaunch.Core.Catalogue;

public static class BuiltInProfiles
{
    /// <summary>Extra process names that belong to the app, or empty when there is no profile.</summary>
    public static string[] ExtraProcessesFor(AppEntry entry)
    {
        // Jellyfin runs as a tray app that spawns jellyfin.exe; both must be handled on restart.
        if (entry.Name.Contains("Jellyfin Server", StringComparison.OrdinalIgnoreCase))
            return ["Jellyfin.Windows.Tray", "jellyfin"];
        return [];
    }
}
