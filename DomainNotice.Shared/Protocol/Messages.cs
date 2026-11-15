using System.Text.Json.Serialization;

namespace DomainNotice.Shared.Protocol;

// Agent link messages (plan 3.3). Rules for compatibility (plan 11.1):
//  - add new fields, never rename or remove them;
//  - frozen from 1.0.0 on: framing, chunk layout, Hello, Welcome, UpdateAgent, PackageRequest, UpdateStatus.
// The Type constant of each message is its wire name in Envelope.Type.

public interface IAgentMessage
{
    static abstract string Type { get; }
}

public sealed record SessionInfo
{
    public int SessionId { get; init; }
    /// <summary>DOMAIN\user, read by the Agent service from WTS.</summary>
    public string UserName { get; init; } = "";
    public SessionState State { get; init; }
    public DateTime LogonTimeUtc { get; init; }
}

// ---- Enrolment ----------------------------------------------------------------------------

/// <summary>A→S. First contact only, from an unknown key.</summary>
public sealed record Enroll : IAgentMessage
{
    public static string Type => "Enroll";
    public string? InstallPassword { get; init; }
    public string ComputerName { get; init; } = "";
    public string DnsName { get; init; } = "";
    public string OsVersion { get; init; } = "";
    public string AgentVersion { get; init; } = "";
}

/// <summary>S→A.</summary>
public sealed record EnrollResult : IAgentMessage
{
    public static string Type => "EnrollResult";
    public bool Accepted { get; init; }
    public Guid? ComputerId { get; init; }
    public string? Error { get; init; }
}

// ---- Session start (FROZEN) ------------------------------------------------------------------

/// <summary>A→S. Sent on every connect after enrolment. FROZEN.</summary>
public sealed record Hello : IAgentMessage
{
    public static string Type => "Hello";
    public string AgentVersion { get; init; } = "";
    public string ComputerName { get; init; } = "";
    public string DnsName { get; init; } = "";
    public string OsVersion { get; init; } = "";
    public List<string> LocalIps { get; init; } = [];
    public List<SessionInfo> Sessions { get; init; } = [];
}

/// <summary>S→A. FROZEN.</summary>
public sealed record Welcome : IAgentMessage
{
    public static string Type => "Welcome";
    public bool Compatible { get; init; }
    public string ServerVersion { get; init; } = "";
    /// <summary>Optional: the server's Kerberos SPN (DomainNotice/fqdn) for ProveIdentity. Added field.</summary>
    public string? KerberosServicePrincipal { get; init; }
}

/// <summary>A→S. On change (IP, logon, logoff, lock, unlock) and every 10 min.</summary>
public sealed record Status : IAgentMessage
{
    public static string Type => "Status";
    public string ComputerName { get; init; } = "";
    public List<string> LocalIps { get; init; } = [];
    public List<SessionInfo> Sessions { get; init; } = [];
}

/// <summary>S→A. Sent before closing a connection that the server refuses (unknown key, revoked, ...).</summary>
public sealed record Refused : IAgentMessage
{
    public static string Type => "Refused";
    /// <summary>One of <see cref="RefusedReasons"/>.</summary>
    public string Reason { get; init; } = "";
    public string? Message { get; init; }
}

public static class RefusedReasons
{
    public const string UnknownKey = "unknown-key";
    public const string Revoked = "revoked";
    public const string Replaced = "replaced";
    public const string ProtocolError = "protocol-error";
    public const string RateLimited = "rate-limited";
}

// ---- Announcements ---------------------------------------------------------------------------

public sealed record AnnouncementButton
{
    public Guid Id { get; init; }
    public string Label { get; init; } = "";
}

public sealed record AnnouncementOption
{
    public Guid Id { get; init; }
    public string Label { get; init; } = "";
}

public sealed record AnnouncementQuestion
{
    public Guid Id { get; init; }
    public string Prompt { get; init; } = "";
    public QuestionKind Kind { get; init; }
    public bool Required { get; init; }
    public List<AnnouncementOption> Options { get; init; } = [];
}

public sealed record AnnouncementPayload
{
    public Guid Id { get; init; }
    public string Title { get; init; } = "";
    public string BodyMarkdown { get; init; } = "";
    public int MinDismissSeconds { get; init; }
    public UnlockMode UnlockMode { get; init; }
    public bool AnonymousAnswers { get; init; }
    public List<AnnouncementButton> Buttons { get; init; } = [];
    public List<AnnouncementQuestion> Questions { get; init; } = [];
}

/// <summary>S→A. Push an announcement to one user on this computer.</summary>
public sealed record Show : IAgentMessage
{
    public static string Type => "Show";
    public Guid DeliveryId { get; init; }
    /// <summary>DOMAIN\user the delivery is for, as reported by the agent.</summary>
    public string SessionUser { get; init; } = "";
    public AnnouncementPayload Announcement { get; init; } = new();
}

/// <summary>A→S. The window really appeared.</summary>
public sealed record Shown : IAgentMessage
{
    public static string Type => "Shown";
    public Guid DeliveryId { get; init; }
}

public sealed record AnswerItem
{
    public Guid QuestionId { get; init; }
    public List<Guid> OptionIds { get; init; } = [];
}

/// <summary>A→S. The user clicked a button.</summary>
public sealed record Confirm : IAgentMessage
{
    public static string Type => "Confirm";
    public Guid DeliveryId { get; init; }
    public Guid ButtonId { get; init; }
    public List<AnswerItem> Answers { get; init; } = [];
}

/// <summary>S→A. ReplyTo = the Confirm envelope id.</summary>
public sealed record ConfirmResult : IAgentMessage
{
    public static string Type => "ConfirmResult";
    public Guid DeliveryId { get; init; }
    public bool Accepted { get; init; }
    public string? Error { get; init; }
}

/// <summary>S→A. The announcement was deleted or ended; close the window if it is still open.</summary>
public sealed record Withdraw : IAgentMessage
{
    public static string Type => "Withdraw";
    public Guid DeliveryId { get; init; }
}

// ---- Identity --------------------------------------------------------------------------------

/// <summary>A→S. Kerberos AP-REQ (base64) for the server SPN, bound to this TLS channel (plan 3.4).</summary>
public sealed record ProveIdentity : IAgentMessage
{
    public static string Type => "ProveIdentity";
    public string KerberosToken { get; init; } = "";
}

/// <summary>S→A.</summary>
public sealed record IdentityResult : IAgentMessage
{
    public static string Type => "IdentityResult";
    public bool Linked { get; init; }
    /// <summary>Distinguished name or sAMAccountName of the linked AD computer.</summary>
    public string? AdComputer { get; init; }
    public string? Error { get; init; }
}

// ---- Agent update (FROZEN) -------------------------------------------------------------------

/// <summary>S→A. Update to the server's version (plan 11.3). FROZEN.</summary>
public sealed record UpdateAgent : IAgentMessage
{
    public static string Type => "UpdateAgent";
    public Guid TaskId { get; init; }
    public string Version { get; init; } = "";
    public long Size { get; init; }
    /// <summary>Lowercase hex SHA-256 of the MSI.</summary>
    public string Sha256 { get; init; } = "";
}

/// <summary>A→S. Ask for the package from Offset on (also resumes after reconnect). FROZEN.</summary>
public sealed record PackageRequest : IAgentMessage
{
    public static string Type => "PackageRequest";
    public Guid TaskId { get; init; }
    public long Offset { get; init; }
}

/// <summary>A→S. FROZEN.</summary>
public sealed record UpdateStatus : IAgentMessage
{
    public static string Type => "UpdateStatus";
    public Guid TaskId { get; init; }
    public AgentUpdateState State { get; init; }
    public string? Error { get; init; }
    /// <summary>Optional end of the msiexec log on failure.</summary>
    public string? LogTail { get; init; }
}

// ---- Keepalive -------------------------------------------------------------------------------

public sealed record Ping : IAgentMessage
{
    public static string Type => "Ping";
}

public sealed record Pong : IAgentMessage
{
    public static string Type => "Pong";
}
