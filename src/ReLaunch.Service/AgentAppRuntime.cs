using ReLaunch.Core.Actions;
using ReLaunch.Core.Catalogue;
using ReLaunch.Core.Pipes;

namespace ReLaunch.Service;

/// <summary>Short-lived cache of the process list so a screenful of status checks costs one agent call.</summary>
public sealed class ProcessSnapshotCache(AgentClient agent)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<ProcessSnapshot> _snapshot = [];
    private DateTime _taken = DateTime.MinValue;

    public async Task<IReadOnlyList<ProcessSnapshot>> GetAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (DateTime.UtcNow - _taken > TimeSpan.FromMilliseconds(300))
            {
                _snapshot = await agent.CallAsync<List<ProcessSnapshot>>("processes", null, TimeSpan.FromSeconds(10), ct) ?? [];
                _taken = DateTime.UtcNow;
            }
            return _snapshot;
        }
        finally { _gate.Release(); }
    }

    public void Invalidate() => _taken = DateTime.MinValue;
}

public sealed class AgentAppRuntime(AgentClient agent, ProcessSnapshotCache processes) : IAppRuntime
{
    public async Task<AppStatus> GetStatusAsync(ManagedApp app, CancellationToken ct) =>
        ProcessMatcher.Summarize(app.ProcessNames, await processes.GetAsync(ct), DateTimeOffset.UtcNow);

    public async Task StopAsync(ManagedApp app, CancellationToken ct)
    {
        if (!app.Trackable) return;
        var timeout = TimeSpan.FromMilliseconds(app.CloseTimeoutMs) + TimeSpan.FromSeconds(15);
        await agent.CallAsync("stop", new { names = app.ProcessNames, timeoutMs = app.CloseTimeoutMs }, timeout, ct);
        processes.Invalidate();
    }

    public async Task LaunchAsync(ManagedApp app, CancellationToken ct)
    {
        try
        {
            await agent.CallAsync("launch", new { id = app.Id, args = app.Args }, TimeSpan.FromSeconds(20), ct);
        }
        catch (RpcException ex) when (ex.Code == "elevationRequired")
        {
            throw new ElevationRequiredException(ex.Message);
        }
        processes.Invalidate();
    }
}
