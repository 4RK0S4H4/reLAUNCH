using System.Security.Cryptography;
using ReLaunch.Core;
using ReLaunch.Core.Catalogue;
using ReLaunch.Core.Pipes;

namespace ReLaunch.Service;

/// <summary>Holds the merged app catalogue; refreshes on agent start, every 10 minutes and on demand; caches icons.</summary>
public sealed class CatalogueService(AgentClient agent, AgentSupervisor supervisor, Database db, EventHub events, ILogger<CatalogueService> log)
    : BackgroundService
{
    private readonly SemaphoreSlim _signal = new(0);
    private readonly Dictionary<string, string> _iconHashes = new();
    private IReadOnlyList<ManagedApp> _apps = [];
    private List<AppEntry> _entries = [];

    public IReadOnlyList<ManagedApp> Apps => _apps;

    public ManagedApp? Find(string id) => _apps.FirstOrDefault(a => a.Id == id);

    public string? IconHash(string id)
    {
        lock (_iconHashes) return _iconHashes.TryGetValue(id, out var h) ? h : null;
    }

    public byte[]? ReadIcon(string id)
    {
        var path = IconPath(id);
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    public void RequestRefresh() => _signal.Release();

    /// <summary>Re-merge overrides without asking the agent again.</summary>
    public void ApplyOverrides() => _apps = CatalogueBuilder.Build(_entries, db.GetOverrides());

    private static string IconPath(string id) => Path.Combine(DataPaths.IconDir, id + ".png");

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        LoadIconHashes();
        supervisor.AgentReady += RequestRefresh;
        while (!ct.IsCancellationRequested)
        {
            if (supervisor.IsReady)
            {
                try
                {
                    await RefreshAsync(ct);
                    await FetchMissingIconsAsync(ct);
                }
                catch (Exception ex) when (ex is RpcException or IOException)
                {
                    log.LogWarning("Catalogue refresh failed: {Message}", ex.Message);
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);
                    continue;
                }
            }
            try { await _signal.WaitAsync(supervisor.IsReady ? TimeSpan.FromMinutes(10) : TimeSpan.FromSeconds(5), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        var entries = await agent.CallAsync<List<AppEntry>>("catalogue", null, TimeSpan.FromSeconds(60), ct) ?? [];
        _entries = entries;
        ApplyOverrides();
        log.LogInformation("Catalogue refreshed: {Count} apps", _apps.Count);
        events.Publish(new { type = "catalogue", count = _apps.Count });
    }

    private async Task FetchMissingIconsAsync(CancellationToken ct)
    {
        foreach (var app in _apps)
        {
            if (ct.IsCancellationRequested) return;
            if (IconHash(app.Id) is not null) continue;
            try
            {
                var res = await agent.CallAsync("icon", new { id = app.Id }, TimeSpan.FromSeconds(15), ct);
                if (res.TryGetProperty("png", out var p) && p.GetString() is { Length: > 0 } b64)
                {
                    var bytes = Convert.FromBase64String(b64);
                    await File.WriteAllBytesAsync(IconPath(app.Id), bytes, ct);
                    SetHash(app.Id, bytes);
                }
            }
            catch (RpcException ex)
            {
                log.LogDebug("No icon for {App}: {Message}", app.Name, ex.Message);
            }
        }
    }

    private void LoadIconHashes()
    {
        foreach (var file in Directory.EnumerateFiles(DataPaths.IconDir, "*.png"))
            SetHash(Path.GetFileNameWithoutExtension(file), File.ReadAllBytes(file));
    }

    private void SetHash(string id, byte[] png)
    {
        var hash = Convert.ToHexString(SHA1.HashData(png)).ToLowerInvariant()[..12];
        lock (_iconHashes) _iconHashes[id] = hash;
    }
}
