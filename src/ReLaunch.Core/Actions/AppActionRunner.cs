using System.Collections.Concurrent;
using ReLaunch.Core.Catalogue;

namespace ReLaunch.Core.Actions;

/// <summary>Runs start / stop / restart with the state transitions stopping → starting → running (or failed).</summary>
public sealed class AppActionRunner(IAppRuntime runtime)
{
    private readonly ConcurrentDictionary<string, ActionPhase> _phases = new();
    private readonly ConcurrentDictionary<string, byte> _busy = new();

    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan StopVerifyTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    public event Action<ActionEvent>? Changed;

    public ActionPhase PhaseOf(string appId) => _phases.TryGetValue(appId, out var p) ? p : ActionPhase.Idle;

    public async Task<ActionResult> RunAsync(ManagedApp app, ActionKind kind, CancellationToken ct = default)
    {
        if (!_busy.TryAdd(app.Id, 0))
            return new ActionResult(false, PhaseOf(app.Id), "busy", "Another action on this app is still running.");
        try
        {
            return kind switch
            {
                ActionKind.Start => await StartAsync(app, kind, ct),
                ActionKind.Stop => await StopAsync(app, kind, ct),
                _ => await RestartAsync(app, ct),
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ElevationRequiredException ex)
        {
            return Fail(app, kind, "elevationRequired", ex.Message);
        }
        catch (Exception ex)
        {
            return Fail(app, kind, "error", ex.Message);
        }
        finally
        {
            _busy.TryRemove(app.Id, out _);
        }
    }

    private async Task<ActionResult> RestartAsync(ManagedApp app, CancellationToken ct)
    {
        var stop = await StopAsync(app, ActionKind.Restart, ct);
        if (!stop.Ok) return stop;
        return await StartAsync(app, ActionKind.Restart, ct);
    }

    private async Task<ActionResult> StopAsync(ManagedApp app, ActionKind kind, CancellationToken ct)
    {
        var status = await runtime.GetStatusAsync(app, ct);
        if (app.Trackable && !status.Running)
            return Done(app, kind, ActionPhase.Stopped, "Already stopped.");

        Set(app, kind, ActionPhase.Stopping, null);
        await runtime.StopAsync(app, ct);

        if (app.Trackable && !await WaitForAsync(app, running: false, StopVerifyTimeout, ct))
            return Fail(app, kind, "stopFailed", $"{app.Name} is still running.");
        return Done(app, kind, ActionPhase.Stopped, null);
    }

    private async Task<ActionResult> StartAsync(ManagedApp app, ActionKind kind, CancellationToken ct)
    {
        if (app.Trackable && (await runtime.GetStatusAsync(app, ct)).Running)
            return Done(app, kind, ActionPhase.Running, "Already running.");

        Set(app, kind, ActionPhase.Starting, null);
        await runtime.LaunchAsync(app, ct);

        // Without a known process there is nothing to wait for; the launch itself succeeded.
        if (app.Trackable && !await WaitForAsync(app, running: true, StartTimeout, ct))
            return Fail(app, kind, "startTimeout", $"{app.Name} did not start within {StartTimeout.TotalSeconds:0} seconds.");
        return Done(app, kind, ActionPhase.Running, null);
    }

    private async Task<bool> WaitForAsync(ManagedApp app, bool running, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            if ((await runtime.GetStatusAsync(app, ct)).Running == running) return true;
            if (DateTime.UtcNow >= deadline) return false;
            await Task.Delay(PollInterval, ct);
        }
    }

    private void Set(ManagedApp app, ActionKind kind, ActionPhase phase, string? message)
    {
        _phases[app.Id] = phase;
        Changed?.Invoke(new ActionEvent(app.Id, kind, phase, message));
    }

    private ActionResult Done(ManagedApp app, ActionKind kind, ActionPhase phase, string? message)
    {
        Set(app, kind, phase, message);
        return new ActionResult(true, phase, null, message);
    }

    private ActionResult Fail(ManagedApp app, ActionKind kind, string code, string message)
    {
        Set(app, kind, ActionPhase.Failed, message);
        return new ActionResult(false, ActionPhase.Failed, code, message);
    }
}
