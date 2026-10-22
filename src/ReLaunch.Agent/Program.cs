using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using ReLaunch.Agent;
using ReLaunch.Core;
using ReLaunch.Core.Catalogue;
using ReLaunch.Core.Pipes;

// Headless agent: runs in the logged-in user's session and does the shell work for the service.
using var single = new Mutex(true, @"Global\reLAUNCH.Agent", out var isFirst);
if (!isFirst) return;

using var cts = new CancellationTokenSource();
AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

var sta = new StaWorker();
var entries = new Dictionary<string, AppEntry>();

var security = new PipeSecurity();
security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, PipeAccessRights.FullControl, AccessControlType.Allow));

NamedPipeServerStream CreateServer() => NamedPipeServerStreamAcl.Create(
    ProductInfo.AgentPipe, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
    PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);

AppEntry Lookup(string id) =>
    entries.TryGetValue(id, out var e) ? e : throw new RpcException("Unknown app.", "unknownApp");

async Task<object?> Handle(string method, JsonElement req, CancellationToken ct)
{
    switch (method)
    {
        case "ping":
            return "pong";
        case "session":
            // The input desktop cannot be opened from a user process while the secure desktop (lock screen) is up.
            var desktop = Native.OpenInputDesktop(0, false, Native.DESKTOP_SWITCHDESKTOP);
            if (desktop != IntPtr.Zero) Native.CloseDesktop(desktop);
            return new { state = desktop != IntPtr.Zero ? "active" : "locked", user = Environment.UserName };
        case "catalogue":
            var list = await sta.RunAsync(AppEnumerator.Enumerate);
            lock (entries)
            {
                entries.Clear();
                foreach (var e in list) entries[e.Id] = e;
            }
            return list;
        case "icon":
            var iconId = req.GetProperty("id").GetString()!;
            var png = await sta.RunAsync(() => IconExtractor.GetPng(Lookup(iconId).ParsingName));
            return new { png = png is null ? null : Convert.ToBase64String(png) };
        case "processes":
            return await Task.Run(ProcessOps.Snapshot, ct);
        case "launch":
            var launchId = req.GetProperty("id").GetString()!;
            var launchArgs = req.TryGetProperty("args", out var a) ? a.GetString() : null;
            var entry = Lookup(launchId);
            await Task.Run(() => ProcessOps.Launch(entry, launchArgs), ct);
            return new { ok = true };
        case "stop":
            var names = req.GetProperty("names").Deserialize<string[]>(Json.Options)!;
            var timeout = req.GetProperty("timeoutMs").GetInt32();
            await Task.Run(() => ProcessOps.Stop(names, timeout), ct);
            return new { ok = true };
        default:
            throw new RpcException($"Unknown method '{method}'.", "unknownMethod");
    }
}

await PipeRpc.ServeAsync(CreateServer, Handle, cts.Token);
