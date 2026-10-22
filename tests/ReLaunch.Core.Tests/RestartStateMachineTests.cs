using ReLaunch.Core.Actions;
using ReLaunch.Core.Catalogue;
using Xunit;

namespace ReLaunch.Core.Tests;

public class RestartStateMachineTests
{
    private sealed class FakeRuntime : IAppRuntime
    {
        public bool Running;
        public bool StopWorks = true;
        public bool LaunchWorks = true;
        public Exception? LaunchThrows;
        public TaskCompletionSource? LaunchGate;
        public List<string> Calls = [];

        public Task<AppStatus> GetStatusAsync(ManagedApp app, CancellationToken ct) =>
            Task.FromResult(Running ? new AppStatus(true, 1, 5, 100) : AppStatus.Stopped);

        public Task StopAsync(ManagedApp app, CancellationToken ct)
        {
            Calls.Add("stop");
            if (StopWorks) Running = false;
            return Task.CompletedTask;
        }

        public async Task LaunchAsync(ManagedApp app, CancellationToken ct)
        {
            Calls.Add("launch");
            if (LaunchGate is not null) await LaunchGate.Task;
            if (LaunchThrows is not null) throw LaunchThrows;
            if (LaunchWorks) Running = true;
        }
    }

    private static ManagedApp App(bool trackable = true) =>
        CatalogueBuilder.Merge(new AppEntry("a1", "App", "app", null, trackable ? ["app"] : []), null);

    private static AppActionRunner Runner(FakeRuntime rt) => new(rt)
    {
        StartTimeout = TimeSpan.FromMilliseconds(80),
        StopVerifyTimeout = TimeSpan.FromMilliseconds(80),
        PollInterval = TimeSpan.FromMilliseconds(5),
    };

    [Fact]
    public async Task RestartGoesStoppingStartingRunning()
    {
        var rt = new FakeRuntime { Running = true };
        var runner = Runner(rt);
        var phases = new List<ActionPhase>();
        runner.Changed += e => phases.Add(e.Phase);

        var result = await runner.RunAsync(App(), ActionKind.Restart);

        Assert.True(result.Ok);
        Assert.Equal(new[] {ActionPhase.Stopping, ActionPhase.Stopped, ActionPhase.Starting, ActionPhase.Running}, phases);
        Assert.Equal(new[] {"stop", "launch"}, rt.Calls);
        Assert.Equal(ActionPhase.Running, runner.PhaseOf("a1"));
    }

    [Fact]
    public async Task RestartOfStoppedAppJustStarts()
    {
        var rt = new FakeRuntime { Running = false };
        var result = await Runner(rt).RunAsync(App(), ActionKind.Restart);
        Assert.True(result.Ok);
        Assert.Equal(new[] {"launch"}, rt.Calls);
    }

    [Fact]
    public async Task StartWhenRunningDoesNothing()
    {
        var rt = new FakeRuntime { Running = true };
        var result = await Runner(rt).RunAsync(App(), ActionKind.Start);
        Assert.True(result.Ok);
        Assert.Empty(rt.Calls);
    }

    [Fact]
    public async Task StopFailureReportsFailed()
    {
        var rt = new FakeRuntime { Running = true, StopWorks = false };
        var result = await Runner(rt).RunAsync(App(), ActionKind.Stop);
        Assert.False(result.Ok);
        Assert.Equal("stopFailed", result.ErrorCode);
        Assert.Equal(ActionPhase.Failed, result.Phase);
    }

    [Fact]
    public async Task FailedStopAbortsRestart()
    {
        var rt = new FakeRuntime { Running = true, StopWorks = false };
        var result = await Runner(rt).RunAsync(App(), ActionKind.Restart);
        Assert.False(result.Ok);
        Assert.DoesNotContain("launch", rt.Calls);
    }

    [Fact]
    public async Task StartTimeoutReportsFailed()
    {
        var rt = new FakeRuntime { LaunchWorks = false };
        var result = await Runner(rt).RunAsync(App(), ActionKind.Start);
        Assert.False(result.Ok);
        Assert.Equal("startTimeout", result.ErrorCode);
    }

    [Fact]
    public async Task ElevationErrorHasItsOwnCode()
    {
        var rt = new FakeRuntime { LaunchThrows = new ElevationRequiredException("needs admin") };
        var result = await Runner(rt).RunAsync(App(), ActionKind.Start);
        Assert.False(result.Ok);
        Assert.Equal("elevationRequired", result.ErrorCode);
        Assert.Equal("needs admin", result.Message);
    }

    [Fact]
    public async Task SecondActionWhileBusyIsRejected()
    {
        var rt = new FakeRuntime { LaunchGate = new TaskCompletionSource() };
        var runner = Runner(rt);
        var first = runner.RunAsync(App(), ActionKind.Start);
        while (!rt.Calls.Contains("launch")) await Task.Delay(1);

        var second = await runner.RunAsync(App(), ActionKind.Start);
        Assert.Equal("busy", second.ErrorCode);

        rt.LaunchGate.SetResult();
        Assert.True((await first).Ok);
    }

    [Fact]
    public async Task UntrackableAppStartsWithoutWaiting()
    {
        var rt = new FakeRuntime { LaunchWorks = false };
        var result = await Runner(rt).RunAsync(App(trackable: false), ActionKind.Start);
        Assert.True(result.Ok);
        Assert.Equal(new[] {"launch"}, rt.Calls);
    }
}
