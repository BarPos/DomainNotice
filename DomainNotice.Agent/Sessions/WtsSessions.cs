using System.Runtime.InteropServices;
using System.Security.Principal;
using DomainNotice.Agent.Logic;
using DomainNotice.Agent.Platform;
using DomainNotice.Shared;
using DomainNotice.Shared.Protocol;

namespace DomainNotice.Agent.Sessions;

/// <summary>Reads the interactive user sessions from the WTS API. User names come from Windows, not from the UI.</summary>
internal static class WtsSessions
{
    /// <summary>Sessions with a logged-on user (session 0 and the login screen are skipped).</summary>
    public static List<SessionInfo> Enumerate(SessionChangeEvents events)
    {
        var result = new List<SessionInfo>();
        if (!NativeMethods.WTSEnumerateSessions(NativeMethods.WtsCurrentServerHandle, 0, 1, out var buffer, out var count))
            throw NativeMethods.LastError("WTSEnumerateSessions");

        try
        {
            var size = Marshal.SizeOf<NativeMethods.WtsSessionInfo>();
            for (var i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<NativeMethods.WtsSessionInfo>(buffer + (i * size));
                if (info.SessionId == 0) continue;
                if (info.State is not (NativeMethods.WtsConnectState.Active or NativeMethods.WtsConnectState.Disconnected or NativeMethods.WtsConnectState.Connected))
                    continue;

                var user = SessionSnapshot.FormatUser(
                    QueryString(info.SessionId, NativeMethods.WtsInfoClass.DomainName),
                    QueryString(info.SessionId, NativeMethods.WtsInfoClass.UserName));
                if (user is null) continue;

                var ex = QueryInfoEx(info.SessionId);
                var locked = events.IsLocked(info.SessionId) ?? (ex?.SessionFlags == NativeMethods.WtsSessionStateLock);
                var state = info.State == NativeMethods.WtsConnectState.Disconnected
                    ? SessionState.Disconnected
                    : locked ? SessionState.Locked : SessionState.Active;
                var logon = ex is { LogonTime: > 0 } e ? DateTime.FromFileTimeUtc(e.LogonTime) : DateTime.UnixEpoch;

                result.Add(new SessionInfo { SessionId = info.SessionId, UserName = user, State = state, LogonTimeUtc = logon });
            }
        }
        finally
        {
            NativeMethods.WTSFreeMemory(buffer);
        }

        return result;
    }

    private static string? QueryString(int sessionId, NativeMethods.WtsInfoClass infoClass)
    {
        if (!NativeMethods.WTSQuerySessionInformation(NativeMethods.WtsCurrentServerHandle, sessionId, infoClass, out var buffer, out _))
            return null;
        try
        {
            return Marshal.PtrToStringUni(buffer);
        }
        finally
        {
            NativeMethods.WTSFreeMemory(buffer);
        }
    }

    private static NativeMethods.WtsInfoExLevel1? QueryInfoEx(int sessionId)
    {
        if (!NativeMethods.WTSQuerySessionInformation(NativeMethods.WtsCurrentServerHandle, sessionId, NativeMethods.WtsInfoClass.SessionInfoEx, out var buffer, out _))
            return null;
        try
        {
            var ex = Marshal.PtrToStructure<NativeMethods.WtsInfoEx>(buffer);
            return ex.Level == 1 ? ex.Data : null;
        }
        finally
        {
            NativeMethods.WTSFreeMemory(buffer);
        }
    }

    /// <summary>The SID of the user logged on to a session, from the session's token (WTSQueryUserToken).</summary>
    public static SecurityIdentifier? QueryUserSid(int sessionId)
    {
        if (!NativeMethods.WTSQueryUserToken(sessionId, out var token)) return null;
        try
        {
            using var identity = new WindowsIdentity(token);
            return identity.User;
        }
        finally
        {
            NativeMethods.CloseHandle(token);
        }
    }
}
