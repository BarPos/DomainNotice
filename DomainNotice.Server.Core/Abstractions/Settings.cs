namespace DomainNotice.Server.Abstractions;

/// <summary>Settings changed in the Console, stored in the database (Settings table).</summary>
public interface ISettingsStore
{
    Task<string?> GetAsync(string key, CancellationToken ct = default);
    Task SetAsync(string key, string? value, string actor, CancellationToken ct = default);
    /// <summary>Encrypted with ASP.NET Data Protection (purpose "DomainNotice.Settings").</summary>
    Task<string?> GetSecretAsync(string key, CancellationToken ct = default);
    Task SetSecretAsync(string key, string? value, string actor, CancellationToken ct = default);
}

public static class SettingKeys
{
    /// <summary>"true"/"false". Overrides Enrolment:RequirePassword when set.</summary>
    public const string EnrolmentRequirePassword = "Enrolment.RequirePassword";
    /// <summary>PBKDF2 hash of the install password. Overrides Enrolment:PasswordHash when set.</summary>
    public const string EnrolmentPasswordHash = "Enrolment.PasswordHash";
    /// <summary><see cref="AwaitingApprovalPolicy"/> as a string. Default EveryoneAndUser.</summary>
    public const string AwaitingApprovalReceives = "Computers.AwaitingApprovalReceives";
    /// <summary>Secret: LDAP bind password when Directory:BindPasswordSource is "Database".</summary>
    public const string DirectoryBindPassword = "Directory.BindPassword";
    /// <summary>Highest uSNChanged seen, per DC: key prefix + host.</summary>
    public const string DirectoryHighestUsnPrefix = "Directory.HighestUsn.";
    /// <summary>Text shown to the right of the title in the announcement window. Unset = "DomainNotice".</summary>
    public const string AnnouncementHeaderText = "Announcements.HeaderText";
    /// <summary>JSON of the last release check (Updates).</summary>
    public const string UpdatesLastCheck = "Updates.LastCheck";
}

public enum AwaitingApprovalPolicy
{
    /// <summary>Default: Everyone and User announcements only.</summary>
    EveryoneAndUser = 0,
    Nothing = 1,
}

/// <summary>P3 audit log.</summary>
public interface IAuditLog
{
    Task WriteAsync(string actor, string action, string? targetType = null, string? targetId = null,
        string? details = null, CancellationToken ct = default);
}

public static class AuditActions
{
    public const string AnnouncementCreated = "announcement.created";
    public const string AnnouncementUpdated = "announcement.updated";
    public const string AnnouncementDeleted = "announcement.deleted";
    public const string ComputerRevoked = "computer.revoked";
    public const string ComputerDeleted = "computer.deleted";
    public const string ComputerApprovedReplace = "computer.approved-replace";
    public const string ComputerKeptSeparate = "computer.kept-separate";
    public const string InstallPasswordChanged = "enrolment.password-changed";
    public const string InstallPasswordRequirementChanged = "enrolment.requirement-changed";
    public const string SettingChanged = "setting.changed";
    public const string LocalAccountCreated = "local-account.created";
    public const string LocalAccountPasswordReset = "local-account.password-reset";
    public const string LocalAccountDisabled = "local-account.disabled";
    public const string LocalAccountEnabled = "local-account.enabled";
    public const string DirectorySyncRequested = "directory.sync-requested";
    public const string DirectoryBindPasswordChanged = "directory.bind-password-changed";
    public const string AgentUpdateRequested = "agent-update.requested";
    public const string AgentUpdateCancelled = "agent-update.cancelled";
    public const string AgentPackageImported = "agent-package.imported";
    public const string ServerUpdateRequested = "server-update.requested";
    public const string SignIn = "sign-in";
    public const string SignInFailed = "sign-in.failed";
}
