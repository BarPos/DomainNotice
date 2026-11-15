using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DomainNotice.Agent.Platform;

/// <summary>Win32 calls used by the service (WTS, process creation in user sessions, pipes, DPAPI).</summary>
internal static class NativeMethods
{
    public static readonly IntPtr WtsCurrentServerHandle = IntPtr.Zero;

    // ---- WTS ---------------------------------------------------------------------------------

    public enum WtsConnectState
    {
        Active = 0,
        Connected = 1,
        ConnectQuery = 2,
        Shadow = 3,
        Disconnected = 4,
        Idle = 5,
        Listen = 6,
        Reset = 7,
        Down = 8,
        Init = 9,
    }

    public enum WtsInfoClass
    {
        UserName = 5,
        DomainName = 7,
        ConnectState = 8,
        SessionInfoEx = 25,
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WtsSessionInfo
    {
        public int SessionId;
        public IntPtr WinStationName;
        public WtsConnectState State;
    }

    /// <summary>WTSINFOEX_LEVEL1_W. Names here are truncated (20 chars); read them with WTSUserName/WTSDomainName.</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WtsInfoExLevel1
    {
        public int SessionId;
        public WtsConnectState SessionState;
        public int SessionFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 33)] public string WinStationName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 21)] public string UserName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 18)] public string DomainName;
        public long LogonTime;
        public long ConnectTime;
        public long DisconnectTime;
        public long LastInputTime;
        public long CurrentTime;
        public int IncomingBytes;
        public int OutgoingBytes;
        public int IncomingFrames;
        public int OutgoingFrames;
        public int IncomingCompressedBytes;
        public int OutgoingCompressedBytes;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WtsInfoEx
    {
        public int Level;
        public WtsInfoExLevel1 Data;
    }

    public const int WtsSessionStateLock = 0;
    public const int WtsSessionStateUnlock = 1;

    [DllImport("wtsapi32.dll", SetLastError = true, EntryPoint = "WTSEnumerateSessionsW")]
    public static extern bool WTSEnumerateSessions(IntPtr server, int reserved, int version, out IntPtr sessionInfo, out int count);

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "WTSQuerySessionInformationW")]
    public static extern bool WTSQuerySessionInformation(IntPtr server, int sessionId, WtsInfoClass infoClass, out IntPtr buffer, out int bytesReturned);

    [DllImport("wtsapi32.dll")]
    public static extern void WTSFreeMemory(IntPtr memory);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    public static extern bool WTSQueryUserToken(int sessionId, out IntPtr token);

    // ---- processes ---------------------------------------------------------------------------

    public const uint CreateUnicodeEnvironment = 0x00000400;
    public const uint CreateNoWindow = 0x08000000;
    public const uint DetachedProcess = 0x00000008;
    public const uint CreateNewProcessGroup = 0x00000200;
    public const uint CreateBreakawayFromJob = 0x01000000;
    public const int ErrorAccessDenied = 5;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct StartupInfo
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ProcessInformation
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateProcessAsUserW")]
    public static extern bool CreateProcessAsUser(
        IntPtr token, string? applicationName, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        bool inheritHandles, uint creationFlags, IntPtr environment, string? currentDirectory,
        ref StartupInfo startupInfo, out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateProcessW")]
    public static extern bool CreateProcess(
        string? applicationName, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        bool inheritHandles, uint creationFlags, IntPtr environment, string? currentDirectory,
        ref StartupInfo startupInfo, out ProcessInformation processInformation);

    [DllImport("userenv.dll", SetLastError = true)]
    public static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    public static extern bool DestroyEnvironmentBlock(IntPtr environment);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr handle);

    // ---- pipes -------------------------------------------------------------------------------

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);

    /// <summary>Session id of the process at the other end of a server pipe, or null.</summary>
    public static int? GetPipeClientSessionId(NamedPipeServerStream pipe)
    {
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid)) return null;
        if (!ProcessIdToSessionId(pid, out var session)) return null;
        return (int)session;
    }

    // ---- DPAPI -------------------------------------------------------------------------------

    private const int CryptProtectUiForbidden = 0x1;
    private const int CryptProtectLocalMachine = 0x4;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DataBlob dataIn, string? description, ref DataBlob entropy,
        IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, ref DataBlob entropy,
        IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    /// <summary>DPAPI with machine scope (CRYPTPROTECT_LOCAL_MACHINE) and entropy.</summary>
    public static byte[] ProtectMachine(byte[] data, byte[] entropy) => Dpapi(data, entropy, protect: true);

    public static byte[] UnprotectMachine(byte[] data, byte[] entropy) => Dpapi(data, entropy, protect: false);

    private static byte[] Dpapi(byte[] data, byte[] entropy, bool protect)
    {
        var dataHandle = GCHandle.Alloc(data, GCHandleType.Pinned);
        var entropyHandle = GCHandle.Alloc(entropy, GCHandleType.Pinned);
        try
        {
            var input = new DataBlob { cbData = data.Length, pbData = dataHandle.AddrOfPinnedObject() };
            var extra = new DataBlob { cbData = entropy.Length, pbData = entropyHandle.AddrOfPinnedObject() };
            var flags = CryptProtectUiForbidden | (protect ? CryptProtectLocalMachine : 0);
            var ok = protect
                ? CryptProtectData(ref input, "DomainNotice install password", ref extra, IntPtr.Zero, IntPtr.Zero, flags, out var output)
                : CryptUnprotectData(ref input, IntPtr.Zero, ref extra, IntPtr.Zero, IntPtr.Zero, flags, out output);
            if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var result = new byte[output.cbData];
                Marshal.Copy(output.pbData, result, 0, output.cbData);
                return result;
            }
            finally
            {
                LocalFree(output.pbData);
            }
        }
        finally
        {
            dataHandle.Free();
            entropyHandle.Free();
        }
    }

    public static Win32Exception LastError(string what) =>
        new(Marshal.GetLastWin32Error(), what + " failed");
}
