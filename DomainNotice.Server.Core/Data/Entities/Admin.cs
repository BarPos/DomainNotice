using DomainNotice.Shared;

namespace DomainNotice.Server.Data.Entities;

/// <summary>Backup admin account (plan 7.1). Full admin rights.</summary>
public class LocalAccount
{
    public Guid Id { get; set; }
    public string UserName { get; set; } = "";
    /// <summary>Lowercase, unique.</summary>
    public string NormalizedUserName { get; set; } = "";
    /// <summary>Format of Common.PasswordHasher.</summary>
    public string PasswordHash { get; set; } = "";
    public bool Disabled { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTime? LastSignInAtUtc { get; set; }
    public DateTime? PasswordChangedAtUtc { get; set; }
}

/// <summary>Key/value settings changed from the Console. Secret values are encrypted with Data Protection.</summary>
public class Setting
{
    public string Key { get; set; } = "";
    public string? Value { get; set; }
    public bool IsSecret { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public string UpdatedBy { get; set; } = "";
}

/// <summary>P3: audit log of Console actions.</summary>
public class AuditLogEntry
{
    public long Id { get; set; }
    public DateTime AtUtc { get; set; }
    public string Actor { get; set; } = "";
    public string Action { get; set; } = "";
    public string? TargetType { get; set; }
    public string? TargetId { get; set; }
    public string? Details { get; set; }
    public string? RemoteIp { get; set; }
}

public class AgentUpdateTask
{
    public Guid Id { get; set; }
    public Guid ComputerId { get; set; }
    public Computer? Computer { get; set; }
    public string FromVersion { get; set; } = "";
    public string ToVersion { get; set; } = "";
    public string RequestedBy { get; set; } = "";
    public AgentUpdateState State { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? SentAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public DateTime? FinishedAtUtc { get; set; }
    public long BytesSent { get; set; }
    public string? Error { get; set; }
    public string? LogTail { get; set; }
}

public enum ServerUpdateState
{
    Downloading = 0,
    Staged = 1,
    Applying = 2,
    Succeeded = 3,
    RolledBack = 4,
    Failed = 5,
}

public class ServerUpdateRun
{
    public Guid Id { get; set; }
    public string FromVersion { get; set; } = "";
    public string ToVersion { get; set; } = "";
    public string RequestedBy { get; set; } = "";
    public ServerUpdateState State { get; set; }
    public DateTime RequestedAtUtc { get; set; }
    public DateTime? FinishedAtUtc { get; set; }
    public string? Message { get; set; }
}
