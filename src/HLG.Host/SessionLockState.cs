using System.Diagnostics;
using System.Runtime.InteropServices;

// Lock state belongs to a Windows session, not to the machine as a whole.
// Unknown is deliberately distinct from unlocked: a failed query must never
// authorize input on a secure desktop.
enum SessionLockState { Unknown, Unlocked, Locked }

static class SessionLock
{
    const int SessionInfoEx = 25;
    static readonly int DataOffset = (int)Marshal.OffsetOf<SessionInfo>(nameof(SessionInfo.Data));

    public static uint CurrentSessionId => (uint)Process.GetCurrentProcess().SessionId;
    public static uint ActiveConsoleSessionId => WTSGetActiveConsoleSessionId();

    public static SessionLockState Query(uint sessionId)
    {
        if (!OperatingSystem.IsWindows() || !WTSQuerySessionInformation(IntPtr.Zero, sessionId, SessionInfoEx, out var buffer, out var size)) return SessionLockState.Unknown;
        try {
            // The returned structure's union is aligned to eight bytes. Only the
            // first three DWORDs of its level-1 member are needed here.
            if (size < DataOffset + 12 || Marshal.ReadInt32(buffer) != 1 || unchecked((uint)Marshal.ReadInt32(buffer, DataOffset)) != sessionId) return SessionLockState.Unknown;
            return Marshal.ReadInt32(buffer, DataOffset + 8) switch { 0 => SessionLockState.Locked, 1 => SessionLockState.Unlocked, _ => SessionLockState.Unknown };
        } finally { WTSFreeMemory(buffer); }
    }

    [StructLayout(LayoutKind.Sequential)] struct SessionInfo { public int Level; public SessionInfoLevel1 Data; }
    // The trailing 64-bit member establishes the native union's alignment.
    [StructLayout(LayoutKind.Sequential)] struct SessionInfoLevel1 { public int Id, State, Flags; public long Alignment; }

    [DllImport("wtsapi32.dll", EntryPoint = "WTSQuerySessionInformationW", SetLastError = true)]
    static extern bool WTSQuerySessionInformation(IntPtr server, uint sessionId, int infoClass, out IntPtr buffer, out int bytesReturned);
    [DllImport("wtsapi32.dll")] static extern void WTSFreeMemory(IntPtr buffer);
    [DllImport("kernel32.dll")] static extern uint WTSGetActiveConsoleSessionId();
}
