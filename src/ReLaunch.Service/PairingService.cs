using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ReLaunch.Core;
using ReLaunch.Core.Pipes;

namespace ReLaunch.Service;

public sealed record PairingInfo(string Code, string Url, long ExpiresAt);

public sealed record DeviceInfo(string Id, string Name, long CreatedAt, long? LastSeen);

/// <summary>One-time pairing codes (valid 5 minutes, sent hashed to the relay) and the phone device list.</summary>
public sealed class PairingService(RelayClient relay)
{
    // Crockford base32 without ambiguous characters.
    private const string Alphabet = "ABCDEFGHJKMNPQRSTVWXYZ23456789";
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    public async Task<PairingInfo> CreateAsync(CancellationToken ct)
    {
        var config = RelayConfig.Resolve() ?? throw new RpcException("Remote access is not set up yet.", "notConfigured");
        var code = new string(Enumerable.Range(0, 10).Select(_ => Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]).ToArray());
        var expiresAt = DateTimeOffset.UtcNow.Add(Lifetime).ToUnixTimeMilliseconds();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code))).ToLowerInvariant();
        await relay.RequestAsync("registerPairingCode", new { codeHash = hash, expiresAt }, ct: ct);
        return new PairingInfo(code, $"{config.HttpBase}/#pair={code}", expiresAt);
    }

    public async Task<List<DeviceInfo>> ListDevicesAsync(CancellationToken ct)
    {
        var res = await relay.RequestAsync("listDevices", new { }, ct: ct);
        return res.Deserialize<List<DeviceInfo>>(Json.Options) ?? [];
    }

    public Task RevokeAsync(string deviceId, CancellationToken ct) =>
        relay.RequestAsync("revokeDevice", new { deviceId }, ct: ct);
}
