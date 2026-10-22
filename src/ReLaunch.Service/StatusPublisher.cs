using ReLaunch.Core.Actions;
using ReLaunch.Core.Catalogue;

namespace ReLaunch.Service;

/// <summary>Polls process state every few seconds and publishes only the changes (running flips, session changes).</summary>
public sealed class StatusPublisher(
    CatalogueService catalogue,
    AgentSupervisor supervisor,
    ProcessSnapshotCache processes,
    AppActionRunner runner,
    EventHub events) : BackgroundService
{
    private readonly Dictionary<string, bool> _last = new();

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        supervisor.SessionChanged += state => events.Publish(new { type = "session", state });
        runner.Changed += e => events.Publish(new
        {
            type = "action",
            appId = e.AppId,
            kind = e.Kind.ToString().ToLowerInvariant(),
            phase = e.Phase.ToString().ToLowerInvariant(),
            message = e.Message,
        });

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (supervisor.IsReady) await PublishChangesAsync(ct);
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception)
            {
                await Task.Delay(TimeSpan.FromSeconds(3), CancellationToken.None);
            }
        }
    }

    private async Task PublishChangesAsync(CancellationToken ct)
    {
        var snapshot = await processes.GetAsync(ct);
        var now = DateTimeOffset.UtcNow;
        foreach (var app in catalogue.Apps.Where(a => a.Trackable))
        {
            var status = ProcessMatcher.Summarize(app.ProcessNames, snapshot, now);
            if (_last.TryGetValue(app.Id, out var was) && was == status.Running) continue;
            var first = !_last.ContainsKey(app.Id);
            _last[app.Id] = status.Running;
            if (!first) events.Publish(new { type = "status", appId = app.Id, status });
        }
    }
}
