using DomainNotice.Shared;

namespace DomainNotice.Server.Data.Entities;

public enum AdLinkState
{
    /// <summary>Not linked to an AD computer object.</summary>
    None = 0,
    /// <summary>Linked by the name fallback (not proof, plan 3.4).</summary>
    MatchedByName = 1,
    /// <summary>Linked by a Kerberos proof.</summary>
    Proven = 2,
    /// <summary>Its name is linked to another enrolled key and it has no proof; an admin decides (D5).</summary>
    AwaitingApproval = 3,
    /// <summary>Admin chose "Keep both": stays separate, not linked, off the awaiting list.</summary>
    KeptSeparate = 4,
}

/// <summary>An enrolled agent. Identity = <see cref="AgentKeyFingerprint"/> (plan 3.4).</summary>
public class Computer
{
    public Guid Id { get; set; }
    /// <summary>Current self-reported name.</summary>
    public string Name { get; set; } = "";
    public string DnsName { get; set; } = "";
    /// <summary>SHA-256 of the agent's SubjectPublicKeyInfo, lowercase hex. Unique.</summary>
    public string AgentKeyFingerprint { get; set; } = "";
    public DateTime EnrolledAtUtc { get; set; }
    public DateTime? LastSeenAtUtc { get; set; }
    public bool Online { get; set; }
    public string AgentVersion { get; set; } = "";
    public string OsVersion { get; set; } = "";
    /// <summary>Address the server sees on the connection (after NAT).</summary>
    public string? RemoteIp { get; set; }
    /// <summary>IPv4/IPv6 addresses reported by the agent.</summary>
    public List<string> LocalIps { get; set; } = [];
    public DateTime? RevokedAtUtc { get; set; }
    public string? RevokedBy { get; set; }
    /// <summary>objectGUID of the linked AD computer.</summary>
    public Guid? DirectoryObjectGuid { get; set; }
    public AdLinkState AdLinkState { get; set; }
    public DateTime? AdLinkedAtUtc { get; set; }
    /// <summary>Set on the old record when a re-imaged machine took its AD link (plan 4).</summary>
    public Guid? ReplacedByComputerId { get; set; }
    public DateTime? ReplacedAtUtc { get; set; }
    /// <summary>For AwaitingApproval: the record that currently holds the AD link the newcomer claims.</summary>
    public Guid? ConflictsWithComputerId { get; set; }

    public bool IsRevoked => RevokedAtUtc is not null;
    public bool IsReplaced => ReplacedByComputerId is not null;

    public List<ComputerSession> Sessions { get; set; } = [];
    public List<ComputerNameHistory> NameHistory { get; set; } = [];
    public List<UserLogonHistory> UserHistory { get; set; } = [];
    public List<ComputerIpHistory> IpHistory { get; set; } = [];
}

public class ComputerNameHistory
{
    public long Id { get; set; }
    public Guid ComputerId { get; set; }
    public Computer? Computer { get; set; }
    public string Name { get; set; } = "";
    public DateTime FirstSeenUtc { get; set; }
    public DateTime LastSeenUtc { get; set; }
    /// <summary>The open row for the current name. Open rows are never pruned.</summary>
    public bool IsCurrent { get; set; }
}

/// <summary>A user currently logged on (one row per Windows session).</summary>
public class ComputerSession
{
    public Guid ComputerId { get; set; }
    public Computer? Computer { get; set; }
    public int SessionId { get; set; }
    /// <summary>DOMAIN\user as reported by the agent.</summary>
    public string UserName { get; set; } = "";
    /// <summary>Lowercased, trimmed UserName for matching.</summary>
    public string UserKey { get; set; } = "";
    public SessionState State { get; set; }
    public DateTime LogonAtUtc { get; set; }
    public DateTime LastSeenUtc { get; set; }
}

public class UserLogonHistory
{
    public long Id { get; set; }
    public Guid ComputerId { get; set; }
    public Computer? Computer { get; set; }
    public int SessionId { get; set; }
    public string UserName { get; set; } = "";
    public string UserKey { get; set; } = "";
    public DateTime LogonAtUtc { get; set; }
    /// <summary>Null = still logged on (open row, never pruned).</summary>
    public DateTime? LogoffAtUtc { get; set; }
}

public enum IpAddressKind
{
    Remote = 0,
    Local = 1,
}

/// <summary>P6: IP history, same retention as name and user history.</summary>
public class ComputerIpHistory
{
    public long Id { get; set; }
    public Guid ComputerId { get; set; }
    public Computer? Computer { get; set; }
    public IpAddressKind Kind { get; set; }
    public string Address { get; set; } = "";
    public DateTime FirstSeenUtc { get; set; }
    public DateTime LastSeenUtc { get; set; }
    public bool IsCurrent { get; set; }
}
