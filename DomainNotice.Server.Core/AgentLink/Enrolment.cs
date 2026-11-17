using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using DomainNotice.Server.Abstractions;
using DomainNotice.Server.Common;
using DomainNotice.Server.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DomainNotice.Server.AgentLink;

/// <summary>
/// Busy: the attempt waited too long for a password check; the connection is closed without a refusal, so the
/// agent retries after its short back-off. Busy is not counted as a failure.
/// </summary>
internal enum EnrolOutcome { Accepted, Rejected, RateLimited, Busy }

internal sealed record EnrolDecision(EnrolOutcome Outcome, string? Error = null);

/// <summary>
/// Install password check (plan 4) and its Console settings. The database settings (set in the Console) win over
/// the Enrolment config section.
/// <para>Brute-force and CPU limits, in memory, per server instance:</para>
/// <list type="bullet">
/// <item>Per remote IP, recorded failures plus checks in progress never exceed <see cref="AgentLinkTimings.EnrolMaxFailures"/>
/// within <see cref="AgentLinkTimings.EnrolFailureWindow"/>. A slot is reserved before hashing, so parallel connections
/// get no extra guesses. Attempts beyond the free slots wait for one (they are not refused); only failures count.</item>
/// <item>At most <see cref="AgentLinkTimings.EnrolMaxParallelHashes"/> PBKDF2 checks run at once, server-wide. Others
/// queue for up to <see cref="AgentLinkTimings.EnrolQueueTimeout"/>.</item>
/// <item>Once a password has been verified against the current hash, later attempts are compared with its HMAC under
/// a random per-process key instead of PBKDF2 (still inside a reserved slot, so every guess counts). A mass rollout
/// (many PCs behind one NAT address enrolling at boot) is then not slowed down by the hashing limit.</item>
/// </list>
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

    private readonly ConcurrentDictionary<IPAddress, IpState> _ips = new();
    private readonly byte[] _macKey = RandomNumberGenerator.GetBytes(32);
    private SemaphoreSlim? _hashing;
    private volatile VerifiedPassword? _verified;
    private int _verifications;
    private int _comparisons;

    private sealed record VerifiedPassword(string Hash, byte[] Mac);

    private sealed class IpState
    {
        public readonly Queue<DateTime> Failures = new();
        public int InFlight;
        public bool Removed;
        public TaskCompletionSource Changed = NewSignal();

        public static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>PBKDF2 verifications started so far (tests).</summary>
    internal int VerificationsStarted => Volatile.Read(ref _verifications);

    /// <summary>Password comparisons made so far, PBKDF2 or remembered HMAC (tests).</summary>
    internal int ComparisonsMade => Volatile.Read(ref _comparisons);

    private SemaphoreSlim Hashing => LazyInitializer.EnsureInitialized(ref _hashing,
        () => new SemaphoreSlim(Math.Max(1, timings.EnrolMaxParallelHashes)));

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

    /// <summary>Checks an Enroll from an unknown key. The rate limit is checked, and a slot reserved, before any hashing.</summary>
    public async Task<EnrolDecision> CheckAsync(IPAddress remote, string? password, CancellationToken ct)
    {
        if (IsRateLimited(remote)) return RateLimited();
        var effective = await GetEffectiveAsync(ct);
        if (!effective.Require) return new EnrolDecision(EnrolOutcome.Accepted);
        if (effective.Hash is null || !PasswordHasher.LooksLikeHash(effective.Hash))
        {
            logger.LogWarning("Enrolment from {Remote} refused: an install password is required but none is configured", remote);
            return new EnrolDecision(EnrolOutcome.Rejected, "The server has no install password configured.");
        }
        var hash = effective.Hash;
        if (string.IsNullOrEmpty(password))
        {
            RecordFailure(remote);
            logger.LogWarning("Enrolment from {Remote} refused: no install password given", remote);
            return Wrong();
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timings.EnrolQueueTimeout);
        IpState? state;
        try
        {
            state = await ReserveAsync(remote, deadline.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Busy(remote);
        }
        if (state is null) return RateLimited();

        // Every comparison, even the fast one, happens while this IP's slot is held: no guess goes uncounted.
        bool? ok = null;
        try
        {
            ok = CompareRemembered(hash, password);
            if (ok is null)
            {
                try
                {
                    await Hashing.WaitAsync(deadline.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    return Busy(remote);
                }
                try
                {
                    // Another attempt may have verified the password while this one queued.
                    ok = CompareRemembered(hash, password);
                    if (ok is null)
                    {
                        Interlocked.Increment(ref _verifications);
                        ok = PasswordHasher.Verify(password, hash);
                        // Remembered before the slot is released, so attempts waiting for it take the fast path.
                        if (ok == true) _verified = new VerifiedPassword(hash, Mac(password));
                    }
                }
                finally
                {
                    Hashing.Release();
                }
            }
        }
        finally
        {
            if (ok is not null) Interlocked.Increment(ref _comparisons);
            ReleaseSlot(state, failed: ok == false);
        }
        if (ok == true) return new EnrolDecision(EnrolOutcome.Accepted);
        logger.LogWarning("Enrolment from {Remote} refused: wrong install password", remote);
        return Wrong();
    }

    private static EnrolDecision RateLimited() => new(EnrolOutcome.RateLimited, "Too many failed attempts.");
    private static EnrolDecision Wrong() => new(EnrolOutcome.Rejected, "Wrong install password.");

    private EnrolDecision Busy(IPAddress remote)
    {
        logger.LogWarning("Enrolment from {Remote} waited more than {Seconds} s for a password check; closed (the agent retries)",
            remote, (int)timings.EnrolQueueTimeout.TotalSeconds);
        return new EnrolDecision(EnrolOutcome.Busy);
    }

    /// <summary>
    /// Waits until failures + checks in progress for this IP are below the limit and takes a slot. Returns null when
    /// the IP is rate-limited (recorded failures alone reach the limit).
    /// </summary>
    private async Task<IpState?> ReserveAsync(IPAddress remote, CancellationToken ct)
    {
        while (true)
        {
            var state = _ips.GetOrAdd(remote, _ => new IpState());
            Task wait;
            lock (state)
            {
                if (state.Removed) continue;
                Prune(state.Failures);
                if (state.Failures.Count >= timings.EnrolMaxFailures) return null;
                if (state.Failures.Count + state.InFlight < timings.EnrolMaxFailures)
                {
                    state.InFlight++;
                    return state;
                }
                wait = state.Changed.Task;
            }
            // Old failures leave the window without any release: look again at least once a second.
            await Task.WhenAny(wait, Task.Delay(TimeSpan.FromSeconds(1), time, ct)).WaitAsync(ct);
        }
    }


    private void ReleaseSlot(IpState state, bool failed)
    {
        TaskCompletionSource signal;
        lock (state)
        {
            state.InFlight--;
            if (failed) state.Failures.Enqueue(time.GetUtcNow().UtcDateTime);
            signal = state.Changed;
            state.Changed = IpState.NewSignal();
        }
        signal.TrySetResult();
        if (failed) PruneStates();
    }

    private bool IsRateLimited(IPAddress remote)
    {
        if (!_ips.TryGetValue(remote, out var state)) return false;
        lock (state)
        {
            Prune(state.Failures);
            return state.Failures.Count >= timings.EnrolMaxFailures;
        }
    }

    private void RecordFailure(IPAddress remote)
    {
        while (true)
        {
            var state = _ips.GetOrAdd(remote, _ => new IpState());
            lock (state)
            {
                if (state.Removed) continue;
                Prune(state.Failures);
                state.Failures.Enqueue(time.GetUtcNow().UtcDateTime);
                break;
            }
        }
        PruneStates();
    }

    /// <summary>Drops idle entries when many IPs are tracked.</summary>
    private void PruneStates()
    {
        if (_ips.Count <= 10_000) return;
        foreach (var (ip, state) in _ips)
            lock (state)
            {
                Prune(state.Failures);
                if (state.Failures.Count != 0 || state.InFlight != 0) continue;
                state.Removed = true;
                _ips.TryRemove(new KeyValuePair<IPAddress, IpState>(ip, state));
            }
    }

    private void Prune(Queue<DateTime> queue)
    {
        var cutoff = time.GetUtcNow().UtcDateTime - timings.EnrolFailureWindow;
        while (queue.Count > 0 && queue.Peek() < cutoff) queue.Dequeue();
    }

    private byte[] Mac(string password) => HMACSHA256.HashData(_macKey, Encoding.UTF8.GetBytes(password));

    /// <summary>
    /// True/false when a password was already verified against this hash (a mismatch is then a wrong password, there
    /// is only one right one); null when nothing is remembered for this hash yet.
    /// </summary>
    private bool? CompareRemembered(string hash, string password) =>
        _verified is { } v && string.Equals(v.Hash, hash, StringComparison.Ordinal)
            ? CryptographicOperations.FixedTimeEquals(v.Mac, Mac(password))
            : null;

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
