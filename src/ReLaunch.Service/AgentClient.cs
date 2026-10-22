using System.Text.Json;
using ReLaunch.Core;
using ReLaunch.Core.Pipes;

namespace ReLaunch.Service;

public sealed class AgentClient
{
    public Task<JsonElement> CallAsync(string method, object? args = null, TimeSpan? timeout = null, CancellationToken ct = default) =>
        PipeRpc.CallAsync(ProductInfo.AgentPipe, method, args, timeout ?? TimeSpan.FromSeconds(10), ct);

    public Task<T?> CallAsync<T>(string method, object? args = null, TimeSpan? timeout = null, CancellationToken ct = default) =>
        PipeRpc.CallAsync<T>(ProductInfo.AgentPipe, method, args, timeout ?? TimeSpan.FromSeconds(10), ct);
}
