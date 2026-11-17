using DomainNotice.Server.Abstractions;
using DomainNotice.Server.Data;
using DomainNotice.Server.Data.Entities;
using DomainNotice.Shared;
using DomainNotice.Shared.Protocol;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DomainNotice.Server.Announcements;

/// <summary>
/// Pushes due announcements to online agents (plan 8.3, P8) and withdraws windows of announcements that are no
/// longer active. Rules:
/// <list type="bullet">
/// <item>Sessions in state Active or Locked are served; Disconnected sessions are skipped.</item>
/// <item>At most one open delivery (sent, not confirmed, not withdrawn) per (computer, user). The next one goes after Confirm.</item>
/// <item>An open delivery is re-sent with the same deliveryId on reconnect and on logon.</item>
/// <item>No second delivery of an announcement while the user has an unconfirmed shown one on another computer.</item>
/// </list>
/// Concurrency: one lock per computer, and inside it one lock per user key (always in that order, one user lock at
/// a time) for the cross-computer per-user rules. State transitions on Delivery rows use conditional updates.
/// </summary>
internal sealed class DeliveryCoordinator : IAgentLifecycleObserver
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IServiceProvider _services;
    private readonly TimeProvider _time;
    private readonly TargetingDataLoader _loader;
    private readonly ILogger<DeliveryCoordinator> _logger;
    private readonly KeyedLocks<Guid> _computerLocks = new();
    private readonly KeyedLocks<string> _userLocks = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _wake = new(0, 1);
    private IAgentHub? _hub;
    private bool _hubMissingLogged;

    public DeliveryCoordinator(IDbContextFactory<AppDbContext> dbFactory, IServiceProvider services, TimeProvider time,
        TargetingDataLoader loader, ILogger<DeliveryCoordinator> logger)
    {
        _dbFactory = dbFactory;
        _services = services;
        _time = time;
        _loader = loader;
        _logger = logger;
    }

    /// <summary>Resolved lazily: the hub depends on the observers and handlers, so a constructor dependency would be a cycle.</summary>
    private IAgentHub? Hub
    {
        get
        {
            if (_hub is not null) return _hub;
            _hub = _services.GetService<IAgentHub>();
            if (_hub is null && !_hubMissingLogged)
            {
                _hubMissingLogged = true;
                _logger.LogWarning("No IAgentHub is registered; announcements cannot be delivered.");
            }
            return _hub;
        }
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    // ------------------------------------------------------------------ IAgentLifecycleObserver

    public Task OnAgentOnlineAsync(IAgentConnection connection, CancellationToken ct) =>
        EvaluateComputerAsync(connection.ComputerId, connection.Sessions, resendAll: true, resendUserKeys: null, ct);

    public Task OnSessionsChangedAsync(IAgentConnection connection, SessionsChange change, CancellationToken ct)
    {
        var loggedOn = change.LoggedOn.Select(s => UserKeys.Normalize(s.UserName)).Where(k => k.Length > 0).ToHashSet(StringComparer.Ordinal);
        return EvaluateComputerAsync(connection.ComputerId, change.Current, resendAll: false, loggedOn, ct);
    }

    public Task OnComputerIdentityChangedAsync(Guid computerId, CancellationToken ct)
    {
        var sessions = Hub?.Get(computerId)?.Sessions;
        return sessions is null ? Task.CompletedTask : EvaluateComputerAsync(computerId, sessions, false, null, ct);
    }

    // ------------------------------------------------------------------ Triggers

    /// <summary>Asks the scheduler to run a full evaluation now (e.g. after an announcement was saved).</summary>
    public void RequestEvaluation()
    {
        try { if (_wake.CurrentCount == 0) _wake.Release(); }
        catch (SemaphoreFullException) { }
    }

    /// <summary>Waits for <see cref="RequestEvaluation"/> or the timeout.</summary>
    internal async Task WaitForWakeAsync(TimeSpan timeout, CancellationToken ct)
    {
        try { await _wake.WaitAsync(timeout, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    /// <summary>One scheduler pass: withdraw inactive announcements' open deliveries, then evaluate every online computer.</summary>
    public async Task EvaluateAllOnlineAsync(CancellationToken ct)
    {
        await WithdrawInactiveAsync(null, ct);
        var hub = Hub;
        if (hub is null) return;
        var online = hub.OnlineComputerIds.ToList();
        if (online.Count == 0) return;

        await using (var db = await _dbFactory.CreateDbContextAsync(ct))
        {
            var now = Now;
            var anyActive = await db.Announcements.AnyAsync(a => !a.IsDraft && a.StartUtc <= now && a.EndUtc >= now, ct);
            if (!anyActive) return;
        }

        await Parallel.ForEachAsync(online, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct },
            async (id, token) =>
            {
                var sessions = hub.Get(id)?.Sessions;
                if (sessions is null) return;
                try { await EvaluateComputerAsync(id, sessions, false, null, token); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Announcement evaluation failed for computer {ComputerId}", id);
                }
            });
    }

    // ------------------------------------------------------------------ Evaluation

    public async Task EvaluateComputerAsync(Guid computerId, IReadOnlyList<SessionInfo> sessions, bool resendAll,
        IReadOnlySet<string>? resendUserKeys, CancellationToken ct)
    {
        using (await _computerLocks.AcquireAsync(computerId, ct))
            await EvaluateLockedAsync(computerId, sessions, resendAll, resendUserKeys, ct);
    }

    internal Task<KeyedLocks<Guid>.Releaser> LockComputerAsync(Guid computerId, CancellationToken ct) => _computerLocks.AcquireAsync(computerId, ct);
    internal Task<KeyedLocks<string>.Releaser> LockUserAsync(string userKey, CancellationToken ct) => _userLocks.AcquireAsync(userKey, ct);

    /// <summary>Caller holds the computer lock.</summary>
    internal async Task EvaluateLockedAsync(Guid computerId, IReadOnlyList<SessionInfo> sessions, bool resendAll,
        IReadOnlySet<string>? resendUserKeys, CancellationToken ct)
    {
        var users = sessions
            .Where(s => s.State is SessionState.Active or SessionState.Locked && !string.IsNullOrWhiteSpace(s.UserName))
            .GroupBy(s => UserKeys.Normalize(s.UserName), StringComparer.Ordinal)
            .Select(g => (Key: g.Key, Name: g.First().UserName.Trim()))
            .ToList();

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var now = Now;

        // Open deliveries on this computer whose announcement is no longer active are withdrawn first.
        var open = await db.Deliveries.AsNoTracking()
            .Where(d => d.ComputerId == computerId && d.ConfirmedAtUtc == null && d.WithdrawnAtUtc == null)
            .Select(d => new
            {
                d.Id, d.UserKey, d.AnnouncementId, d.UserName,
                Active = !d.Announcement!.IsDraft && d.Announcement.StartUtc <= now && d.Announcement.EndUtc >= now,
            })
            .ToListAsync(ct);
        var inactive = open.Where(o => !o.Active).Select(o => (o.Id, computerId)).ToList();
        if (inactive.Count > 0) await WithdrawAsync(db, inactive, ct);
        if (users.Count == 0) return;

        var computer = await _loader.LoadComputerAsync(db, computerId, ct);
        if (computer is null) return;
        var policy = await _loader.GetAwaitingApprovalPolicyAsync(ct);
        var reachNothing = Eligibility.ReachOf(computer, policy) == ComputerReach.Nothing;

        var openByUser = open.Where(o => o.Active).GroupBy(o => o.UserKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        List<LoadedAnnouncement>? active = null;
        foreach (var user in users)
        {
            if (openByUser.TryGetValue(user.Key, out var existing))
            {
                if (!reachNothing && (resendAll || resendUserKeys?.Contains(user.Key) == true))
                {
                    var announcement = await _loader.LoadActiveAsync(db, now, ct, existing.AnnouncementId);
                    if (announcement.Count == 1)
                        await SendShowAsync(computerId, existing.Id, existing.UserName, announcement[0].Entity, ct);
                }
                continue;
            }
            if (reachNothing) continue;

            active ??= await _loader.LoadActiveAsync(db, now, ct);
            if (active.Count == 0) return;
            await DeliverNextLockedAsync(db, computer, user.Name, user.Key, active, policy, ct);
        }
    }

    /// <summary>Creates and sends the next due delivery for one user, under the user lock.</summary>
    private async Task DeliverNextLockedAsync(AppDbContext db, ComputerFacts computer, string userName, string userKey,
        List<LoadedAnnouncement> active, AwaitingApprovalPolicy policy, CancellationToken ct)
    {
        using var _ = await _userLocks.AcquireAsync(userKey, ct);
        var now = Now;

        // Re-check under the user lock: still no open delivery for (computer, user).
        var hasOpen = await db.Deliveries.AnyAsync(d => d.ComputerId == computer.Id && d.UserKey == userKey
                                                        && d.ConfirmedAtUtc == null && d.WithdrawnAtUtc == null, ct);
        if (hasOpen) return;

        var history = await TargetingDataLoader.LoadHistoryAsync(db, active.Select(a => a.Rule.Id).ToList(), [userKey], computer.Id, ct);

        // Cheap filters first (window, display limit, cross-computer rule), audience last (needs directory lookups).
        var candidates = active.Where(a =>
        {
            var h = history.GetValueOrDefault((a.Rule.Id, userKey), UserAnnouncementHistory.None);
            return Eligibility.IsDeliverableAt(a.Rule, now) && Eligibility.DisplayAllowed(a.Rule, h, now) && !h.HasUnconfirmedShownElsewhere;
        }).ToList();
        if (candidates.Count == 0) return;

        var netBios = await NetBiosDomainAsync(ct);
        // A local account (PC01\alice) of the one supported domain's PCs is not the AD user alice: no AD facts.
        var user = netBios is not null && !IdentityMatch.IsDomainAccount(userName, netBios)
            ? UserFacts.NameOnly(userName)
            : await _loader.LoadUserAsync(db, userName, ct);
        user = user with { NetBiosDomain = netBios };
        var due =candidates.FirstOrDefault(a => Eligibility.AudienceMatches(a.Rule.Targets, computer, user, policy));
        if (due is null) return;

        var delivery = new Delivery
        {
            Id = Guid.NewGuid(),
            AnnouncementId = due.Rule.Id,
            ComputerId = computer.Id,
            UserName = userName,
            UserKey = userKey,
            SentAtUtc = now,
        };
        db.Deliveries.Add(delivery);
        await db.SaveChangesAsync(ct);
        _logger.LogInformation("Delivering announcement {AnnouncementId} to {User} on {ComputerId} (delivery {DeliveryId})",
            due.Rule.Id, userName, computer.Id, delivery.Id);
        await SendShowAsync(computer.Id, delivery.Id, userName, due.Entity, ct);
    }

    private static readonly TimeSpan NetBiosCacheTime = TimeSpan.FromMinutes(1);
    private (string? Value, DateTime ReadUtc) _netBios = (null, DateTime.MinValue);
    private bool _netBiosMissingLogged;

    /// <summary>The domain's NetBIOS name (written by the directory sync), cached for a minute; null when unknown.</summary>
    private async Task<string?> NetBiosDomainAsync(CancellationToken ct)
    {
        var cached = _netBios;
        if (Now - cached.ReadUtc < NetBiosCacheTime) return cached.Value;
        var settings = _services.GetService<ISettingsStore>();
        var value = settings is null ? null : await settings.GetAsync(SettingKeys.DirectoryNetBiosName, ct);
        value = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        _netBios = (value, Now);
        if (value is null && !_netBiosMissingLogged)
        {
            _netBiosMissingLogged = true;
            _logger.LogInformation("The domain's NetBIOS name is not known yet (no directory sync); user targets typed as names " +
                                   "also match accounts of other domains or local accounts with that name.");
        }
        return value;
    }

    private async Task SendShowAsync(Guid computerId, Guid deliveryId, string userName, Announcement announcement, CancellationToken ct)
    {
        var hub = Hub;
        if (hub is null) return;
        var sent = await hub.TrySendAsync(computerId, new Show
        {
            DeliveryId = deliveryId,
            SessionUser = userName,
            Announcement = ToPayload(announcement, await HeaderTextAsync(ct), _services.GetService<IAppLanguage>()?.Current),
        }, ct);
        if (!sent)
            _logger.LogInformation("Show {DeliveryId} not sent (computer {ComputerId} offline); it is re-sent on reconnect", deliveryId, computerId);
    }

    /// <summary>The window content: only items that are not hidden, in sort order.</summary>
    private async Task<string?> HeaderTextAsync(CancellationToken ct)
    {
        var window = _services.GetService<IAnnouncementWindowSettings>();
        return window is null ? null : await window.GetHeaderTextAsync(ct);
    }

    public static AnnouncementPayload ToPayload(Announcement a, string? headerText = null, string? language = null) => new()
    {
        Id = a.Id,
        Title = a.DisplayTitle,
        HeaderText = headerText,
        Language = language,
        BodyMarkdown = a.BodyMarkdown,
        MinDismissSeconds = a.MinDismissSeconds,
        UnlockMode = a.UnlockMode,
        AnonymousAnswers = a.AnonymousAnswers,
        Buttons = a.Buttons.Where(b => !b.Hidden).OrderBy(b => b.SortOrder)
            .Select(b => new AnnouncementButton { Id = b.Id, Label = b.Label }).ToList(),
        Questions = a.Questions.Where(q => !q.Hidden).OrderBy(q => q.SortOrder)
            .Select(q => new AnnouncementQuestion
            {
                Id = q.Id,
                Prompt = q.Prompt,
                Kind = q.Kind,
                Required = q.Required,
                Options = q.Options.Where(o => !o.Hidden).OrderBy(o => o.SortOrder)
                    .Select(o => new AnnouncementOption { Id = o.Id, Label = o.Label }).ToList(),
            }).ToList(),
    };

    // ------------------------------------------------------------------ Withdraw

    /// <summary>
    /// Withdraws open deliveries of announcements that are not active now (ended, draft, or moved to the future),
    /// on all computers. WithdrawnAtUtc is always set; Withdraw is sent to computers that are online.
    /// </summary>
    public async Task<int> WithdrawInactiveAsync(Guid? announcementId, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var now = Now;
        var query = db.Deliveries.AsNoTracking()
            .Where(d => d.ConfirmedAtUtc == null && d.WithdrawnAtUtc == null
                        && (d.Announcement!.IsDraft || d.Announcement.StartUtc > now || d.Announcement.EndUtc < now));
        if (announcementId is { } id) query = query.Where(d => d.AnnouncementId == id);
        var rows = await query.Select(d => new { d.Id, d.ComputerId }).ToListAsync(ct);
        if (rows.Count == 0) return 0;
        await WithdrawAsync(db, rows.Select(r => (r.Id, r.ComputerId)).ToList(), ct);
        return rows.Count;
    }

    /// <summary>Marks open deliveries withdrawn (conditional update) and sends Withdraw to those computers.</summary>
    internal async Task WithdrawAsync(AppDbContext db, IReadOnlyList<(Guid DeliveryId, Guid ComputerId)> deliveries, CancellationToken ct)
    {
        if (deliveries.Count == 0) return;
        var now = Now;
        var ids = deliveries.Select(d => d.DeliveryId).ToList();
        await db.Deliveries
            .Where(d => ids.Contains(d.Id) && d.ConfirmedAtUtc == null && d.WithdrawnAtUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.WithdrawnAtUtc, now), ct);
        await SendWithdrawAsync(deliveries, ct);
    }

    /// <summary>Sends Withdraw only (e.g. after delete, when the rows are already gone).</summary>
    internal async Task SendWithdrawAsync(IReadOnlyList<(Guid DeliveryId, Guid ComputerId)> deliveries, CancellationToken ct)
    {
        var hub = Hub;
        if (hub is null) return;
        foreach (var (deliveryId, computerId) in deliveries)
        {
            try
            {
                if (hub.IsOnline(computerId))
                    await hub.TrySendAsync(computerId, new Withdraw { DeliveryId = deliveryId }, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Withdraw {DeliveryId} to {ComputerId} failed", deliveryId, computerId);
            }
        }
    }
}
