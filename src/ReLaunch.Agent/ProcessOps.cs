using System.ComponentModel;
using System.Diagnostics;
using ReLaunch.Core.Catalogue;
using ReLaunch.Core.Pipes;
using static ReLaunch.Agent.Native;

namespace ReLaunch.Agent;

internal static class ProcessOps
{
    private const int ErrorElevationRequired = 740;

    public static List<ProcessSnapshot> Snapshot()
    {
        var list = new List<ProcessSnapshot>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                DateTimeOffset? start = null;
                long ws = 0;
                try { start = p.StartTime; ws = p.WorkingSet64; }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { }
                list.Add(new ProcessSnapshot(p.ProcessName, p.Id, start, ws));
            }
        }
        return list;
    }

    public static void Launch(AppEntry entry, string? args)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(args) && entry.ExePath is { } exe && File.Exists(exe))
            {
                Process.Start(new ProcessStartInfo(exe, args)
                {
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(exe) ?? "",
                })?.Dispose();
            }
            else
            {
                Process.Start(new ProcessStartInfo("shell:AppsFolder\\" + entry.ParsingName) { UseShellExecute = true })?.Dispose();
            }
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorElevationRequired)
        {
            throw new RpcException($"{entry.Name} needs administrator rights, which reLAUNCH cannot start.", "elevationRequired");
        }
        catch (Win32Exception ex)
        {
            throw new RpcException($"Could not start {entry.Name}: {ex.Message}", "launchFailed");
        }
    }

    /// <summary>Ask every window of the processes to close, wait, then kill what is left (and their children).</summary>
    public static void Stop(string[] names, int timeoutMs)
    {
        var procs = Find(names);
        if (procs.Count == 0) return;

        var pids = procs.Select(p => (uint)p.Id).ToHashSet();
        var posted = PostCloseToWindows(pids);
        if (posted > 0)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline && procs.Any(p => !p.HasExited)) Thread.Sleep(100);
        }

        foreach (var p in procs.Where(p => !p.HasExited))
        {
            try { p.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { }
        }
        foreach (var p in procs) p.Dispose();

        // A supervisor (e.g. a tray app) may have respawned something while we were killing.
        Thread.Sleep(300);
        foreach (var p in Find(names))
        {
            try { p.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { }
            p.Dispose();
        }
    }

    private static List<Process> Find(string[] names)
    {
        var result = new List<Process>();
        foreach (var name in names)
            result.AddRange(Process.GetProcessesByName(CatalogueBuilder.NormalizeProcessName(name)));
        return result;
    }

    private static int PostCloseToWindows(HashSet<uint> pids)
    {
        var count = 0;
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pids.Contains(pid) && IsWindowVisible(hwnd) && PostMessageW(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero))
                count++;
            return true;
        }, IntPtr.Zero);
        return count;
    }
}
