namespace ReLaunch.Core;

public static class DataPaths
{
    public static string Root
    {
        get
        {
            var overridden = Environment.GetEnvironmentVariable("RELAUNCH_DATA");
            var dir = !string.IsNullOrWhiteSpace(overridden)
                ? overridden
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "reLAUNCH");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string Database => Path.Combine(Root, "relaunch.db");
    public static string Settings => Path.Combine(Root, "settings.json");
    public static string IconDir
    {
        get
        {
            var dir = Path.Combine(Root, "icons");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
