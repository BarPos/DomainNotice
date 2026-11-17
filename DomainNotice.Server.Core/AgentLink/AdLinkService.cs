using DomainNotice.Server.Abstractions;
using DomainNotice.Server.Data;
using DomainNotice.Server.Data.Entities;
using DomainNotice.Shared.Protocol;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DomainNotice.Server.AgentLink;

/// <summary>
/// The link between an enrolled computer (key) and its AD computer object (plan 3.4, 4, D5, D13).
/// <para>Invariant used by targeting: <see cref="Computer.DirectoryObjectGuid"/> is set only for
/// <see cref="AdLinkState.Proven"/> and <see cref="AdLinkState.MatchedByName"/>. It is null for None,
/// AwaitingApproval, KeptSeparate and replaced records.</para>
/// All link changes run under one lock (low volume; avoids two keys taking the same AD object at once).
/// </summary>
internal sealed class AdLinkService(
    IDbContextFactory<AppDbContext> dbFactory,
    AgentObservers events,
    AgentHub hub,
    IAuditLog audit,
    AgentLinkTimings timings,
    TimeProvider time,
    ILogger<AdLinkService> logger)
{
    /// <summary>Audit actor for link decisions the server makes by itself.</summary>
    internal const string SystemActor = "system (agent link)";

    private readonly SemaphoreSlim _gate = new(1, 1);
    /// <summary>
    /// (newcomer, holder) pairs whose Kerberos proof was held back (in memory). Audited once per pair (agents prove
    /// again on every connect), and the name job leaves these waiting.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(Guid, Guid), byte> _proofHeld = new();

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    /// <summary>sAMAccountName (lowercase, with $) that a self-reported computer name would have.</summary>
    internal static string? SamForName(string? name)
    {
        var n = (name ?? "").Trim();
        var dot = n.IndexOf('.');
        if (dot >= 0) n = n[..dot];
        return n.Length == 0 ? null : n.ToLowerInvariant() + "$";
    }

    /// <summary>Name fallback for one computer. Raises identity-changed when its state changed.</summary>
    public async Task<bool> EvaluateNameAsync(Guid computerId, CancellationToken ct)
    {
        bool changed;
        await _gate.WaitAsync(ct);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var computer = await db.Computers.FirstOrDefaultAsync(c => c.Id == computerId, ct);
            if (computer is null) return false;
            changed = await EvaluateAsync(db, computer, ct);
            if (changed) await db.SaveChangesAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
        if (changed) await events.RaiseComputerIdentityChangedAsync(computerId, ct);
        return changed;
    }

    /// <summary>Periodic job: computers in None or AwaitingApproval (directory sync may have arrived since).</summary>
    public async Task<int> EvaluatePendingAsync(CancellationToken ct)
    {
        List<Guid> ids;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            ids = await db.Computers
                .Where(c => c.RevokedAtUtc == null && c.ReplacedByComputerId == null
                            && (c.AdLinkState == AdLinkState.None || c.AdLinkState == AdLinkState.AwaitingApproval))
                .Select(c => c.Id).ToListAsync(ct);
        }
        var changed = 0;
        foreach (var id in ids)
            if (await EvaluateNameAsync(id, ct)) changed++;
        return changed;
    }

    private async Task<bool> EvaluateAsync(AppDbContext db, Computer computer, CancellationToken ct)
    {
        if (computer.IsRevoked || computer.IsReplaced) return false;
        if (computer.AdLinkState is AdLinkState.Proven or AdLinkState.KeptSeparate) return false;
        // Waiting because this computer's own Kerberos proof was held back (see ApplyProofAsync): only an
        // administrator, or the holder being revoked or deleted, ends the wait. Waits caused by the name alone are
        // re-evaluated as before.
        if (computer.AdLinkState == AdLinkState.AwaitingApproval && computer.ConflictsWithComputerId is { } holderId
            && _proofHeld.ContainsKey((computer.Id, holderId))
            && await db.Computers.AnyAsync(c => c.Id == holderId && c.AdLinkState == AdLinkState.Proven && c.RevokedAtUtc == null
                                                && c.ReplacedByComputerId == null && c.DirectoryObjectGuid != null, ct))
            return false;

        var sam = SamForName(computer.Name);
        if (computer.AdLinkState == AdLinkState.MatchedByName && computer.DirectoryObjectGuid is { } linkedGuid)
        {
            var linked = await db.DirectoryObjects.AsNoTracking().FirstOrDefaultAsync(o => o.ObjectGuid == linkedGuid, ct);
            // Keep the link while the object is unknown (not synced) or still matches the reported name.
            if (linked is null || (!linked.Deleted && linked.SamAccountNameLower == sam)) return false;
        }

        var (state, guid, conflict) = (AdLinkState.None, (Guid?)null, (Guid?)null);
        if (sam is not null)
        {
            var candidates = await db.DirectoryObjects.AsNoTracking()
                .Where(o => o.Kind == DirectoryObjectKind.Computer && !o.Deleted && o.SamAccountNameLower == sam)
                .Select(o => o.ObjectGuid).Take(2).ToListAsync(ct);
            if (candidates.Count == 1)
            {
                var candidate = candidates[0];
                var holder = await ActiveHoldersQuery(db, candidate, computer.Id).Select(c => (Guid?)c.Id).FirstOrDefaultAsync(ct);
                if (holder is null) (state, guid) = (AdLinkState.MatchedByName, candidate);
                else (state, conflict) = (AdLinkState.AwaitingApproval, holder);
            }
        }

        if (computer.AdLinkState == state && computer.DirectoryObjectGuid == guid && computer.ConflictsWithComputerId == conflict)
            return false;
        logger.LogInformation("AD link of computer {Computer} ({Name}): {Old} -> {New}{Conflict}", computer.Id, computer.Name,
            computer.AdLinkState, state, conflict is null ? "" : $" (conflicts with {conflict})");
        computer.AdLinkState = state;
        computer.DirectoryObjectGuid = guid;
        computer.ConflictsWithComputerId = conflict;
        computer.AdLinkedAtUtc = guid is null ? null : Now;
        return true;
    }

    /// <summary>Other non-revoked, non-replaced records that hold the link to this AD object.</summary>
    private static IQueryable<Computer> ActiveHoldersQuery(AppDbContext db, Guid objectGuid, Guid exceptId) =>
        db.Computers.Where(c => c.Id != exceptId && c.DirectoryObjectGuid == objectGuid && c.RevokedAtUtc == null
                                && c.ReplacedByComputerId == null
                                && (c.AdLinkState == AdLinkState.Proven || c.AdLinkState == AdLinkState.MatchedByName));

    /// <summary>
    /// A valid Kerberos proof for <paramref name="samLower"/> (e.g. "pc1$"): the computer is linked Proven, every
    /// other record linked to that AD object is marked replaced by it and disconnected (plan 3.4 step 4).
    /// <para>Exception: when another non-revoked record already holds a <b>Proven</b> link to that AD object and is
    /// online or was seen within <see cref="AgentLinkTimings.ProofTakeoverQuietPeriod"/>, the link does not move: any
    /// code running as the machine account on that PC could otherwise enrol a second key and take the computer over.
    /// The newcomer is put on Awaiting approval instead (audited once), and an administrator decides there.</para>
    /// </summary>
    public async Task<(bool Linked, DirectoryObject? Object, string? Error)> ApplyProofAsync(Guid computerId, string samLower, CancellationToken ct)
    {
        var replaced = new List<Guid>();
        var repointed = new List<Guid>();
        DirectoryObject? obj;
        Guid? heldBy = null;
        var newlyHeld = false;
        await _gate.WaitAsync(ct);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var matches = await db.DirectoryObjects.AsNoTracking()
                .Where(o => o.Kind == DirectoryObjectKind.Computer && !o.Deleted && o.SamAccountNameLower == samLower)
                .Take(2).ToListAsync(ct);
            if (matches.Count != 1)
                return (false, null, matches.Count == 0
                    ? $"AD computer {samLower} is not in the synced directory."
                    : $"More than one AD computer is named {samLower}.");
            obj = matches[0];
            var computer = await db.Computers.FirstOrDefaultAsync(c => c.Id == computerId, ct);
            if (computer is null || computer.IsRevoked || computer.IsReplaced) return (false, null, "This computer can no longer be linked.");

            var now = Now;
            var recent = now - timings.ProofTakeoverQuietPeriod;
            var provenHolders = await db.Computers.AsNoTracking()
                .Where(c => c.Id != computerId && c.DirectoryObjectGuid == obj.ObjectGuid && c.ReplacedByComputerId == null
                            && c.RevokedAtUtc == null && c.AdLinkState == AdLinkState.Proven)
                .Select(c => new { c.Id, c.LastSeenAtUtc }).ToListAsync(ct);
            if (provenHolders.FirstOrDefault(h => hub.IsOnline(h.Id) || h.LastSeenAtUtc >= recent) is { } holder)
            {
                heldBy = holder.Id;
                if (computer.AdLinkState == AdLinkState.KeptSeparate)
                    return (false, obj, "An administrator chose to keep this computer separate from the AD computer it proved.");
                newlyHeld = computer.AdLinkState != AdLinkState.AwaitingApproval || computer.ConflictsWithComputerId != holder.Id;
                computer.AdLinkState = AdLinkState.AwaitingApproval;
                computer.ConflictsWithComputerId = holder.Id;
                computer.DirectoryObjectGuid = null;
                computer.AdLinkedAtUtc = null;
                await db.SaveChangesAsync(ct);
                if (newlyHeld || !_proofHeld.ContainsKey((computerId, holder.Id)))
                    logger.LogWarning("Computer {Computer} proved AD account {Sam} ({Dn}), but computer {Holder} holds a Kerberos-proven link " +
                                      "to it and was online recently; the link stays, the newcomer awaits approval", computerId, samLower,
                        obj.DistinguishedName, holder.Id);
            }
            else await LinkProvenAsync(db, computer, obj, now, replaced, repointed, ct);
        }
        finally
        {
            _gate.Release();
        }

        if (heldBy is { } heldById)
        {
            if (_proofHeld.TryAdd((computerId, heldById), 0))
                await audit.WriteAsync(SystemActor, AuditActions.ComputerProofAwaitingApproval, "Computer", computerId.ToString(),
                    $"proved {samLower}; computer {heldById} keeps the link until an administrator decides", ct);
            if (newlyHeld) await events.RaiseComputerIdentityChangedAsync(computerId, ct);
            return (false, obj, "Another installation holds this AD computer's link and was online recently. An administrator must " +
                                "approve the replacement in the Console (Computers, Awaiting approval).");
        }

        foreach (var id in replaced)
            await hub.DisconnectAsync(id, RefusedReasons.Replaced, "Another installation proved this AD computer account.");
        await events.RaiseComputerIdentityChangedAsync(computerId, ct);
        foreach (var id in replaced.Concat(repointed))
            await events.RaiseComputerIdentityChangedAsync(id, ct);
        return (true, obj, null);
    }

    /// <summary>Links Proven and marks every other record linked to the AD object replaced (caller holds the gate).</summary>
    private async Task LinkProvenAsync(AppDbContext db, Computer computer, DirectoryObject obj, DateTime now, List<Guid> replaced,
        List<Guid> repointed, CancellationToken ct)
    {
        var computerId = computer.Id;
        computer.AdLinkState = AdLinkState.Proven;
        computer.DirectoryObjectGuid = obj.ObjectGuid;
        computer.AdLinkedAtUtc = now;
        computer.ConflictsWithComputerId = null;

        var olds = await db.Computers
            .Where(c => c.Id != computerId && c.DirectoryObjectGuid == obj.ObjectGuid && c.ReplacedByComputerId == null)
            .ToListAsync(ct);
        foreach (var old in olds) MarkReplaced(old, computerId, now);
        replaced.AddRange(olds.Select(o => o.Id));

        // Newcomers waiting on a record that was just replaced now wait on this one.
        var waiting = await db.Computers
            .Where(c => c.Id != computerId && c.AdLinkState == AdLinkState.AwaitingApproval && c.ConflictsWithComputerId != null
                        && replaced.Contains(c.ConflictsWithComputerId.Value))
            .ToListAsync(ct);
        foreach (var w in waiting) w.ConflictsWithComputerId = computerId;
        repointed.AddRange(waiting.Select(w => w.Id));

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Computer {Computer} proved AD account {Sam} ({Dn}); replaced: {Replaced}",
            computerId, obj.SamAccountNameLower, obj.DistinguishedName, replaced.Count == 0 ? "none" : string.Join(", ", replaced));
    }

    private static void MarkReplaced(Computer old, Guid byId, DateTime now)
    {
        old.ReplacedByComputerId = byId;
        old.ReplacedAtUtc = now;
        old.DirectoryObjectGuid = null;
        old.AdLinkState = AdLinkState.None;
        old.ConflictsWithComputerId = null;
    }

    /// <summary>Awaiting approval → "Replace the old computer".</summary>
    public async Task<(bool Ok, string? Error, Guid? OldId)> ApproveReplaceAsync(Guid newId, CancellationToken ct)
    {
        Guid oldId;
        var reevaluate = false;
        await _gate.WaitAsync(ct);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var computer = await db.Computers.FirstOrDefaultAsync(c => c.Id == newId, ct);
            if (computer is null) return (false, "Computer not found.", null);
            if (computer.AdLinkState != AdLinkState.AwaitingApproval || computer.ConflictsWithComputerId is null)
                return (false, "This computer is not awaiting approval.", null);
            if (computer.IsRevoked || computer.IsReplaced) return (false, "This computer is revoked or replaced.", null);
            oldId = computer.ConflictsWithComputerId.Value;
            var old = await db.Computers.FirstOrDefaultAsync(c => c.Id == oldId, ct);
            var now = Now;
            var guid = old?.DirectoryObjectGuid;
            if (old is not null && old.ReplacedByComputerId is null) MarkReplaced(old, newId, now);
            computer.ConflictsWithComputerId = null;
            if (guid is { } g)
            {
                computer.AdLinkState = AdLinkState.MatchedByName;
                computer.DirectoryObjectGuid = g;
                computer.AdLinkedAtUtc = now;
            }
            else
            {
                // The old record lost its link meanwhile; fall back to the name rules.
                computer.AdLinkState = AdLinkState.None;
                computer.DirectoryObjectGuid = null;
                reevaluate = true;
            }
            await db.SaveChangesAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
        await hub.DisconnectAsync(oldId, RefusedReasons.Replaced, "An administrator replaced this computer record.");
        if (reevaluate) await EvaluateNameAsync(newId, ct);
        await events.RaiseComputerIdentityChangedAsync(newId, ct);
        await events.RaiseComputerIdentityChangedAsync(oldId, ct);
        return (true, null, oldId);
    }

    /// <summary>Awaiting approval → "Keep both": separate, not linked, off the list.</summary>
    public async Task<(bool Ok, string? Error)> KeepSeparateAsync(Guid newId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var computer = await db.Computers.FirstOrDefaultAsync(c => c.Id == newId, ct);
            if (computer is null) return (false, "Computer not found.");
            if (computer.AdLinkState != AdLinkState.AwaitingApproval) return (false, "This computer is not awaiting approval.");
            computer.AdLinkState = AdLinkState.KeptSeparate;
            computer.ConflictsWithComputerId = null;
            computer.DirectoryObjectGuid = null;
            computer.AdLinkedAtUtc = null;
            await db.SaveChangesAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
        await events.RaiseComputerIdentityChangedAsync(newId, ct);
        return (true, null);
    }

    /// <summary>After a holder was revoked or deleted: re-evaluate the computers that waited on it.</summary>
    public async Task ReevaluateWaitingOnAsync(Guid holderId, CancellationToken ct)
    {
        List<Guid> ids;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            ids = await db.Computers.Where(c => c.ConflictsWithComputerId == holderId).Select(c => c.Id).ToListAsync(ct);
        }
        foreach (var id in ids) await EvaluateNameAsync(id, ct);
    }
}
