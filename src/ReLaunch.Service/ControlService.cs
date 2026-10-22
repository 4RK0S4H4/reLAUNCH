using ReLaunch.Core.Actions;
using ReLaunch.Core.Catalogue;
using ReLaunch.Core.Contracts;

namespace ReLaunch.Service;

public sealed record StatusDto(string Pc, string Session, string PcName);

/// <summary>Everything a client (debug HTTP API now, relay later) can ask the PC to do.</summary>
public sealed class ControlService(
    CatalogueService catalogue,
    AgentSupervisor supervisor,
    AppActionRunner runner,
    ProcessSnapshotCache processes,
    Database db,
    EventHub events)
{
    public StatusDto GetStatus() => new("online", supervisor.SessionState, Environment.MachineName);

    public async Task<List<AppDto>> GetAppsAsync(bool favoritesOnly, string? query, CancellationToken ct)
    {
        var favorites = db.GetFavorites();
        IEnumerable<ManagedApp> apps = catalogue.Apps;
        if (favoritesOnly) apps = favorites.Select(catalogue.Find).OfType<ManagedApp>();
        apps = CatalogueBuilder.Search(apps, query);

        var snapshot = supervisor.IsReady ? await processes.GetAsync(ct) : Array.Empty<ProcessSnapshot>();
        var now = DateTimeOffset.UtcNow;
        return apps.Select(a => new AppDto(
            a.Id, a.Name, favorites.Contains(a.Id), a.Trackable, catalogue.IconHash(a.Id),
            ProcessMatcher.Summarize(a.ProcessNames, snapshot, now),
            runner.PhaseOf(a.Id).ToString().ToLowerInvariant())).ToList();
    }

    public List<string> GetFavoriteIds() => db.GetFavorites();

    public byte[]? GetIcon(string id) => catalogue.ReadIcon(id);

    public async Task<ActionResultDto> ActAsync(string appId, ActionKind kind, string deviceId, CancellationToken ct)
    {
        var app = catalogue.Find(appId);
        if (app is null) return Record(deviceId, appId, kind, new ActionResultDto(false, "failed", "unknownApp", "Unknown app."));
        if (supervisor.SessionState == "noSession")
            return Record(deviceId, appId, kind, new ActionResultDto(false, "failed", "noSession", "Nobody is logged in on the PC."));
        if (!supervisor.IsReady)
            return Record(deviceId, appId, kind, new ActionResultDto(false, "failed", "agentNotReady", "The PC helper is still starting."));

        var r = await runner.RunAsync(app, kind, ct);
        return Record(deviceId, appId, kind, new ActionResultDto(r.Ok, r.Phase.ToString().ToLowerInvariant(), r.ErrorCode, r.Message));
    }

    public void SetFavorites(IEnumerable<string> ids)
    {
        db.SetFavorites(ids.Where(id => catalogue.Find(id) is not null));
        events.Publish(new { type = "favorites" });
    }

    public List<ActivityDto> GetActivity(int limit)
    {
        var names = catalogue.Apps.ToDictionary(a => a.Id, a => a.Name);
        return db.GetActivity(limit)
            .Select(r => new ActivityDto(r.Time, r.DeviceId, r.AppId, names.TryGetValue(r.AppId, out var n) ? n : r.AppId, r.Action, r.Result))
            .ToList();
    }

    private ActionResultDto Record(string deviceId, string appId, ActionKind kind, ActionResultDto result)
    {
        db.AddActivity(deviceId, appId, kind.ToString().ToLowerInvariant(), result.Ok ? "ok" : result.ErrorCode ?? "failed");
        return result;
    }
}
