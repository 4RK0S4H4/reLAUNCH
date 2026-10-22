using System.Text.Json;
using ReLaunch.Core;
using ReLaunch.Core.Catalogue;
using ReLaunch.Core.Contracts;
using ReLaunch.Core.Pipes;

namespace ReLaunch.App;

/// <summary>Typed calls to the service over its named pipe. Throws RpcException when the service is unreachable.</summary>
internal static class ServiceClient
{
    private static readonly TimeSpan Short = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(45);

    private static Task<T?> Call<T>(string method, object? args = null, TimeSpan? timeout = null) =>
        PipeRpc.CallAsync<T>(ProductInfo.ServicePipe, method, args, timeout ?? Short);

    private static async Task Call(string method, object? args = null, TimeSpan? timeout = null) =>
        await PipeRpc.CallAsync(ProductInfo.ServicePipe, method, args, timeout ?? Short);

    public static Task<ServiceState?> GetStateAsync() => Call<ServiceState>("getState");

    public static async Task<List<AppDto>> GetAppsAsync() =>
        await Call<List<AppDto>>("getApps", new { favoritesOnly = false }) ?? [];

    public static async Task<string?> GetIconAsync(string id)
    {
        var el = await PipeRpc.CallAsync(ProductInfo.ServicePipe, "getIcon", new { id }, Short);
        return el.ValueKind == JsonValueKind.Object && el.TryGetProperty("png", out var p) ? p.GetString() : null;
    }

    public static async Task<ActionResultDto> ActAsync(string id, string kind) =>
        await Call<ActionResultDto>("act", new { id, kind }, Long) ?? new ActionResultDto(false, "failed", "error", "No answer from the service.");

    public static Task SetFavoritesAsync(IEnumerable<string> ids) => Call("setFavorites", new { ids });

    public static async Task<List<AppOverride>> GetOverridesAsync() =>
        await Call<List<AppOverride>>("getOverrides") ?? [];

    public static Task SetOverrideAsync(AppOverride o) => Call("setOverride", o);

    public static Task RefreshCatalogueAsync() => Call("refreshCatalogue");

    public static async Task<List<ActivityDto>> GetActivityAsync() =>
        await Call<List<ActivityDto>>("getActivity", new { limit = 100 }) ?? [];

    public static Task<PairingInfo?> CreatePairingAsync() => Call<PairingInfo>("createPairing", null, TimeSpan.FromSeconds(20));

    public static async Task<List<DeviceInfo>> ListDevicesAsync() =>
        await Call<List<DeviceInfo>>("listDevices", null, TimeSpan.FromSeconds(20)) ?? [];

    public static Task RevokeDeviceAsync(string id) => Call("revokeDevice", new { id }, TimeSpan.FromSeconds(20));

    public static Task SetRelayUrlAsync(string url) => Call("setRelayUrl", new { url });

    public static async Task<string?> GenerateKeyAsync()
    {
        var el = await PipeRpc.CallAsync(ProductInfo.ServicePipe, "generateKey", null, Short);
        return el.TryGetProperty("key", out var k) ? k.GetString() : null;
    }

    public static async Task<string?> GetKeyAsync()
    {
        var el = await PipeRpc.CallAsync(ProductInfo.ServicePipe, "getKey", null, Short);
        return el.ValueKind == JsonValueKind.Object && el.TryGetProperty("key", out var k) ? k.GetString() : null;
    }

    public static Task SetDebugApiAsync(bool enabled) => Call("setDebugApi", new { enabled });
}
