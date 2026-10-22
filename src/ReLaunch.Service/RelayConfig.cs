using System.Security.Cryptography;
using System.Text;
using ReLaunch.Core;

namespace ReLaunch.Service;

/// <summary>Where to connect and with which key. Environment variables override settings.json (for tests and dev).</summary>
public sealed record RelayConfig(Uri WsUri, string HttpBase, string Key)
{
    public static RelayConfig? Resolve()
    {
        var settings = ServiceSettings.Load();
        var url = Environment.GetEnvironmentVariable("RELAUNCH_RELAY_URL");
        if (string.IsNullOrWhiteSpace(url)) url = settings.RelayUrl;
        var key = Environment.GetEnvironmentVariable("RELAUNCH_PC_KEY");
        if (string.IsNullOrWhiteSpace(key)) key = PcKeyStore.Unprotect(settings.PcKeyProtected);
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(key)) return null;

        if (!Uri.TryCreate(url.Trim().TrimEnd('/'), UriKind.Absolute, out var http)) return null;
        var wsScheme = http.Scheme switch { "https" => "wss", "http" => "ws", _ => null };
        if (wsScheme is null) return null;
        var baseUrl = http.GetLeftPart(UriPartial.Authority);
        return new RelayConfig(new Uri($"{wsScheme}://{http.Authority}/pc/connect"), baseUrl, key.Trim());
    }
}

/// <summary>PC key: 32 random bytes (base64url), stored DPAPI-protected (machine scope) in settings.json.</summary>
public static class PcKeyStore
{
    public static string Generate() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    public static string Protect(string key) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(key), null, DataProtectionScope.LocalMachine));

    public static string? Unprotect(string? protectedKey)
    {
        if (string.IsNullOrEmpty(protectedKey)) return null;
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedKey), null, DataProtectionScope.LocalMachine));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }
}
