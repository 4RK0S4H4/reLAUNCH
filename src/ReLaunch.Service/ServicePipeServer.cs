using System.IO.Pipes;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using ReLaunch.Core;
using ReLaunch.Core.Actions;
using ReLaunch.Core.Catalogue;
using ReLaunch.Core.Contracts;
using ReLaunch.Core.Pipes;

namespace ReLaunch.Service;

/// <summary>
/// Named pipe for the desktop app (ACL: SYSTEM, Administrators and the interactive user).
/// The app can do everything the settings UI needs; none of it is reachable from the network.
/// </summary>
public sealed class ServicePipeServer(
    ControlService control,
    CatalogueService catalogue,
    AgentSupervisor supervisor,
    RelayClient relay,
    PairingService pairing,
    Database db) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken ct) => PipeRpc.ServeAsync(CreateServer, HandleAsync, ct);

    private static NamedPipeServerStream CreateServer()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.InteractiveSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(ProductInfo.ServicePipe, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
    }

    private ServiceState BuildState()
    {
        var settings = ServiceSettings.Load();
        var config = RelayConfig.Resolve();
        return new ServiceState(
            Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.1.0",
            Environment.MachineName,
            supervisor.SessionState,
            supervisor.IsReady,
            catalogue.Apps.Count,
            relay.State.ToString().ToLowerInvariant(),
            config?.HttpBase ?? settings.RelayUrl,
            config is not null || !string.IsNullOrEmpty(settings.PcKeyProtected),
            relay.LastHeartbeat,
            relay.LastError,
            settings.DebugApi,
            DataPaths.Root);
    }

    private async Task<object?> HandleAsync(string method, JsonElement p, CancellationToken ct)
    {
        switch (method)
        {
            case "getState":
                return BuildState();

            case "getApps":
                var favoritesOnly = p.ValueKind == JsonValueKind.Object && p.TryGetProperty("favoritesOnly", out var fo) && fo.GetBoolean();
                var query = p.ValueKind == JsonValueKind.Object && p.TryGetProperty("query", out var q) ? q.GetString() : null;
                return await control.GetAppsAsync(favoritesOnly, query, ct);

            case "getIcon":
                var png = control.GetIcon(p.GetProperty("id").GetString()!);
                return new { png = png is null ? null : Convert.ToBase64String(png) };

            case "act":
                var kind = Enum.Parse<ActionKind>(p.GetProperty("kind").GetString()!, ignoreCase: true);
                return await control.ActAsync(p.GetProperty("id").GetString()!, kind, "desktop", ct);

            case "setFavorites":
                control.SetFavorites(p.GetProperty("ids").Deserialize<string[]>(Json.Options) ?? []);
                return new { ok = true };

            case "getOverrides":
                return db.GetOverrides().Values.ToList();

            case "setOverride":
                db.SetOverride(p.Deserialize<AppOverride>(Json.Options) ?? throw new RpcException("Missing override.", "badRequest"));
                catalogue.ApplyOverrides();
                return new { ok = true };

            case "refreshCatalogue":
                catalogue.RequestRefresh();
                return new { ok = true };

            case "getActivity":
                return control.GetActivity(p.ValueKind == JsonValueKind.Object && p.TryGetProperty("limit", out var lim) ? lim.GetInt32() : 100);

            case "createPairing":
                return await pairing.CreateAsync(ct);

            case "listDevices":
                return await pairing.ListDevicesAsync(ct);

            case "revokeDevice":
                await pairing.RevokeAsync(p.GetProperty("id").GetString()!, ct);
                return new { ok = true };

            case "setRelayUrl":
                var settings = ServiceSettings.Load();
                settings.RelayUrl = (p.GetProperty("url").GetString() ?? "").Trim();
                settings.Save();
                relay.Reconfigure();
                return new { ok = true };

            case "generateKey":
                var key = PcKeyStore.Generate();
                var s2 = ServiceSettings.Load();
                s2.PcKeyProtected = PcKeyStore.Protect(key);
                s2.Save();
                relay.Reconfigure();
                return new { key };

            case "getKey":
                return new { key = PcKeyStore.Unprotect(ServiceSettings.Load().PcKeyProtected) };

            case "setDebugApi":
                var s3 = ServiceSettings.Load();
                s3.DebugApi = p.GetProperty("enabled").GetBoolean();
                s3.Save();
                return new { ok = true, restartRequired = true };

            default:
                throw new RpcException($"Unknown method '{method}'.", "unknownMethod");
        }
    }
}
