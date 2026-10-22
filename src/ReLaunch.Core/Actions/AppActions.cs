using ReLaunch.Core.Catalogue;

namespace ReLaunch.Core.Actions;

public enum ActionKind { Start, Stop, Restart }

public enum ActionPhase { Idle, Stopping, Starting, Running, Stopped, Failed }

public sealed record ActionResult(bool Ok, ActionPhase Phase, string? ErrorCode = null, string? Message = null);

public sealed record ActionEvent(string AppId, ActionKind Kind, ActionPhase Phase, string? Message);

/// <summary>Raised by a runtime when launching needs elevation, which v1 does not support.</summary>
public sealed class ElevationRequiredException(string message) : Exception(message);

/// <summary>What the runner needs from the world; implemented over the agent, faked in tests.</summary>
public interface IAppRuntime
{
    Task<AppStatus> GetStatusAsync(ManagedApp app, CancellationToken ct);
    /// <summary>Close gracefully, then kill whatever is left. Returns when the attempt is finished.</summary>
    Task StopAsync(ManagedApp app, CancellationToken ct);
    Task LaunchAsync(ManagedApp app, CancellationToken ct);
}
