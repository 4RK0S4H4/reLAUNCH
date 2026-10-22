using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace ReLaunch.Core.Pipes;

public sealed record RpcRequest(string Method, JsonElement Params);

public sealed record RpcResponse(bool Ok, JsonElement? Result, string? Error, string? Code);

/// <summary>Thrown by handlers (and by callers on a failed response) with a machine-readable code.</summary>
public sealed class RpcException(string message, string? code = null) : Exception(message)
{
    public string? Code { get; } = code;
}

/// <summary>
/// One request per connection: the client writes a JSON line, the server answers with a JSON line.
/// Used between service and agent, and between service and desktop app.
/// </summary>
public static class PipeRpc
{
    private static readonly UTF8Encoding Utf8 = new(false);

    public static async Task<JsonElement> CallAsync(string pipeName, string method, object? args, TimeSpan timeout, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(cts.Token);
            using var writer = new StreamWriter(pipe, Utf8, 4096, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(pipe, Utf8, false, 4096, leaveOpen: true);

            var req = new RpcRequest(method, JsonSerializer.SerializeToElement(args, Json.Options));
            await writer.WriteLineAsync(JsonSerializer.Serialize(req, Json.Options).AsMemory(), cts.Token);
            var line = await reader.ReadLineAsync(cts.Token) ?? throw new RpcException("Connection closed.", "closed");
            var res = JsonSerializer.Deserialize<RpcResponse>(line, Json.Options)!;
            if (!res.Ok) throw new RpcException(res.Error ?? "Request failed.", res.Code);
            return res.Result ?? default;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new RpcException($"'{method}' timed out.", "timeout");
        }
    }

    public static async Task<T?> CallAsync<T>(string pipeName, string method, object? args, TimeSpan timeout, CancellationToken ct = default)
    {
        var el = await CallAsync(pipeName, method, args, timeout, ct);
        return el.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? default : el.Deserialize<T>(Json.Options);
    }

    public delegate Task<object?> Handler(string method, JsonElement args, CancellationToken ct);

    public static async Task ServeAsync(Func<NamedPipeServerStream> createServer, Handler handler, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream server;
            try { server = createServer(); }
            catch (IOException) { await Task.Delay(500, ct); continue; }

            try { await server.WaitForConnectionAsync(ct); }
            catch (OperationCanceledException) { server.Dispose(); return; }
            catch (IOException) { server.Dispose(); continue; }

            _ = Task.Run(() => HandleAsync(server, handler, ct), CancellationToken.None);
        }
    }

    private static async Task HandleAsync(NamedPipeServerStream pipe, Handler handler, CancellationToken ct)
    {
        await using (pipe)
        {
            try
            {
                using var reader = new StreamReader(pipe, Utf8, false, 4096, leaveOpen: true);
                using var writer = new StreamWriter(pipe, Utf8, 4096, leaveOpen: true) { AutoFlush = true };
                var line = await reader.ReadLineAsync(ct);
                if (line is null) return;

                RpcResponse response;
                try
                {
                    var req = JsonSerializer.Deserialize<RpcRequest>(line, Json.Options)!;
                    var result = await handler(req.Method, req.Params, ct);
                    response = new RpcResponse(true, JsonSerializer.SerializeToElement(result, Json.Options), null, null);
                }
                catch (RpcException ex)
                {
                    response = new RpcResponse(false, null, ex.Message, ex.Code);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    response = new RpcResponse(false, null, ex.Message, "error");
                }
                await writer.WriteLineAsync(JsonSerializer.Serialize(response, Json.Options).AsMemory(), ct);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
                // Peer went away; nothing to answer.
            }
        }
    }
}
