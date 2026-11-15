using DomainNotice.Shared.Protocol;

namespace DomainNotice.Server.Options;

// Bound from configuration sections with the names in plan section 12. Keep key names verbatim.

public sealed class ConsoleOptions
{
    public const string Section = "Console";
    public string Urls { get; set; } = "https://0.0.0.0:443";
    /// <summary>PEM certificate path. Empty = development certificate.</summary>
    public string Certificate { get; set; } = "";
    public string CertificateKey { get; set; } = "";
}

public sealed class PathsOptions
{
    public const string Section = "Paths";
    public string Data { get; set; } = "/opt/domainnotice/data";
    public string Backups { get; set; } = "/opt/domainnotice/backups";

    public string KeysDirectory => Path.Combine(Data, "keys");
    public string DataProtectionDirectory => Path.Combine(Data, "keys", "dataprotection");
    public string UpdatesDirectory => Path.Combine(Data, "updates");
    public string AgentPackagesDirectory => Path.Combine(Data, "packages", "agent");
}

public sealed class AgentsOptions
{
    public const string Section = "Agents";
    public int Port { get; set; } = ProtocolInfo.DefaultPort;
    /// <summary>Address to listen on. Not in plan section 12; defaults to all interfaces.</summary>
    public string ListenAddress { get; set; } = "0.0.0.0";
    public int OfflineAfterSeconds { get; set; } = 90;
    public int MaxMessageBytes { get; set; } = ProtocolInfo.DefaultMaxFrameBytes;
    public int UpdateTimeoutMinutes { get; set; } = 30;
    public int MaxParallelUpdates { get; set; } = 50;
    /// <summary>Total bandwidth cap for package transfers; 0 = unlimited.</summary>
    public int UpdateBandwidthMbps { get; set; } = 0;
    /// <summary>P11: after a server update, send the update command to outdated agents automatically.</summary>
    public bool AutoUpdate { get; set; } = false;
}

public sealed class AnonymousAnswersOptions
{
    public const string Section = "AnonymousAnswers";
    public int FlushMinutes { get; set; } = 10;
}

public sealed class LocalAccountsOptions
{
    public const string Section = "LocalAccounts";
    public bool Enabled { get; set; } = true;
}

public sealed class UpdatesOptions
{
    public const string Section = "Updates";
    /// <summary>owner/name. Blank = updates off, and no network calls at all (plan 11.4).</summary>
    public string GitHubRepository { get; set; } = "";
    /// <summary>stable | prerelease</summary>
    public string Channel { get; set; } = "stable";
    public int CheckIntervalHours { get; set; } = 24;
    public string GitHubToken { get; set; } = "";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(GitHubRepository);
}

public sealed class EnrolmentOptions
{
    public const string Section = "Enrolment";
    public bool RequirePassword { get; set; } = true;
    /// <summary>PBKDF2 hash (see Common.PasswordHasher format). A value set in the Console (database) wins.</summary>
    public string PasswordHash { get; set; } = "";
}

public sealed class HistoryOptions
{
    public const string Section = "History";
    public int RetentionDays { get; set; } = 30;
}

public enum DirectorySecurity
{
    Ldaps = 0,
    StartTls = 1,
    /// <summary>Plain LDAP. Development only.</summary>
    None = 2,
}

public sealed class DirectoryOptions
{
    public const string Section = "Directory";
    public List<string> Hosts { get; set; } = [];
    public int Port { get; set; } = 636;
    public DirectorySecurity Security { get; set; } = DirectorySecurity.Ldaps;
    /// <summary>Path to a CA or pinned DC certificate (PEM/DER). Empty = OS trust store.</summary>
    public string TrustedCertificate { get; set; } = "";
    /// <summary>Development only; refused outside the Development environment.</summary>
    public bool AcceptInvalidCertificate { get; set; } = false;
    public string BaseDn { get; set; } = "";
    public string BindUser { get; set; } = "";
    /// <summary>"Environment:VAR" or "Database" (entered in the Console, stored encrypted).</summary>
    public string BindPasswordSource { get; set; } = "Environment:DOMAINNOTICE_BIND_PASSWORD";
    public List<string> AdminGroups { get; set; } = [];
    /// <summary>P4: read-only Console access.</summary>
    public List<string> ViewerGroups { get; set; } = [];
    public string FullSyncCron { get; set; } = "0 2 * * *";
    public int IncrementalSyncMinutes { get; set; } = 5;
    /// <summary>DomainNotice/&lt;server-fqdn&gt;, registered on the bind account with setspn.</summary>
    public string KerberosServicePrincipal { get; set; } = "";

    public bool IsConfigured => Hosts.Count > 0 && !string.IsNullOrWhiteSpace(BaseDn);
}
