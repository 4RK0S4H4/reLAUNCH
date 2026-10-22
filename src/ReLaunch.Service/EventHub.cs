using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using ReLaunch.Core;

namespace ReLaunch.Service;

/// <summary>Fan-out of status / action events to SSE clients (and later the relay).</summary>
public sealed class EventHub
{
    private readonly ConcurrentDictionary<Guid, Channel<string>> _subscribers = new();

    public event Action<string>? Published;

    public void Publish(object evt)
    {
        var json = JsonSerializer.Serialize(evt, Json.Options);
        foreach (var ch in _subscribers.Values) ch.Writer.TryWrite(json);
        Published?.Invoke(json);
    }

    public async IAsyncEnumerable<string> SubscribeAsync([EnumeratorCancellation] CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(200) { FullMode = BoundedChannelFullMode.DropOldest });
        _subscribers[id] = channel;
        try
        {
            await foreach (var msg in channel.Reader.ReadAllAsync(ct)) yield return msg;
        }
        finally
        {
            _subscribers.TryRemove(id, out _);
        }
    }
}
