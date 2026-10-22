namespace ReLaunch.Core.Catalogue;

public static class ProcessMatcher
{
    public static IReadOnlyList<ProcessSnapshot> Match(IReadOnlyCollection<string> names, IEnumerable<ProcessSnapshot> all)
    {
        if (names.Count == 0) return [];
        var set = new HashSet<string>(names.Select(CatalogueBuilder.NormalizeProcessName), StringComparer.OrdinalIgnoreCase);
        return all.Where(p => set.Contains(CatalogueBuilder.NormalizeProcessName(p.Name))).ToList();
    }

    public static AppStatus Summarize(IReadOnlyCollection<string> names, IEnumerable<ProcessSnapshot> all, DateTimeOffset now)
    {
        var matched = Match(names, all);
        if (matched.Count == 0) return AppStatus.Stopped;
        var oldest = matched.Where(p => p.StartTime.HasValue).Select(p => p.StartTime!.Value).DefaultIfEmpty(now).Min();
        var uptime = (long)Math.Max(0, (now - oldest).TotalSeconds);
        return new AppStatus(true, matched.Count, uptime, matched.Sum(p => p.WorkingSet));
    }
}
