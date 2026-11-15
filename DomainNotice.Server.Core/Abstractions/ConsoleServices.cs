using DomainNotice.Server.Data.Entities;
using DomainNotice.Shared;

namespace DomainNotice.Server.Abstractions;

// Services the Console calls for ACTIONS. For lists and grids the Console queries AppDbContext directly
// (via IDbContextFactory<AppDbContext>) and must apply the anonymous-answer rules of plan 8.4 itself.

public readonly record struct OperationResult(bool Ok, string? Error = null)
{
    public static OperationResult Success => new(true);
    public static OperationResult Fail(string error) => new(false, error);
}

// ---------------------------------------------------------------- Announcements (owner: Core/Announcements)

public sealed class AnnouncementEditModel
{
    /// <summary>Null = new.</summary>
    public Guid? Id { get; set; }
    /// <summary>Name of the announcement in the Console (lists, results). Not shown on the PCs.</summary>
    public string Title { get; set; } = "";
    /// <summary>Title shown above the message in the window on the PCs. Optional.</summary>
    public string DisplayTitle { get; set; } = "";
    public string BodyMarkdown { get; set; } = "";
    public int MinDismissSeconds { get; set; } = 10;
    public UnlockMode UnlockMode { get; set; } = UnlockMode.AfterTimeAndRequired;
    public bool AnonymousAnswers { get; set; }
    public DateTime StartUtc { get; set; }
    public DateTime EndUtc { get; set; }
    public int MaxDisplays { get; set; } = 1;
    public int RepeatIntervalMinutes { get; set; }
    public bool IsDraft { get; set; }
    public List<ButtonEdit> Buttons { get; set; } = [];
    public List<QuestionEdit> Questions { get; set; } = [];
    public List<TargetEdit> Targets { get; set; } = [];
}

/// <summary>Id null = new item. Existing items keep their id (plan 8.1). Items left out are hidden or deleted.</summary>
public sealed class ButtonEdit
{
    public Guid? Id { get; set; }
    public string Label { get; set; } = "";
}

public sealed class QuestionEdit
{
    public Guid? Id { get; set; }
    public string Prompt { get; set; } = "";
    public QuestionKind Kind { get; set; }
    public bool Required { get; set; }
    /// <summary>True = hidden (a question with answers cannot be deleted, only hidden).</summary>
    public bool Hidden { get; set; }
    /// <summary>Set by the service: whether answers exist (the editor disables Delete then).</summary>
    public bool HasAnswers { get; set; }
    public List<OptionEdit> Options { get; set; } = [];
}

public sealed class OptionEdit
{
    public Guid? Id { get; set; }
    public string Label { get; set; } = "";
}

public sealed class TargetEdit
{
    public TargetKind Kind { get; set; }
    public Guid? DirectoryObjectGuid { get; set; }
    public Guid? ComputerId { get; set; }
    public string? Value { get; set; }
    public string DisplayName { get; set; } = "";
}

public interface IAnnouncementService
{
    /// <summary>Null id = defaults for a new one (now .. now+7 days, one "OK" button, 10 s, AfterTimeAndRequired).</summary>
    Task<AnnouncementEditModel?> GetForEditAsync(Guid? id, CancellationToken ct = default);
    /// <summary>Validates, saves with stable ids, audits, and triggers delivery. Returns the id.</summary>
    Task<(OperationResult Result, Guid? Id)> SaveAsync(AnnouncementEditModel model, string actor, CancellationToken ct = default);
    /// <summary>Deletes the announcement and its results (cascade); open windows get Withdraw.</summary>
    Task<OperationResult> DeleteAsync(Guid id, string actor, CancellationToken ct = default);
    /// <summary>Validation only, for the wizard's Summary step.</summary>
    IReadOnlyList<string> Validate(AnnouncementEditModel model);
    /// <summary>Safe HTML for the live preview (Markdig, raw HTML disabled).</summary>
    string RenderPreviewHtml(string markdown);
}

public static class AnnouncementWindowDefaults
{
    public const string HeaderText = "DomainNotice";
    public const int HeaderTextMax = 60;
}

/// <summary>Settings of the announcement window shown on the PCs (Settings → Announcement window).</summary>
public interface IAnnouncementWindowSettings
{
    /// <summary>Text to the right of the title. Default "DomainNotice"; empty = none.</summary>
    Task<string> GetHeaderTextAsync(CancellationToken ct = default);
    Task<OperationResult> SetHeaderTextAsync(string? text, string actor, CancellationToken ct = default);
}

/// <summary>CSV exports, UTF-8 with BOM. Anonymous answers have no user/computer/time columns (plan 8.4).</summary>
public interface IResultsExport
{
    Task<byte[]> ExportConfirmationsCsvAsync(Guid announcementId, CancellationToken ct = default);
    Task<byte[]> ExportAnswersCsvAsync(Guid announcementId, CancellationToken ct = default);
}

// ---------------------------------------------------------------- Computers & enrolment (owner: Core/AgentLink)

public interface IComputerAdminService
{
    Task<OperationResult> RevokeAsync(Guid computerId, string actor, CancellationToken ct = default);
    Task<OperationResult> DeleteAsync(Guid computerId, string actor, CancellationToken ct = default);
    /// <summary>Awaiting approval → "Replace the old computer": AD link moves to the new key, old marked replaced.</summary>
    Task<OperationResult> ApproveReplaceAsync(Guid newComputerId, string actor, CancellationToken ct = default);
    /// <summary>Awaiting approval → "Keep both": stays separate, not linked to AD.</summary>
    Task<OperationResult> KeepSeparateAsync(Guid newComputerId, string actor, CancellationToken ct = default);
    /// <summary>All computer ids whose history belongs to this one (itself plus records it replaced, recursively).</summary>
    Task<IReadOnlyList<Guid>> GetHistoryChainAsync(Guid computerId, CancellationToken ct = default);
}

public sealed record EnrolmentState(bool RequirePassword, bool PasswordSet, bool RequirePasswordFromDatabase, bool PasswordFromDatabase);

public interface IEnrolmentAdmin
{
    Task<EnrolmentState> GetStateAsync(CancellationToken ct = default);
    Task<OperationResult> SetPasswordAsync(string newPassword, string actor, CancellationToken ct = default);
    Task<OperationResult> SetRequirePasswordAsync(bool required, string actor, CancellationToken ct = default);
    Task<AwaitingApprovalPolicy> GetAwaitingApprovalPolicyAsync(CancellationToken ct = default);
    Task SetAwaitingApprovalPolicyAsync(AwaitingApprovalPolicy policy, string actor, CancellationToken ct = default);
}

// ---------------------------------------------------------------- Sign-in & accounts (owner: Core/Accounts + Core/Directory)

public static class ConsoleRoles
{
    public const string Admin = "Admin";
    /// <summary>P4: read-only.</summary>
    public const string Viewer = "Viewer";
}

public enum SignInSource { Domain = 0, Local = 1 }

public sealed record SignInOutcome(bool Succeeded, string? UserName = null, string? DisplayName = null,
    string? Role = null, SignInSource Source = SignInSource.Local, string? Error = null);

public interface IAdminSignInService
{
    bool DomainSignInAvailable { get; }
    bool LocalSignInEnabled { get; }
    /// <summary>Binds as the user over LDAP(S); must be in Directory:AdminGroups (Admin) or ViewerGroups (Viewer), nested groups count.</summary>
    Task<SignInOutcome> SignInDomainAsync(string userName, string password, string? remoteIp, CancellationToken ct = default);
    /// <summary>Rate-limited per account and per IP.</summary>
    Task<SignInOutcome> SignInLocalAsync(string userName, string password, string? remoteIp, CancellationToken ct = default);
    /// <summary>True when /account/setup may create the first local account: no directory and no local account.</summary>
    Task<bool> IsFirstRunSetupAllowedAsync(CancellationToken ct = default);
}

public interface ILocalAccountService
{
    Task<bool> AnyAsync(CancellationToken ct = default);
    /// <summary>Min 8 characters.</summary>
    Task<OperationResult> CreateAsync(string userName, string password, string actor, CancellationToken ct = default);
    /// <summary>Creates the first account only when IsFirstRunSetupAllowed (atomic check).</summary>
    Task<OperationResult> CreateFirstAsync(string userName, string password, CancellationToken ct = default);
    Task<OperationResult> ResetPasswordAsync(Guid id, string newPassword, string actor, CancellationToken ct = default);
    Task<OperationResult> SetDisabledAsync(Guid id, bool disabled, string actor, CancellationToken ct = default);
}

// ---------------------------------------------------------------- Directory (owner: Core/Directory)

public sealed record DirectorySyncStatus(bool Configured, bool Running, DateTime? LastSuccessUtc, DateTime? LastAttemptUtc,
    string? LastError, int ObjectCount);

public interface IDirectoryAdmin
{
    bool IsConfigured { get; }
    Task<DirectorySyncStatus> GetStatusAsync(CancellationToken ct = default);
    /// <summary>Queues a sync ("Sync now"). Full when <paramref name="full"/>.</summary>
    Task RequestSyncAsync(bool full, string actor, CancellationToken ct = default);
    /// <summary>Stores the bind password encrypted in the database (BindPasswordSource "Database").</summary>
    Task<OperationResult> SetBindPasswordAsync(string password, string actor, CancellationToken ct = default);
    /// <summary>Binds with the service account and reports success or the error.</summary>
    Task<OperationResult> TestConnectionAsync(CancellationToken ct = default);
}

// ---------------------------------------------------------------- Updates (owner: Core/Updates)

public sealed record ReleaseInfo(string Version, string TagName, string? Name, string? NotesMarkdown, DateTime? PublishedAtUtc,
    bool Prerelease, string? HtmlUrl);

public sealed record ServerUpdateStatus(
    bool Configured,
    string CurrentVersion,
    ReleaseInfo? Latest,
    bool UpdateAvailable,
    DateTime? LastCheckUtc,
    string? LastCheckError,
    ServerUpdateRun? LastRun,
    bool SelfUpdateSupported);

public interface IServerUpdateService
{
    Task<ServerUpdateStatus> GetStatusAsync(CancellationToken ct = default);
    /// <summary>No network call when Updates:GitHubRepository is blank.</summary>
    Task<ServerUpdateStatus> CheckNowAsync(string actor, CancellationToken ct = default);
    /// <summary>Downloads, verifies and stages the latest release, then writes the request file for update.sh.</summary>
    Task<OperationResult> UpdateNowAsync(string actor, CancellationToken ct = default);
}

public sealed record AgentPackageInfo(string Version, long Size, string Sha256, string FilePath, DateTime AddedAtUtc, string Source);

public interface IAgentPackageCache
{
    /// <summary>The MSI for the server's own version, if cached.</summary>
    Task<AgentPackageInfo?> GetCurrentAsync(CancellationToken ct = default);
    Task<IReadOnlyList<AgentPackageInfo>> ListAsync(CancellationToken ct = default);
    Stream OpenRead(AgentPackageInfo package);
    /// <summary>P13: import a locally built MSI for the server's version.</summary>
    Task<OperationResult> ImportAsync(Stream msi, string fileName, string actor, CancellationToken ct = default);
}

public interface IAgentUpdateService
{
    /// <summary>Creates one task per computer (skips computers that already have an open task or are current).</summary>
    Task<(OperationResult Result, int Created)> RequestUpdateAsync(IReadOnlyCollection<Guid> computerIds, string actor, CancellationToken ct = default);
    Task<(OperationResult Result, int Created)> RequestUpdateAllOutdatedAsync(string actor, CancellationToken ct = default);
    Task<OperationResult> CancelAsync(Guid taskId, string actor, CancellationToken ct = default);
    /// <summary>Settings → Agent install: update every outdated agent automatically when it is online. A Console value
    /// overrides Agents:AutoUpdate.</summary>
    Task<bool> GetAutoUpdateAsync(CancellationToken ct = default);
    /// <summary>Turns automatic updates on/off; turning it on also requests updates for the outdated agents online now.</summary>
    Task<OperationResult> SetAutoUpdateAsync(bool enabled, string actor, CancellationToken ct = default);
    /// <summary>Called when an agent comes online: requests its update when automatic updates are on and it is outdated.</summary>
    Task AutoUpdateOnlineAgentAsync(Guid computerId, string? agentVersion, CancellationToken ct = default);
}
