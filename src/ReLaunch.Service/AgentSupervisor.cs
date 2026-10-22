using System.Diagnostics;
using Microsoft.Extensions.Hosting.WindowsServices;
using ReLaunch.Core;
using ReLaunch.Core.Pipes;

namespace ReLaunch.Service;

/// <summary>
/// Keeps one ReLaunch.Agent running in the active console session, restarts it when it dies or the
/// session changes, and tracks whether the session is active, locked or empty.
/// </summary>
public sealed class AgentSupervisor(ILogger<AgentSupervisor> log) : BackgroundService
{
    private Process? _agent;
    private uint _agentSession = SessionLauncher.NoSession;
    private bool _ready;

    /// <summary>"noSession", "starting", "active" or "locked".</summary>
    public string SessionState { get; private set; } = "noSession";
    public bool IsReady => _ready;

    public event Action? AgentReady;
    public event Action<string>? SessionChanged;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        KillStrayAgents();
        while (!ct.IsCancellationRequested)
        {
            try { await TickAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Agent supervision step failed");
            }
            try { await Task.Delay(TimeSpan.FromSeconds(2), ct); }
            catch (OperationCanceledException) { break; }
        }
        KillAgent();
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var asService = WindowsServiceHelpers.IsWindowsService();
        var session = asService ? SessionLauncher.ActiveSessionId() : (uint)Process.GetCurrentProcess().SessionId;

        if (asService && !SessionLauncher.HasUser(session))
        {
            KillAgent();
            SetState("noSession");
            return;
        }

        var alive = _agent is { HasExited: false };
        if (!alive || _agentSession != session)
        {
            KillAgent();
            StartAgent(session, asService);
            SetState("starting");
        }

        try
        {
            var info = await PipeRpc.CallAsync(ProductInfo.AgentPipe, "session", null, TimeSpan.FromSeconds(3), ct);
            var state = info.GetProperty("state").GetString() ?? "active";
            var wasReady = _ready;
            _ready = true;
            SetState(state);
            if (!wasReady) AgentReady?.Invoke();
        }
        catch (Exception ex) when (ex is RpcException or IOException)
        {
            // Agent still starting up.
        }
    }

    private void StartAgent(uint session, bool asService)
    {
        var exe = FindAgentExe();
        if (exe is null)
        {
            log.LogError("ReLaunch.Agent.exe not found next to the service");
            return;
        }
        try
        {
            _agent = asService
                ? SessionLauncher.LaunchInSession(session, exe, "")
                : Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true });
            _agentSession = session;
            _ready = false;
            log.LogInformation("Started agent pid {Pid} in session {Session}", _agent?.Id, session);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Could not start the agent");
        }
    }

    private static string? FindAgentExe()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "ReLaunch.Agent.exe"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "agent", "ReLaunch.Agent.exe")),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private void KillAgent()
    {
        _ready = false;
        try { if (_agent is { HasExited: false }) _agent.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        _agent?.Dispose();
        _agent = null;
        _agentSession = SessionLauncher.NoSession;
    }

    private static void KillStrayAgents()
    {
        foreach (var p in Process.GetProcessesByName("ReLaunch.Agent"))
        {
            try { p.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            p.Dispose();
        }
    }

    private void SetState(string state)
    {
        if (state == SessionState) return;
        SessionState = state;
        SessionChanged?.Invoke(state);
    }
}
