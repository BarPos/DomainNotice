using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using DomainNotice.Agent.Platform;
using DomainNotice.Shared.LocalIpc;

namespace DomainNotice.Agent.Sessions;

/// <summary>Starts DomainNotice.Agent.Session.exe in a user session (plan 5.2) and finds running instances.</summary>
internal static class SessionUiLauncher
{
    public static string ExecutablePath => Path.Combine(SessionFolder, LocalPipe.SessionExeName);

    private static string SessionFolder => Path.Combine(AppContext.BaseDirectory, LocalPipe.SessionSubfolder);

    private static string ProcessName => Path.GetFileNameWithoutExtension(LocalPipe.SessionExeName);

    /// <summary>
    /// WTSQueryUserToken + CreateEnvironmentBlock + CreateProcessAsUser on <c>winsta0\default</c>. The token from
    /// WTSQueryUserToken is the user's normal (filtered, non-elevated) token. The command line carries
    /// <c>--pipe &lt;suffix&gt;</c>, the per-start pipe suffix; this is the only place that starts the Session UI.
    /// </summary>
    public static Process Start(int sessionId, string pipeSuffix)
    {
        if (!LocalPipe.IsValidSuffix(pipeSuffix)) throw new ArgumentException("Invalid pipe suffix.", nameof(pipeSuffix));
        var exe = ExecutablePath;
        if (!File.Exists(exe)) throw new FileNotFoundException("Session UI executable not found.", exe);

        if (!NativeMethods.WTSQueryUserToken(sessionId, out var token))
            throw NativeMethods.LastError($"WTSQueryUserToken({sessionId})");

        var environment = IntPtr.Zero;
        try
        {
            if (!NativeMethods.CreateEnvironmentBlock(out environment, token, inherit: false))
                throw NativeMethods.LastError("CreateEnvironmentBlock");

            var startup = new NativeMethods.StartupInfo
            {
                cb = Marshal.SizeOf<NativeMethods.StartupInfo>(),
                lpDesktop = @"winsta0\default",
            };
            var commandLine = new StringBuilder($"\"{exe}\" {LocalPipe.SuffixArgument} {pipeSuffix}");
            if (!NativeMethods.CreateProcessAsUser(
                    token, exe, commandLine, IntPtr.Zero, IntPtr.Zero, inheritHandles: false,
                    NativeMethods.CreateUnicodeEnvironment, environment, SessionFolder,
                    ref startup, out var info))
                throw NativeMethods.LastError("CreateProcessAsUser");

            try
            {
                // Taken while our handle is still open, so the pid cannot be reused in between.
                var process = Process.GetProcessById(info.dwProcessId);
                _ = process.Handle; // open our own handle now, so HasExited stays reliable
                return process;
            }
            finally
            {
                NativeMethods.CloseHandle(info.hThread);
                NativeMethods.CloseHandle(info.hProcess);
            }
        }
        finally
        {
            if (environment != IntPtr.Zero) NativeMethods.DestroyEnvironmentBlock(environment);
            NativeMethods.CloseHandle(token);
        }
    }

    /// <summary>
    /// A Session UI that is already running in the session but was not started by this service process (left over
    /// from an earlier start, or started by hand), if any. It cannot know this start's pipe suffix, so it is replaced.
    /// </summary>
    public static Process? FindRunning(int sessionId)
    {
        Process? found = null;
        foreach (var process in Process.GetProcessesByName(ProcessName))
        {
            if (found is null && process.SessionId == sessionId && IsOurExecutable(process))
            {
                found = process;
                continue;
            }
            process.Dispose();
        }
        return found;
    }

    /// <summary>All Session UI processes started from this installation (to stop them when the service stops).</summary>
    public static List<Process> FindAll() =>
        Process.GetProcessesByName(ProcessName).Where(IsOurExecutable).ToList();

    private static bool IsOurExecutable(Process process)
    {
        try
        {
            var path = process.MainModule?.FileName;
            return path is null || string.Equals(Path.GetFullPath(path), Path.GetFullPath(ExecutablePath), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return true; // cannot tell; treat it as ours rather than starting a second instance
        }
    }
}
