using System.Collections.Concurrent;
using System.Net;
using DomainNotice.Server.Abstractions;
using DomainNotice.Server.Common;
using DomainNotice.Server.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DomainNotice.Server.AgentLink;

internal enum EnrolOutcome { Accepted, Rejected, RateLimited }

internal sealed record EnrolDecision(EnrolOutcome Outcome, string? Error = null);

/// <summary>
/// Install password check (plan 4) and its Console settings. The database settings (set in the Console) win over
/// the Enrolment config section. Failed attempts are rate-limited per remote IP, in memory, per server instance.
/// </summary>
internal sealed class Enrolment(
    ISettingsStore settings,
    IAuditLog audit,
    IOptions<EnrolmentOptions> options,
    AgentLinkTimings timings,
    TimeProvider time,
    ILogger<Enrolment> logger) : IEnrolmentAdmin
{
    public const int MinPasswordLength = 8;

    private readonly ConcurrentDictionary<IPAddress, Queue<DateTime>> _failures = new();

    internal async Task<(bool Require, string? Hash, bool RequireFromDb, bool HashFromDb)> GetEffectiveAsync(CancellationToken ct)
    {
        var requireText = await settings.GetAsync(SettingKeys.EnrolmentRequirePassword, ct);
        var requireFromDb = bool.TryParse(requireText, out var requireDb);
        var dbHash = await settings.GetAsync(SettingKeys.EnrolmentPasswordHash, ct);
        var hashFromDb = !string.IsNullOrWhiteSpace(dbHash);
        var hash = hashFromDb ? dbHash : options.Value.PasswordHash;
        return (requireFromDb ? requireDb : options.Value.RequirePassword,
            string.IsNullOrWhiteSpace(hash) ? null : hash.Trim(), requireFromDb, hashFromDb);
    }

    /// <summary>Checks an Enroll from an unknown key. The rate limit is checked before any hashing.</summary>
    public async Task<EnrolDecision> CheckAsync(IPAddress remote, string? password, CancellationToken ct)
    {
        if (IsRateLimited(remote)) return new EnrolDecision(EnrolOutcome.RateLimited, "Too many failed attempts.");
        var effective = await GetEffectiveAsync(ct);
        if (!effective.Require) return new EnrolDecision(EnrolOutcome.Accepted);
        if (effective.Hash is null || !PasswordHasher.LooksLikeHash(effective.Hash))
        {
            logger.LogWarning("Enrolment from {Remote} refused: an install password is required but none is configured", remote);
            return new EnrolDecision(EnrolOutcome.Rejected, "The server has no install password configured.");
        }
        if (!string.IsNullOrEmpty(password) && PasswordHasher.Verify(password, effective.Hash))
            return new EnrolDecision(EnrolOutcome.Accepted);
        RecordFailure(remote);
        logger.LogWarning("Enrolment from {Remote} refused: wrong install password", remote);
        return new EnrolDecision(EnrolOutcome.Rejected, "Wrong install password.");
    }

    private bool IsRateLimited(IPAddress remote)
    {
        if (!_failures.TryGetValue(remote, out var queue)) return false;
        lock (queue)
        {
            Prune(queue);
            return queue.Count >= timings.EnrolMaxFailures;
        }
    }

    private void RecordFailure(IPAddress remote)
    {
        var queue = _failures.GetOrAdd(remote, _ => new Queue<DateTime>());
        lock (queue)
        {
            Prune(queue);
            queue.Enqueue(time.GetUtcNow().UtcDateTime);
        }
        if (_failures.Count > 10_000)
        {
            foreach (var (ip, q) in _failures)
                lock (q)
                {
                    Prune(q);
                    if (q.Count == 0) _failures.TryRemove(ip, out _);
                }
        }
    }

    private void Prune(Queue<DateTime> queue)
    {
        var cutoff = time.GetUtcNow().UtcDateTime - timings.EnrolFailureWindow;
        while (queue.Count > 0 && queue.Peek() < cutoff) queue.Dequeue();
    }

    // ---------------------------------------------------------------- IEnrolmentAdmin

    public async Task<EnrolmentState> GetStateAsync(CancellationToken ct = default)
    {
        var e = await GetEffectiveAsync(ct);
        return new EnrolmentState(e.Require, e.Hash is not null && PasswordHasher.LooksLikeHash(e.Hash), e.RequireFromDb, e.HashFromDb);
    }

    public async Task<OperationResult> SetPasswordAsync(string newPassword, string actor, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(newPassword) || newPassword.Length < MinPasswordLength)
            return OperationResult.Fail($"The install password must have at least {MinPasswordLength} characters.");
        await settings.SetAsync(SettingKeys.EnrolmentPasswordHash, PasswordHasher.Hash(newPassword), actor, ct);
        await audit.WriteAsync(actor, AuditActions.InstallPasswordChanged, "Enrolment", null, null, ct);
        return OperationResult.Success;
    }

    public async Task<OperationResult> SetRequirePasswordAsync(bool required, string actor, CancellationToken ct = default)
    {
        await settings.SetAsync(SettingKeys.EnrolmentRequirePassword, required ? "true" : "false", actor, ct);
        await audit.WriteAsync(actor, AuditActions.InstallPasswordRequirementChanged, "Enrolment", null,
            required ? "required" : "not required", ct);
        return OperationResult.Success;
    }

    public async Task<AwaitingApprovalPolicy> GetAwaitingApprovalPolicyAsync(CancellationToken ct = default) =>
        Enum.TryParse<AwaitingApprovalPolicy>(await settings.GetAsync(SettingKeys.AwaitingApprovalReceives, ct), true, out var p)
        && Enum.IsDefined(p)
            ? p
            : AwaitingApprovalPolicy.EveryoneAndUser;

    public async Task SetAwaitingApprovalPolicyAsync(AwaitingApprovalPolicy policy, string actor, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(policy)) throw new ArgumentOutOfRangeException(nameof(policy));
        await settings.SetAsync(SettingKeys.AwaitingApprovalReceives, policy.ToString(), actor, ct);
        await audit.WriteAsync(actor, AuditActions.SettingChanged, "Setting", SettingKeys.AwaitingApprovalReceives, policy.ToString(), ct);
    }
}
