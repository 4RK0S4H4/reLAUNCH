using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ReLaunch.Service;

/// <summary>Starts a process inside the active console session as the logged-in user (service runs as SYSTEM).</summary>
internal static class SessionLauncher
{
    public const uint NoSession = 0xFFFFFFFF;
    private const uint MaximumAllowed = 0x02000000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint CreateNoWindow = 0x08000000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("kernel32.dll")] private static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("wtsapi32.dll", SetLastError = true)] private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(IntPtr existing, uint access, IntPtr attrs, int impersonationLevel, int tokenType, out IntPtr newToken);
    [DllImport("userenv.dll", SetLastError = true)] private static extern bool CreateEnvironmentBlock(out IntPtr env, IntPtr token, bool inherit);
    [DllImport("userenv.dll")] private static extern bool DestroyEnvironmentBlock(IntPtr env);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUserW(IntPtr token, string? app, StringBuilder? cmd, IntPtr pa, IntPtr ta, bool inherit,
        uint flags, IntPtr env, string? cwd, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);

    public static uint ActiveSessionId() => WTSGetActiveConsoleSessionId();

    /// <summary>True when someone is logged on to that session (the query needs SYSTEM rights).</summary>
    public static bool HasUser(uint sessionId)
    {
        if (sessionId == NoSession) return false;
        if (!WTSQueryUserToken(sessionId, out var token)) return false;
        CloseHandle(token);
        return true;
    }

    public static Process LaunchInSession(uint sessionId, string exePath, string arguments)
    {
        if (!WTSQueryUserToken(sessionId, out var userToken)) throw new Win32Exception(Marshal.GetLastWin32Error());
        IntPtr primary = IntPtr.Zero, env = IntPtr.Zero;
        try
        {
            // 2 = SecurityImpersonation, 1 = TokenPrimary
            if (!DuplicateTokenEx(userToken, MaximumAllowed, IntPtr.Zero, 2, 1, out primary))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!CreateEnvironmentBlock(out env, primary, false))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>(), lpDesktop = @"winsta0\default" };
            var cmd = new StringBuilder($"\"{exePath}\" {arguments}");
            if (!CreateProcessAsUserW(primary, null, cmd, IntPtr.Zero, IntPtr.Zero, false,
                    CreateUnicodeEnvironment | CreateNoWindow, env, Path.GetDirectoryName(exePath), ref si, out var pi))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            CloseHandle(pi.hThread);
            CloseHandle(pi.hProcess);
            return Process.GetProcessById(pi.dwProcessId);
        }
        finally
        {
            if (env != IntPtr.Zero) DestroyEnvironmentBlock(env);
            if (primary != IntPtr.Zero) CloseHandle(primary);
            CloseHandle(userToken);
        }
    }
}
