using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ReLaunch.Core.Catalogue;
using Windows.Management.Deployment;
using static ReLaunch.Agent.Native;

namespace ReLaunch.Agent;

/// <summary>Reads the Start menu app list (shell:AppsFolder) and resolves each entry to an executable.</summary>
internal static partial class AppEnumerator
{
    [GeneratedRegex(@"^\{([0-9A-Fa-f\-]{36})\}\\(.+)$")]
    private static partial Regex KnownFolderPrefix();

    public static List<AppEntry> Enumerate()
    {
        var shortcuts = IndexStartMenuShortcuts();
        var packages = IndexPackages();

        var folderId = FolderIdAppsFolder;
        var iid = IidShellItem;
        SHGetKnownFolderItem(ref folderId, 0, IntPtr.Zero, ref iid, out var folder);

        var bhid = BhidEnumItems;
        var iidEnum = IidEnumShellItems;
        folder.BindToHandler(IntPtr.Zero, ref bhid, ref iidEnum, out var ptr);
        var enumerator = (IEnumShellItems)Marshal.GetObjectForIUnknown(ptr);
        Marshal.Release(ptr);

        var result = new List<AppEntry>();
        while (enumerator.Next(1, out var item, out var fetched) == 0 && fetched == 1)
        {
            try
            {
                item.GetDisplayName(SIGDN_NORMALDISPLAY, out var name);
                item.GetDisplayName(SIGDN_PARENTRELATIVEPARSING, out var parsing);
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(parsing)) continue;

                var exe = ResolveExe(name, parsing, shortcuts, packages);
                var procs = exe is not null && exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    ? new[] { Path.GetFileNameWithoutExtension(exe) }
                    : [];
                result.Add(new AppEntry(AppIds.From(parsing), name, parsing, exe, procs));
            }
            catch (COMException)
            {
                // Skip entries the shell cannot describe.
            }
            finally
            {
                Marshal.ReleaseComObject(item);
            }
        }
        Marshal.ReleaseComObject(enumerator);
        Marshal.ReleaseComObject(folder);
        return result;
    }

    private static string? ResolveExe(string name, string parsing, Dictionary<string, string> shortcuts,
        Dictionary<string, string> packages)
    {
        // Desktop entries sometimes carry the path itself, optionally behind a known-folder GUID.
        var path = parsing;
        var m = KnownFolderPrefix().Match(path);
        if (m.Success && Guid.TryParse(m.Groups[1].Value, out var kf))
        {
            try
            {
                SHGetKnownFolderPath(ref kf, 0, IntPtr.Zero, out var root);
                path = Path.Combine(root, m.Groups[2].Value);
            }
            catch (COMException) { }
        }
        if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && Path.IsPathRooted(path)) return path;

        // Packaged apps: "<PackageFamilyName>!<AppId>".
        var bang = parsing.IndexOf('!');
        if (bang > 0 && packages.TryGetValue(parsing[..bang], out var installed))
        {
            var exe = ReadManifestExecutable(installed, parsing[(bang + 1)..]);
            if (exe is not null) return exe;
        }

        return shortcuts.TryGetValue(name, out var target) ? target : null;
    }

    private static string? ReadManifestExecutable(string installPath, string appId)
    {
        try
        {
            var doc = XDocument.Load(Path.Combine(installPath, "AppxManifest.xml"));
            var app = doc.Descendants().FirstOrDefault(e =>
                e.Name.LocalName == "Application" && (string?)e.Attribute("Id") == appId);
            var exe = (string?)app?.Attribute("Executable");
            return string.IsNullOrEmpty(exe) ? null : Path.Combine(installPath, exe);
        }
        catch (Exception ex) when (ex is IOException or System.Xml.XmlException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static Dictionary<string, string> IndexPackages()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var package in new PackageManager().FindPackagesForUser(string.Empty))
            {
                try { map[package.Id.FamilyName] = package.InstalledPath; }
                catch (Exception) { /* package without a readable location */ }
            }
        }
        catch (Exception)
        {
            // Package enumeration is best effort; desktop apps still work.
        }
        return map;
    }

    /// <summary>Start menu shortcut name (without .lnk) to the .exe it targets.</summary>
    private static Dictionary<string, string> IndexStartMenuShortcuts()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var roots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs"),
        };
        foreach (var root in roots.Where(Directory.Exists))
        {
            foreach (var lnk in Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories))
            {
                try
                {
                    var link = (IShellLinkW)new ShellLink();
                    ((IPersistFile)link).Load(lnk, 0);
                    var sb = new StringBuilder(520);
                    link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
                    var target = sb.ToString();
                    if (target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        map.TryAdd(Path.GetFileNameWithoutExtension(lnk), target);
                    Marshal.ReleaseComObject(link);
                }
                catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or IOException)
                {
                    // Unreadable shortcut; ignore.
                }
            }
        }
        return map;
    }
}
