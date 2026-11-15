using DomainNotice.Shared.Protocol;

namespace DomainNotice.Shared.LocalIpc;

// Messages on the local named pipe between the Agent service (LocalSystem) and the Session UI in one
// user session. Local only: these are not part of the agent link and not frozen, but both executables
// ship in the same MSI, so they always have the same version.
// The Type constant of each message is its wire name in LocalEnvelope.Type.

/// <summary>Non-generic base of every local message, usable as a variable or type argument.</summary>
public interface ILocalPayload;

public interface ILocalMessage : ILocalPayload
{
    static abstract string Type { get; }
}

public static class LocalPipe
{
    public const string NamePrefix = "DomainNotice.Agent.Session.";

    /// <summary>Largest frame body on the pipe. An announcement must fit in one agent-link frame anyway.</summary>
    public const int MaxFrameBytes = 4 * 1024 * 1024;

    /// <summary>Pipe name for one Windows session: <c>DomainNotice.Agent.Session.&lt;sessionId&gt;</c>.</summary>
    public static string NameFor(int sessionId) =>
        NamePrefix + sessionId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Single-instance mutex of the Session UI (per logon session).</summary>
    public const string SessionMutexName = @"Local\DomainNotice.Agent.Session";

    /// <summary>File name of the Session UI executable, next to the service executable.</summary>
    public const string SessionExeName = "DomainNotice.Agent.Session.exe";
}

// ---- service -> session ------------------------------------------------------------------------

/// <summary>Show (or keep showing) an announcement. Sent again after a reconnect; the UI dedupes by DeliveryId.</summary>
public sealed record ShowAnnouncement : ILocalMessage
{
    public static string Type => "ShowAnnouncement";
    public Guid DeliveryId { get; init; }
    public AnnouncementPayload Announcement { get; init; } = new();
}

/// <summary>Close the window of this delivery (withdrawn by the server, or confirmed in another session).</summary>
public sealed record WithdrawAnnouncement : ILocalMessage
{
    public static string Type => "Withdraw";
    public Guid DeliveryId { get; init; }
}

/// <summary>State of the service's link to the server, for the tray status line.</summary>
public sealed record ConnectionStatus : ILocalMessage
{
    public static string Type => "ConnectionStatus";
    public bool Connected { get; init; }
    public string ServerName { get; init; } = "";
    /// <summary>Optional human-readable detail, e.g. "The server is older than this agent".</summary>
    public string? Message { get; init; }
}

/// <summary>Answer to a <see cref="ConfirmRequest"/>. On rejection the window stays open.</summary>
public sealed record ConfirmReply : ILocalMessage
{
    public static string Type => "ConfirmResult";
    public Guid DeliveryId { get; init; }
    public bool Accepted { get; init; }
    public string? Error { get; init; }
}

// ---- session -> service ------------------------------------------------------------------------

/// <summary>The window really appeared (WPF ContentRendered).</summary>
public sealed record ShownReport : ILocalMessage
{
    public static string Type => "Shown";
    public Guid DeliveryId { get; init; }
}

/// <summary>The user clicked a button.</summary>
public sealed record ConfirmRequest : ILocalMessage
{
    public static string Type => "Confirm";
    public Guid DeliveryId { get; init; }
    public Guid ButtonId { get; init; }
    public List<AnswerItem> Answers { get; init; } = [];
}

/// <summary>Asks the service to send the connection status and every pending announcement again.</summary>
public sealed record RequestPending : ILocalMessage
{
    public static string Type => "RequestPending";
}
