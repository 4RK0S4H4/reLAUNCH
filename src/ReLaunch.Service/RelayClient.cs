using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using ReLaunch.Core;
using ReLaunch.Core.Actions;
using ReLaunch.Core.Pipes;

namespace ReLaunch.Service;

public enum RelayState { NotConfigured, Connecting, Connected, Reconnecting, Rejected }

/// <summary>
/// Keeps an outbound WebSocket open to the Cloudflare relay, answers its requests (start / stop / restart /
/// favorites / icons), pushes the catalogue and status events, and sends a heartbeat every 30 seconds.
/// Protocol: relay/src/protocol.ts.
/// </summary>
public sealed class RelayClient(ControlService control, AgentSupervisor supervisor, EventHub events, ILogger<RelayClient> log)
    : BackgroundService
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _connectionCts;
    private int _nextId;
    private int _pushScheduled;

    public RelayState State { get; private set; } = RelayState.NotConfigured;
    public DateTimeOffset? LastHeartbeat { get; private set; }
    public string? LastError { get; private set; }
    public bool IsConnected => _ws is { State: WebSocketState.Open };

    /// <summary>Drop the current connection so new settings take effect.</summary>
    public void Reconfigure() => _connectionCts?.Cancel();

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        events.Published += OnLocalEvent;
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            var config = RelayConfig.Resolve();
            if (config is null)
            {
                State = RelayState.NotConfigured;
                await DelayAsync(TimeSpan.FromSeconds(3), ct);
                continue;
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _connectionCts = cts;
            State = failures == 0 ? RelayState.Connecting : RelayState.Reconnecting;
            try
            {
                await RunConnectionAsync(config, cts.Token, () => failures = 0);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                failures = 0; // reconfigured: reconnect right away
                continue;
            }
            catch (Exception ex) when (ex is WebSocketException or IOException or HttpRequestException)
            {
                LastError = ex.Message;
                log.LogWarning("Relay connection failed: {Message}", ex.Message);
            }
            if (ct.IsCancellationRequested) break;

            if (State == RelayState.Rejected)
            {
                await DelayAsync(TimeSpan.FromSeconds(60), ct);
                continue;
            }
            failures++;
            State = RelayState.Reconnecting;
            var seconds = Math.Min(60, 1 << Math.Min(failures, 6));
            await DelayAsync(TimeSpan.FromSeconds(seconds) + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 1000)), ct);
        }
        events.Published -= OnLocalEvent;
    }

    private static async Task DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try { await Task.Delay(delay, ct); }
        catch (OperationCanceledException) { }
    }

    private async Task RunConnectionAsync(RelayConfig config, CancellationToken token, Action onConnected)
    {
        using var ws = new ClientWebSocket();
        ws.Options.CollectHttpResponseDetails = true;
        ws.Options.SetRequestHeader("Authorization", $"Bearer {config.Key}");
        try
        {
            await ws.ConnectAsync(config.WsUri, token);
        }
        catch (WebSocketException) when (ws.HttpStatusCode == HttpStatusCode.Unauthorized)
        {
            State = RelayState.Rejected;
            LastError = "The relay rejected the PC key.";
            return;
        }

        _ws = ws;
        State = RelayState.Connected;
        LastError = null;
        onConnected();
        log.LogInformation("Connected to relay {Url}", config.HttpBase);

        try
        {
            await SendEventAsync("hello", new { pcName = Environment.MachineName, session = supervisor.SessionState }, token);
            await PushCatalogueAsync(token);
            using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            var heartbeat = HeartbeatLoopAsync(heartbeatCts.Token);
            try { await ReceiveLoopAsync(ws, token); }
            finally
            {
                heartbeatCts.Cancel();
                _ws = null;
                foreach (var p in _pending.Values) p.TrySetException(new RpcException("Relay connection closed.", "relayOffline"));
                _pending.Clear();
                await heartbeat;
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
        finally
        {
            _ws = null;
            if (ws.State == WebSocketState.Open)
            {
                try { await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); }
                catch (WebSocketException) { }
            }
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(HeartbeatInterval, token);
                await SendEventAsync("heartbeat", new { pcName = Environment.MachineName, session = supervisor.SessionState }, token);
                LastHeartbeat = DateTimeOffset.UtcNow;
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken token)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (ws.State == WebSocketState.Open && !token.IsCancellationRequested)
        {
            message.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                if (result.MessageType == WebSocketMessageType.Close) return;
                message.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            if (result.MessageType != WebSocketMessageType.Text) continue;
            JsonElement root;
            try { root = JsonDocument.Parse(message.ToArray()).RootElement.Clone(); }
            catch (JsonException) { continue; }
            Dispatch(root, token);
        }
    }

    private void Dispatch(JsonElement root, CancellationToken token)
    {
        var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
        switch (type)
        {
            case "request":
                _ = Task.Run(() => HandleRequestAsync(root, token), CancellationToken.None);
                break;
            case "response":
                if (root.TryGetProperty("id", out var idEl) && idEl.GetString() is { } id && _pending.TryRemove(id, out var tcs))
                {
                    var ok = root.TryGetProperty("ok", out var okEl) && okEl.GetBoolean();
                    if (ok)
                        tcs.TrySetResult(root.TryGetProperty("result", out var r) ? r.Clone() : default);
                    else
                        tcs.TrySetException(new RpcException(
                            root.TryGetProperty("error", out var e) ? e.GetString() ?? "Request failed." : "Request failed.",
                            root.TryGetProperty("code", out var c) ? c.GetString() : null));
                }
                break;
            case "event":
                if (root.TryGetProperty("name", out var nameEl) && nameEl.GetString() == "devicePaired")
                    log.LogInformation("A phone was paired: {Data}", root.TryGetProperty("data", out var d) ? d.ToString() : "");
                break;
        }
    }

    private async Task HandleRequestAsync(JsonElement root, CancellationToken token)
    {
        var id = root.GetProperty("id").GetString()!;
        var method = root.TryGetProperty("method", out var m) ? m.GetString() ?? "" : "";
        var args = root.TryGetProperty("params", out var p) ? p : default;
        try
        {
            var result = await ExecuteRequestAsync(method, args, token);
            await SendAsync(new { id, type = "response", ok = true, result }, token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            var code = ex is RpcException rpc ? rpc.Code : "error";
            await SendAsync(new { id, type = "response", ok = false, error = ex.Message, code }, token);
        }
    }

    private async Task<object?> ExecuteRequestAsync(string method, JsonElement args, CancellationToken ct)
    {
        switch (method)
        {
            case "start" or "stop" or "restart":
                var kind = Enum.Parse<ActionKind>(method, ignoreCase: true);
                var appId = args.GetProperty("appId").GetString()!;
                var device = args.TryGetProperty("device", out var d) ? d.GetString() ?? "phone" : "phone";
                return await control.ActAsync(appId, kind, device, ct);
            case "setFavorites":
                control.SetFavorites(args.GetProperty("ids").Deserialize<string[]>(Json.Options) ?? []);
                return new { ok = true };
            case "getIcon":
                var png = control.GetIcon(args.GetProperty("id").GetString()!);
                return new { png = png is null ? null : Convert.ToBase64String(png) };
            default:
                throw new RpcException($"Unknown method '{method}'.", "unknownMethod");
        }
    }

    // ───────────── outbound ─────────────

    private async Task SendAsync(object frame, CancellationToken ct)
    {
        var ws = _ws;
        if (ws is not { State: WebSocketState.Open }) return;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(frame, Json.Options);
        await _sendLock.WaitAsync(ct);
        try { await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct); }
        finally { _sendLock.Release(); }
    }

    private Task SendEventAsync(string name, object data, CancellationToken ct) =>
        SendAsync(new { type = "event", name, data }, ct);

    private async Task PushCatalogueAsync(CancellationToken ct)
    {
        var apps = await control.GetAppsAsync(false, null, ct);
        await SendEventAsync("catalogue", new { apps, favorites = control.GetFavoriteIds() }, ct);
    }

    private void OnLocalEvent(string json)
    {
        if (!IsConnected) return;
        JsonObject? node;
        try { node = JsonNode.Parse(json)?.AsObject(); }
        catch (JsonException) { return; }
        var type = node?["type"]?.GetValue<string>();
        switch (type)
        {
            case "status" or "action" or "session":
                node!.Remove("type");
                _ = SendEventAsync(type, node, CancellationToken.None).ContinueWith(_ => { }, TaskScheduler.Default);
                break;
            case "catalogue" or "favorites":
                SchedulePush();
                break;
        }
    }

    private void SchedulePush()
    {
        if (Interlocked.Exchange(ref _pushScheduled, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(500);
            Interlocked.Exchange(ref _pushScheduled, 0);
            try { await PushCatalogueAsync(CancellationToken.None); }
            catch (Exception ex) when (ex is WebSocketException or RpcException or OperationCanceledException) { }
        });
    }

    /// <summary>PC → relay request (pairing codes, device list, revocation).</summary>
    public async Task<JsonElement> RequestAsync(string method, object? args, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        if (!IsConnected) throw new RpcException("The PC is not connected to the relay.", "relayOffline");
        var id = "pc-" + Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            await SendAsync(new { id, type = "request", method, @params = args }, ct);
            return await tcs.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(15), ct);
        }
        catch (TimeoutException)
        {
            throw new RpcException($"The relay did not answer '{method}'.", "timeout");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }
}
