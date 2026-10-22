namespace ReLaunch.Core.Catalogue;

/// <summary>An entry of the Start menu app list, as reported by the agent.</summary>
public sealed record AppEntry(
    string Id,
    string Name,
    string ParsingName,
    string? ExePath,
    string[] ProcessNames);

public sealed record AppOverride(
    string AppId,
    string? Args,
    string[] ExtraProcesses,
    int? CloseTimeoutMs);

/// <summary>A catalogue entry merged with its override and built-in profile.</summary>
public sealed record ManagedApp(
    AppEntry Entry,
    string[] ProcessNames,
    string? Args,
    int CloseTimeoutMs)
{
    public string Id => Entry.Id;
    public string Name => Entry.Name;
    /// <summary>False when no process can be tied to the entry, so status cannot be tracked.</summary>
    public bool Trackable => ProcessNames.Length > 0;
}

public sealed record ProcessSnapshot(string Name, int Pid, DateTimeOffset? StartTime, long WorkingSet);

public sealed record AppStatus(bool Running, int ProcessCount, long UptimeSeconds, long WorkingSetBytes)
{
    public static readonly AppStatus Stopped = new(false, 0, 0, 0);
}
