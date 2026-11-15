// DEVELOPMENT-ONLY stand-ins for area services, so the Console can be run before the other areas are merged.
// Registered by AddDomainNoticeConsole only when the environment is Development AND Console:DevFakes=true, and
// only with TryAdd*, so a real registration always wins. Never used in Production.

using System.Security.Cryptography;
using System.Text;
using DomainNotice.Server.Abstractions;
using DomainNotice.Server.Common;
using DomainNotice.Server.Data;
using DomainNotice.Server.Data.Entities;
using DomainNotice.Shared;
using DomainNotice.Shared.Protocol;
using Markdig;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DomainNotice.Console.DevFakes;

internal static class DevFakeRegistration
{
    public static IServiceCollection AddConsoleDevFakes(this IServiceCollection services)
    {
        services.TryAddSingleton<IAdminSignInService, DevSignInService>();
        services.TryAddSingleton<ILocalAccountService, DevLocalAccountService>();
        services.TryAddSingleton<IAnnouncementService, DevAnnouncementService>();
        services.TryAddSingleton<IResultsExport, DevResultsExport>();
        services.TryAddSingleton<IComputerAdminService, DevComputerAdminService>();
        services.TryAddSingleton<IEnrolmentAdmin, DevEnrolmentAdmin>();
        services.TryAddSingleton<IDirectoryAdmin, DevDirectoryAdmin>();
        services.TryAddSingleton<IServerUpdateService, DevServerUpdateService>();
        services.TryAddSingleton<IAgentPackageCache, DevAgentPackageCache>();
        services.TryAddSingleton<IAgentUpdateService, DevAgentUpdateService>();
        services.TryAddSingleton<IServerIdentity, DevServerIdentity>();
        services.TryAddSingleton<IAgentHub, DevAgentHub>();
        return services;
    }
}

/// <summary>Local accounts from the database. Accounts whose name starts with "viewer" get the Viewer role (to try P4).</summary>
internal sealed class DevSignInService(IDbContextFactory<AppDbContext> dbFactory) : IAdminSignInService
{
    public bool DomainSignInAvailable => false;
    public bool LocalSignInEnabled => true;

    public Task<SignInOutcome> SignInDomainAsync(string userName, string password, string? remoteIp, CancellationToken ct = default) =>
        Task.FromResult(new SignInOutcome(false, Error: "No directory in development."));

    public async Task<SignInOutcome> SignInLocalAsync(string userName, string password, string? remoteIp, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var normalized = userName.Trim().ToLowerInvariant();
        var account = await db.LocalAccounts.FirstOrDefaultAsync(a => a.NormalizedUserName == normalized, ct);
        if (account is null || account.Disabled || !PasswordHasher.Verify(password, account.PasswordHash))
            return new SignInOutcome(false, Error: "Invalid");
        var role = normalized.StartsWith("viewer", StringComparison.Ordinal) ? ConsoleRoles.Viewer : ConsoleRoles.Admin;
        return new SignInOutcome(true, account.UserName, account.UserName, role, SignInSource.Local);
    }

    public async Task<bool> IsFirstRunSetupAllowedAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return !await db.LocalAccounts.AnyAsync(ct);
    }
}

internal sealed class DevLocalAccountService(IDbContextFactory<AppDbContext> dbFactory, IAuditLog audit) : ILocalAccountService
{
    public async Task<bool> AnyAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.LocalAccounts.AnyAsync(ct);
    }

    public async Task<OperationResult> CreateAsync(string userName, string password, string actor, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userName)) return OperationResult.Fail("Enter a user name.");
        if (password.Length < 8) return OperationResult.Fail("The password must have at least 8 characters.");
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var normalized = userName.Trim().ToLowerInvariant();
        if (await db.LocalAccounts.AnyAsync(a => a.NormalizedUserName == normalized, ct)) return OperationResult.Fail("That user name is taken.");
        db.LocalAccounts.Add(new LocalAccount
        {
            Id = Guid.NewGuid(), UserName = userName.Trim(), NormalizedUserName = normalized, PasswordHash = PasswordHasher.Hash(password),
            CreatedAtUtc = DateTime.UtcNow, CreatedBy = actor,
        });
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(actor, AuditActions.LocalAccountCreated, "LocalAccount", userName, ct: ct);
        return OperationResult.Success;
    }

    public async Task<OperationResult> CreateFirstAsync(string userName, string password, CancellationToken ct = default)
    {
        if (await AnyAsync(ct)) return OperationResult.Fail("Setup is closed.");
        return await CreateAsync(userName, password, "setup", ct);
    }

    public async Task<OperationResult> ResetPasswordAsync(Guid id, string newPassword, string actor, CancellationToken ct = default)
    {
        if (newPassword.Length < 8) return OperationResult.Fail("The password must have at least 8 characters.");
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var a = await db.LocalAccounts.FindAsync([id], ct);
        if (a is null) return OperationResult.Fail("Not found.");
        a.PasswordHash = PasswordHasher.Hash(newPassword);
        a.PasswordChangedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(actor, AuditActions.LocalAccountPasswordReset, "LocalAccount", a.UserName, ct: ct);
        return OperationResult.Success;
    }

    public async Task<OperationResult> SetDisabledAsync(Guid id, bool disabled, string actor, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var a = await db.LocalAccounts.FindAsync([id], ct);
        if (a is null) return OperationResult.Fail("Not found.");
        a.Disabled = disabled;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(actor, disabled ? AuditActions.LocalAccountDisabled : AuditActions.LocalAccountEnabled, "LocalAccount", a.UserName, ct: ct);
        return OperationResult.Success;
    }
}

/// <summary>Simplified save with stable ids; good enough to exercise the editor.</summary>
internal sealed class DevAnnouncementService(IDbContextFactory<AppDbContext> dbFactory, TimeProvider time) : IAnnouncementService
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().DisableHtml().Build();

    public async Task<AnnouncementEditModel?> GetForEditAsync(Guid? id, CancellationToken ct = default)
    {
        var now = time.GetUtcNow().UtcDateTime;
        if (id is null)
            return new AnnouncementEditModel { StartUtc = now, EndUtc = now.AddDays(7), Buttons = [new ButtonEdit { Label = "OK" }] };
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var a = await db.Announcements.AsNoTracking().Include(x => x.Buttons).Include(x => x.Questions).ThenInclude(q => q.Options)
            .Include(x => x.Targets).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (a is null) return null;
        var answered = await db.Answers.Where(x => x.AnswerSet!.AnnouncementId == a.Id).Select(x => x.QuestionId).Distinct().ToListAsync(ct);
        return new AnnouncementEditModel
        {
            Id = a.Id, Title = a.Title, BodyMarkdown = a.BodyMarkdown, MinDismissSeconds = a.MinDismissSeconds, UnlockMode = a.UnlockMode,
            AnonymousAnswers = a.AnonymousAnswers, StartUtc = a.StartUtc, EndUtc = a.EndUtc, MaxDisplays = a.MaxDisplays,
            RepeatIntervalMinutes = a.RepeatIntervalMinutes, IsDraft = a.IsDraft,
            Buttons = a.Buttons.Where(b => !b.Hidden).OrderBy(b => b.SortOrder).Select(b => new ButtonEdit { Id = b.Id, Label = b.Label }).ToList(),
            Questions = a.Questions.OrderBy(q => q.SortOrder).Select(q => new QuestionEdit
            {
                Id = q.Id, Prompt = q.Prompt, Kind = q.Kind, Required = q.Required, Hidden = q.Hidden, HasAnswers = answered.Contains(q.Id),
                Options = q.Options.Where(o => !o.Hidden).OrderBy(o => o.SortOrder).Select(o => new OptionEdit { Id = o.Id, Label = o.Label }).ToList(),
            }).ToList(),
            Targets = a.Targets.Select(t => new TargetEdit
            {
                Kind = t.Kind, DirectoryObjectGuid = t.DirectoryObjectGuid, ComputerId = t.ComputerId, Value = t.Value, DisplayName = t.DisplayName,
            }).ToList(),
        };
    }

    public async Task<(OperationResult Result, Guid? Id)> SaveAsync(AnnouncementEditModel model, string actor, CancellationToken ct = default)
    {
        var errors = Validate(model);
        if (errors.Count > 0) return (OperationResult.Fail(string.Join(" ", errors)), null);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = time.GetUtcNow().UtcDateTime;
        Announcement? a = null;
        if (model.Id is { } id)
            a = await db.Announcements.Include(x => x.Buttons).Include(x => x.Questions).ThenInclude(q => q.Options)
                .Include(x => x.Targets).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (a is null)
        {
            a = new Announcement { Id = model.Id ?? Guid.NewGuid(), CreatedAtUtc = now, CreatedBy = actor };
            db.Announcements.Add(a);
        }
        a.Title = model.Title.Trim();
        a.BodyMarkdown = model.BodyMarkdown;
        a.MinDismissSeconds = model.MinDismissSeconds;
        a.UnlockMode = model.UnlockMode;
        a.AnonymousAnswers = model.AnonymousAnswers;
        a.StartUtc = DateTime.SpecifyKind(model.StartUtc, DateTimeKind.Utc);
        a.EndUtc = DateTime.SpecifyKind(model.EndUtc, DateTimeKind.Utc);
        a.MaxDisplays = model.MaxDisplays;
        a.RepeatIntervalMinutes = model.RepeatIntervalMinutes;
        a.IsDraft = model.IsDraft;
        a.UpdatedAtUtc = now;
        a.UpdatedBy = actor;

        foreach (var b in a.Buttons) b.Hidden = true;
        for (var i = 0; i < model.Buttons.Count; i++)
        {
            var e = model.Buttons[i];
            var b = a.Buttons.FirstOrDefault(x => x.Id == e.Id);
            if (b is null) { b = new AnnouncementButtonEntity { Id = Guid.NewGuid(), AnnouncementId = a.Id }; db.AnnouncementButtons.Add(b); }
            b.Label = e.Label; b.SortOrder = i; b.Hidden = false;
        }
        foreach (var q in a.Questions) q.Hidden = true;
        for (var i = 0; i < model.Questions.Count; i++)
        {
            var e = model.Questions[i];
            var q = a.Questions.FirstOrDefault(x => x.Id == e.Id);
            if (q is null) { q = new AnnouncementQuestionEntity { Id = Guid.NewGuid(), AnnouncementId = a.Id }; db.AnnouncementQuestions.Add(q); }
            q.Prompt = e.Prompt; q.Kind = e.Kind; q.Required = e.Required; q.SortOrder = i; q.Hidden = e.Hidden;
            foreach (var o in q.Options) o.Hidden = true;
            for (var j = 0; j < e.Options.Count; j++)
            {
                var oe = e.Options[j];
                var o = q.Options.FirstOrDefault(x => x.Id == oe.Id);
                if (o is null) { o = new QuestionOption { Id = Guid.NewGuid(), QuestionId = q.Id }; db.QuestionOptions.Add(o); }
                o.Label = oe.Label; o.SortOrder = j; o.Hidden = false;
            }
        }
        db.AnnouncementTargets.RemoveRange(a.Targets);
        foreach (var t in model.Targets)
            db.AnnouncementTargets.Add(new AnnouncementTarget
            {
                Id = Guid.NewGuid(), AnnouncementId = a.Id, Kind = t.Kind, DirectoryObjectGuid = t.DirectoryObjectGuid, ComputerId = t.ComputerId,
                Value = t.Value, DisplayName = t.DisplayName,
            });
        await db.SaveChangesAsync(ct);
        return (OperationResult.Success, a.Id);
    }

    public async Task<OperationResult> DeleteAsync(Guid id, string actor, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.Announcements.Where(a => a.Id == id).ExecuteDeleteAsync(ct);
        return OperationResult.Success;
    }

    public IReadOnlyList<string> Validate(AnnouncementEditModel model)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(model.Title)) errors.Add("Enter a title.");
        if (model.EndUtc <= model.StartUtc) errors.Add("The end must be after the start.");
        if (model.Buttons.Count == 0) errors.Add("Add at least one button.");
        if (model.Buttons.Any(b => string.IsNullOrWhiteSpace(b.Label))) errors.Add("Every button needs a label.");
        if (model.Questions.Any(q => !q.Hidden && q.Options.Count < 2)) errors.Add("Every question needs at least two options.");
        return errors;
    }

    public string RenderPreviewHtml(string markdown) => Markdown.ToHtml(markdown ?? "", Pipeline);
}

internal sealed class DevResultsExport(IDbContextFactory<AppDbContext> dbFactory) : IResultsExport
{
    public async Task<byte[]> ExportConfirmationsCsvAsync(Guid announcementId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.Confirmations.Where(c => c.AnnouncementId == announcementId).OrderBy(c => c.ConfirmedAtUtc).ToListAsync(ct);
        var sb = new StringBuilder("User,Computer,Time,Button\r\n");
        foreach (var r in rows) sb.Append($"{r.UserName},{r.ComputerName},{r.ConfirmedAtUtc:O},{r.ButtonLabel}\r\n");
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(sb.ToString())];
    }

    public Task<byte[]> ExportAnswersCsvAsync(Guid announcementId, CancellationToken ct = default) =>
        Task.FromResult<byte[]>([.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes("Question,Selected options\r\n")]);
}

internal sealed class DevComputerAdminService(IDbContextFactory<AppDbContext> dbFactory, IAuditLog audit) : IComputerAdminService
{
    public async Task<OperationResult> RevokeAsync(Guid computerId, string actor, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var c = await db.Computers.FindAsync([computerId], ct);
        if (c is null) return OperationResult.Fail("Not found.");
        c.RevokedAtUtc = DateTime.UtcNow;
        c.RevokedBy = actor;
        c.Online = false;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(actor, AuditActions.ComputerRevoked, "Computer", computerId.ToString(), c.Name, ct);
        return OperationResult.Success;
    }

    public async Task<OperationResult> DeleteAsync(Guid computerId, string actor, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.Computers.Where(c => c.Id == computerId).ExecuteDeleteAsync(ct);
        await audit.WriteAsync(actor, AuditActions.ComputerDeleted, "Computer", computerId.ToString(), ct: ct);
        return OperationResult.Success;
    }

    public async Task<OperationResult> ApproveReplaceAsync(Guid newComputerId, string actor, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var n = await db.Computers.FindAsync([newComputerId], ct);
        if (n is null) return OperationResult.Fail("Not found.");
        if (n.ConflictsWithComputerId is { } oldId && await db.Computers.FindAsync([oldId], ct) is { } old)
        {
            n.DirectoryObjectGuid = old.DirectoryObjectGuid;
            old.DirectoryObjectGuid = null;
            old.ReplacedByComputerId = n.Id;
            old.ReplacedAtUtc = DateTime.UtcNow;
            old.AdLinkState = AdLinkState.None;
        }
        n.AdLinkState = AdLinkState.MatchedByName;
        n.ConflictsWithComputerId = null;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(actor, AuditActions.ComputerApprovedReplace, "Computer", newComputerId.ToString(), ct: ct);
        return OperationResult.Success;
    }

    public async Task<OperationResult> KeepSeparateAsync(Guid newComputerId, string actor, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var n = await db.Computers.FindAsync([newComputerId], ct);
        if (n is null) return OperationResult.Fail("Not found.");
        n.AdLinkState = AdLinkState.KeptSeparate;
        n.ConflictsWithComputerId = null;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(actor, AuditActions.ComputerKeptSeparate, "Computer", newComputerId.ToString(), ct: ct);
        return OperationResult.Success;
    }

    public async Task<IReadOnlyList<Guid>> GetHistoryChainAsync(Guid computerId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var result = new List<Guid> { computerId };
        var frontier = new List<Guid> { computerId };
        while (frontier.Count > 0 && result.Count < 100)
        {
            var current = frontier;
            frontier = await db.Computers.Where(c => c.ReplacedByComputerId != null && current.Contains(c.ReplacedByComputerId.Value))
                .Select(c => c.Id).ToListAsync(ct);
            frontier.RemoveAll(result.Contains);
            result.AddRange(frontier);
        }
        return result;
    }
}

internal sealed class DevEnrolmentAdmin : IEnrolmentAdmin
{
    private bool _required = true;
    private bool _passwordSet;
    private AwaitingApprovalPolicy _policy;

    public Task<EnrolmentState> GetStateAsync(CancellationToken ct = default) =>
        Task.FromResult(new EnrolmentState(_required, _passwordSet, true, _passwordSet));

    public Task<OperationResult> SetPasswordAsync(string newPassword, string actor, CancellationToken ct = default)
    {
        if (newPassword.Length < 8) return Task.FromResult(OperationResult.Fail("At least 8 characters."));
        _passwordSet = true;
        return Task.FromResult(OperationResult.Success);
    }

    public Task<OperationResult> SetRequirePasswordAsync(bool required, string actor, CancellationToken ct = default)
    {
        _required = required;
        return Task.FromResult(OperationResult.Success);
    }

    public Task<AwaitingApprovalPolicy> GetAwaitingApprovalPolicyAsync(CancellationToken ct = default) => Task.FromResult(_policy);

    public Task SetAwaitingApprovalPolicyAsync(AwaitingApprovalPolicy policy, string actor, CancellationToken ct = default)
    {
        _policy = policy;
        return Task.CompletedTask;
    }
}

internal sealed class DevDirectoryAdmin : IDirectoryAdmin
{
    public bool IsConfigured => false;
    public Task<DirectorySyncStatus> GetStatusAsync(CancellationToken ct = default) =>
        Task.FromResult(new DirectorySyncStatus(false, false, null, null, null, 0));
    public Task RequestSyncAsync(bool full, string actor, CancellationToken ct = default) => Task.CompletedTask;
    public Task<OperationResult> SetBindPasswordAsync(string password, string actor, CancellationToken ct = default) =>
        Task.FromResult(OperationResult.Fail("No directory in development."));
    public Task<OperationResult> TestConnectionAsync(CancellationToken ct = default) =>
        Task.FromResult(OperationResult.Fail("No directory in development."));
}

internal sealed class DevServerUpdateService : IServerUpdateService
{
    private static ServerUpdateStatus Status => new(false, AppVersion.Current, null, false, null, null, null, false);
    public Task<ServerUpdateStatus> GetStatusAsync(CancellationToken ct = default) => Task.FromResult(Status);
    public Task<ServerUpdateStatus> CheckNowAsync(string actor, CancellationToken ct = default) => Task.FromResult(Status);
    public Task<OperationResult> UpdateNowAsync(string actor, CancellationToken ct = default) =>
        Task.FromResult(OperationResult.Fail("Updates are not configured."));
}

internal sealed class DevAgentPackageCache : IAgentPackageCache
{
    public Task<AgentPackageInfo?> GetCurrentAsync(CancellationToken ct = default) => Task.FromResult<AgentPackageInfo?>(null);
    public Task<IReadOnlyList<AgentPackageInfo>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AgentPackageInfo>>([]);
    public Stream OpenRead(AgentPackageInfo package) => throw new FileNotFoundException();
    public Task<OperationResult> ImportAsync(Stream msi, string fileName, string actor, CancellationToken ct = default) =>
        Task.FromResult(OperationResult.Fail("The package cache is not available in development."));
}

internal sealed class DevAgentUpdateService(IDbContextFactory<AppDbContext> dbFactory) : IAgentUpdateService
{
    public async Task<(OperationResult Result, int Created)> RequestUpdateAsync(IReadOnlyCollection<Guid> computerIds, string actor, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var computers = await db.Computers.Where(c => computerIds.Contains(c.Id)).ToListAsync(ct);
        var created = 0;
        foreach (var c in computers.Where(c => AppVersion.IsAgentOutdated(c.AgentVersion)))
        {
            var now = DateTime.UtcNow;
            db.AgentUpdateTasks.Add(new AgentUpdateTask
            {
                Id = Guid.NewGuid(), ComputerId = c.Id, FromVersion = c.AgentVersion, ToVersion = AppVersion.Current, RequestedBy = actor,
                State = AgentUpdateState.Pending, CreatedAtUtc = now, UpdatedAtUtc = now,
            });
            created++;
        }
        await db.SaveChangesAsync(ct);
        return (OperationResult.Success, created);
    }

    public async Task<(OperationResult Result, int Created)> RequestUpdateAllOutdatedAsync(string actor, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var ids = await db.Computers.Where(c => c.RevokedAtUtc == null).Select(c => c.Id).ToListAsync(ct);
        return await RequestUpdateAsync(ids, actor, ct);
    }

    public async Task<OperationResult> CancelAsync(Guid taskId, string actor, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var t = await db.AgentUpdateTasks.FindAsync([taskId], ct);
        if (t is null) return OperationResult.Fail("Not found.");
        t.State = AgentUpdateState.Cancelled;
        t.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return OperationResult.Success;
    }
}

internal sealed class DevServerIdentity : IServerIdentity
{
    public DevServerIdentity()
    {
        Certificate = KeyFingerprint.CreateSelfSigned(ECDsa.Create(ECCurve.NamedCurves.nistP256), "DomainNotice dev");
        Fingerprint = KeyFingerprint.Compute(Certificate);
    }

    public System.Security.Cryptography.X509Certificates.X509Certificate2 Certificate { get; }
    public string Fingerprint { get; }
}

internal sealed class DevAgentHub : IAgentHub
{
    public bool IsOnline(Guid computerId) => false;
    public IReadOnlyCollection<Guid> OnlineComputerIds => [];
    public IAgentConnection? Get(Guid computerId) => null;
    public Task<bool> TrySendAsync<T>(Guid computerId, T message, CancellationToken ct = default) where T : IAgentMessage => Task.FromResult(false);
    public Task DisconnectAsync(Guid computerId, string refusedReason, string? message = null) => Task.CompletedTask;
}
