using DomainNotice.Server.Abstractions;
using DomainNotice.Server.Data;
using Microsoft.EntityFrameworkCore;
using DomainNotice.Shared.Protocol;

namespace DomainNotice.Server.AgentLink;

/// <summary>Console actions on computers (plan 4, 9.2). Every action is audited.</summary>
internal sealed class ComputerAdminService(
    IDbContextFactory<AppDbContext> dbFactory,
    AgentHub hub,
    AdLinkService adLink,
    AgentObservers events,
    IAuditLog audit,
    TimeProvider time) : IComputerAdminService
{
    public async Task<OperationResult> RevokeAsync(Guid computerId, string actor, CancellationToken ct = default)
    {
        string name;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var computer = await db.Computers.FirstOrDefaultAsync(c => c.Id == computerId, ct);
            if (computer is null) return OperationResult.Fail("Computer not found.");
            if (computer.IsRevoked) return OperationResult.Fail("The computer is already revoked.");
            computer.RevokedAtUtc = time.GetUtcNow().UtcDateTime;
            computer.RevokedBy = actor;
            name = computer.Name;
            await db.SaveChangesAsync(ct);
        }
        await hub.DisconnectAsync(computerId, RefusedReasons.Revoked, "This computer was revoked by an administrator.");
        await audit.WriteAsync(actor, AuditActions.ComputerRevoked, "Computer", computerId.ToString(), name, ct);
        await events.RaiseComputerIdentityChangedAsync(computerId, ct);
        await adLink.ReevaluateWaitingOnAsync(computerId, ct);
        return OperationResult.Success;
    }

    public async Task<OperationResult> DeleteAsync(Guid computerId, string actor, CancellationToken ct = default)
    {
        string name;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var computer = await db.Computers.AsNoTracking().FirstOrDefaultAsync(c => c.Id == computerId, ct);
            if (computer is null) return OperationResult.Fail("Computer not found.");
            name = computer.Name;
        }
        // Its key becomes unknown: the agent may enrol again (with the install password when one is required).
        await hub.DisconnectAsync(computerId, RefusedReasons.UnknownKey, "This computer was deleted by an administrator.");
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            await db.Computers.Where(c => c.Id == computerId).ExecuteDeleteAsync(ct);
            await db.Computers.Where(c => c.ConflictsWithComputerId == computerId)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.ConflictsWithComputerId, (Guid?)null), ct);
        }
        // A reconnect between the first disconnect and the delete would still be online: close it too.
        await hub.DisconnectAsync(computerId, RefusedReasons.UnknownKey, "This computer was deleted by an administrator.");
        await audit.WriteAsync(actor, AuditActions.ComputerDeleted, "Computer", computerId.ToString(), name, ct);
        await events.RaiseComputerIdentityChangedAsync(computerId, ct);
        await adLink.EvaluatePendingAsync(ct);
        return OperationResult.Success;
    }

    public async Task<OperationResult> ApproveReplaceAsync(Guid newComputerId, string actor, CancellationToken ct = default)
    {
        var (ok, error, oldId) = await adLink.ApproveReplaceAsync(newComputerId, ct);
        if (!ok) return OperationResult.Fail(error!);
        await audit.WriteAsync(actor, AuditActions.ComputerApprovedReplace, "Computer", newComputerId.ToString(),
            $"replaces {oldId}", ct);
        return OperationResult.Success;
    }

    public async Task<OperationResult> KeepSeparateAsync(Guid newComputerId, string actor, CancellationToken ct = default)
    {
        var (ok, error) = await adLink.KeepSeparateAsync(newComputerId, ct);
        if (!ok) return OperationResult.Fail(error!);
        await audit.WriteAsync(actor, AuditActions.ComputerKeptSeparate, "Computer", newComputerId.ToString(), null, ct);
        return OperationResult.Success;
    }

    public async Task<IReadOnlyList<Guid>> GetHistoryChainAsync(Guid computerId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var result = new List<Guid> { computerId };
        var seen = new HashSet<Guid> { computerId };
        var frontier = new List<Guid> { computerId };
        while (frontier.Count > 0)
        {
            var next = await db.Computers.AsNoTracking()
                .Where(c => c.ReplacedByComputerId != null && frontier.Contains(c.ReplacedByComputerId.Value))
                .OrderBy(c => c.EnrolledAtUtc).Select(c => c.Id).ToListAsync(ct);
            frontier = next.Where(seen.Add).ToList();
            result.AddRange(frontier);
        }
        return result;
    }
}
