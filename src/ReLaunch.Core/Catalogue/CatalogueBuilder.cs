namespace ReLaunch.Core.Catalogue;

public static class CatalogueBuilder
{
    public const int DefaultCloseTimeoutMs = 5000;

    public static string NormalizeProcessName(string name)
    {
        name = name.Trim();
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    public static ManagedApp Merge(AppEntry entry, AppOverride? over)
    {
        var names = entry.ProcessNames
            .Concat(BuiltInProfiles.ExtraProcessesFor(entry))
            .Concat(over?.ExtraProcesses ?? [])
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(NormalizeProcessName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var args = string.IsNullOrWhiteSpace(over?.Args) ? null : over!.Args;
        return new ManagedApp(entry, names, args, over?.CloseTimeoutMs ?? DefaultCloseTimeoutMs);
    }

    public static IReadOnlyList<ManagedApp> Build(IEnumerable<AppEntry> entries, IReadOnlyDictionary<string, AppOverride> overrides) =>
        entries
            .GroupBy(e => e.Id)
            .Select(g => g.First())
            .Select(e => Merge(e, overrides.TryGetValue(e.Id, out var o) ? o : null))
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    /// <summary>Case-insensitive search: prefix matches first, then substring matches.</summary>
    public static IEnumerable<ManagedApp> Search(IEnumerable<ManagedApp> apps, string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return apps;
        query = query.Trim();
        return apps
            .Select(a => (App: a, Rank: a.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 0
                : a.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ? 1 : 2))
            .Where(x => x.Rank < 2)
            .OrderBy(x => x.Rank)
            .ThenBy(x => x.App.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(x => x.App);
    }
}
