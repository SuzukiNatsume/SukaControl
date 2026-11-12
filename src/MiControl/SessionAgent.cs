using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace SukaControl;

// Session 0 cannot display UI. The service starts a limited user-token bridge with fixed arguments.
internal sealed class SessionAgent(string ownerSid) : IDisposable
{
    Process? agent;
    public void EnsureRunning()
    {
        uint session = WTSGetActiveConsoleSessionId();
        if (session == uint.MaxValue) { Dispose(); return; }
        if (agent is { HasExited: false } && agent.SessionId == session) return;
        Dispose();
        if (!WTSQueryUserToken(session, out var token)) return;
        using (token)
        {
            using var identity = new WindowsIdentity(token.DangerousGetHandle());
            if (identity.User?.Value != ownerSid) return;
            if (!CreateEnvironmentBlock(out var environment, token, false)) throw new Win32Exception();
            try
            {
                var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>(), Desktop = @"winsta0\default" };
                string executable = Path.Combine(ServiceInstaller.InstallDirectory, "SukaService.exe");
                if (!CreateProcessAsUser(token, executable, new System.Text.StringBuilder($"\"{executable}\" --key-agent"), IntPtr.Zero, IntPtr.Zero, false, 0x400, environment, ServiceInstaller.InstallDirectory, ref startup, out var result)) throw new Win32Exception();
                try { agent = Process.GetProcessById((int)result.ProcessId); Store.Log($"Key agent started: session {session}, PID {result.ProcessId}"); }
                finally { CloseHandle(result.Thread); CloseHandle(result.Process); }
            }
            finally { DestroyEnvironmentBlock(environment); }
        }
    }
    public void Dispose()
    {
        if (agent == null) return;
        try { if (!agent.HasExited) { agent.Kill(); agent.WaitForExit(3000); } } catch (InvalidOperationException) { }
        agent.Dispose(); agent = null;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct StartupInfo
    {
        public int Size; public string? Reserved, Desktop, Title; public uint X, Y, XSize, YSize, XChars, YChars, Fill, Flags; public ushort Show, ReservedSize; public IntPtr ReservedData, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)] struct ProcessInfo { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
    [DllImport("kernel32.dll")] static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("wtsapi32.dll", SetLastError = true)] static extern bool WTSQueryUserToken(uint session, out SafeAccessTokenHandle token);
    [DllImport("userenv.dll", SetLastError = true)] static extern bool CreateEnvironmentBlock(out IntPtr environment, SafeAccessTokenHandle token, bool inherit);
    [DllImport("userenv.dll")] static extern bool DestroyEnvironmentBlock(IntPtr environment);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool CreateProcessAsUser(SafeAccessTokenHandle token, string application, System.Text.StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes, bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfo startup, out ProcessInfo process);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
}
