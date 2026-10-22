using System.IO.Pipes;
using System.Text.Json;
using ReLaunch.Core.Pipes;
using Xunit;

namespace ReLaunch.Core.Tests;

public class PipeRpcTests
{
    private sealed record Echo(string Text);

    [Fact]
    public async Task RoundTripsRequestsAndErrors()
    {
        var name = "relaunch-test-" + Guid.NewGuid().ToString("N");
        using var cts = new CancellationTokenSource();
        var server = PipeRpc.ServeAsync(
            () => new NamedPipeServerStream(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous),
            (method, args, _) => method switch
            {
                "echo" => Task.FromResult<object?>(args.Deserialize<Echo>(Json.Options)),
                "boom" => throw new RpcException("nope", "denied"),
                _ => throw new InvalidOperationException("unknown"),
            },
            cts.Token);

        var echo = await PipeRpc.CallAsync<Echo>(name, "echo", new Echo("hi"), TimeSpan.FromSeconds(5));
        Assert.Equal("hi", echo!.Text);

        var ex = await Assert.ThrowsAsync<RpcException>(() => PipeRpc.CallAsync(name, "boom", null, TimeSpan.FromSeconds(5)));
        Assert.Equal("denied", ex.Code);

        cts.Cancel();
        await server.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
