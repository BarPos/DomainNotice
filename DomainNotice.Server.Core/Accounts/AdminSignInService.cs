using DomainNotice.Server.Abstractions;
using DomainNotice.Server.Common;
using DomainNotice.Server.Data;
using DomainNotice.Server.Directory;
using DomainNotice.Server.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DomainNotice.Server.Accounts;

/// <summary>
/// Console sign-in (plan 7 item 1, 7.1, P4). Domain: bind as the user, role from Directory:AdminGroups (Admin) or
/// ViewerGroups (Viewer), nested groups included. Local: PBKDF2 accounts with full admin rights. Both kinds are
/// rate-limited (<see cref="SignInRateLimiter"/>: an attempt is reserved before the password is checked) and share a
/// small limit on concurrent password checks; unknown local users cost one PBKDF2 like known ones. Every failure the
/// person can cause returns the same <see cref="GenericFailure"/>; the reason goes to the audit log only.
/// </summary>
internal sealed class AdminSignInService(
    IDbContextFactory<AppDbContext> dbFactory,
    IDomainAuthenticator domain,
    FirstRunGate firstRun,
    SignInRateLimiter limiter,
    IOptions<LocalAccountsOptions> localOptions,
    IAuditLog audit,
    TimeProvider time,
    ILogger<AdminSignInService> logger) : IAdminSignInService
{
    public const string GenericFailure = "Sign-in failed. Check the user name and password.";
    public const string TooManyAttempts = "Too many failed sign-ins. Wait a few minutes and try again.";

    // Verified against for unknown or disabled users so timing does not reveal which names exist.
    private static readonly Lazy<string> DummyHash = new(() => PasswordHasher.Hash(Guid.NewGuid().ToString("N")));

    public bool DomainSignInAvailable => domain.IsAvailable;

    /// <summary>
    /// LocalAccounts:Enabled, except that local sign-in always stays on while no directory is configured, since it
    /// is then the only way in.
    /// </summary>
    public bool LocalSignInEnabled => localOptions.Value.Enabled || !firstRun.DirectoryConfigured;

    public Task<bool> IsFirstRunSetupAllowedAsync(CancellationToken ct = default) => firstRun.IsAllowedAsync(ct);

    public async Task<SignInOutcome> SignInLocalAsync(string userName, string password, string? remoteIp, CancellationToken ct = default)
    {
        var typed = (userName ?? "").Trim();
        if (!LocalSignInEnabled)
            return Fail(SignInSource.Local, "Local sign-in is turned off (LocalAccounts:Enabled).");
        var normalized = LocalAccountService.Normalize(typed);
        var key = SignInRateLimiter.AccountKey("local", normalized);
        using var attempt = limiter.TryBegin(key, remoteIp, out var blockedBy);
        if (attempt is null) return await RateLimitedAsync(typed, "local", blockedBy, remoteIp, ct);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var account = normalized.Length == 0 ? null
            : await db.LocalAccounts.FirstOrDefaultAsync(a => a.NormalizedUserName == normalized, ct);
        bool ok;
        using (await limiter.EnterVerificationAsync(ct))
            ok = PasswordHasher.Verify(password ?? "", account?.PasswordHash ?? DummyHash.Value);
        if (account is null || !ok || account.Disabled)
        {
            attempt.Failed();
            var reason = account is null ? "unknown user" : !ok ? "wrong password" : "account disabled";
            await AuditFailureAsync(typed, "local", reason, remoteIp, ct);
            return Fail(SignInSource.Local, GenericFailure);
        }

        attempt.Succeeded();
        var now = time.GetUtcNow().UtcDateTime;
        account.LastSignInAtUtc = now;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(account.UserName, AuditActions.SignIn, "LocalAccount", account.Id.ToString(), Details("local", null, remoteIp), ct);
        return new SignInOutcome(true, account.UserName, account.UserName, ConsoleRoles.Admin, SignInSource.Local,
            IssuedAtUtc: now, PasswordStamp: LocalAccountService.PasswordStamp(account.PasswordHash));
    }

    public async Task<SignInOutcome> SignInDomainAsync(string userName, string password, string? remoteIp, CancellationToken ct = default)
    {
        var typed = (userName ?? "").Trim();
        if (!domain.IsAvailable)
            return Fail(SignInSource.Domain, "Domain sign-in is not available. Use a local account.");
        var key = SignInRateLimiter.AccountKey("domain", BindNames.RateLimitKey(typed));
        using var attempt = limiter.TryBegin(key, remoteIp, out var blockedBy);
        if (attempt is null) return await RateLimitedAsync(typed, "domain", blockedBy, remoteIp, ct);

        if (!BindNames.IsAcceptedSignInName(typed))
        {
            // DN-form names are refused without asking the directory (they would give fresh rate-limit keys).
            attempt.Failed();
            await AuditFailureAsync(typed, "domain", "sign-in name refused (DN form or invalid)", remoteIp, ct);
            return Fail(SignInSource.Domain, GenericFailure);
        }

        DomainAuthResult result;
        try
        {
            using (await limiter.EnterVerificationAsync(ct))
                result = await domain.AuthenticateAsync(typed, password ?? "", ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogError(e, "Domain sign-in failed unexpectedly for {User}.", typed);
            result = new DomainAuthResult(DomainAuthStatus.Unavailable, Error: "The directory could not be reached.");
        }

        switch (result.Status)
        {
            case DomainAuthStatus.Success:
                attempt.Succeeded();
                await audit.WriteAsync(result.UserName ?? typed, AuditActions.SignIn, "DomainAccount", result.UserName ?? typed,
                    Details("domain", result.Role, remoteIp), ct);
                return new SignInOutcome(true, result.UserName ?? typed, result.DisplayName ?? result.UserName ?? typed,
                    result.Role, SignInSource.Domain, IssuedAtUtc: time.GetUtcNow().UtcDateTime, DirectoryObjectGuid: result.ObjectGuid);
            case DomainAuthStatus.Unavailable or DomainAuthStatus.NotConfigured:
                // Not the user's fault: not counted against the limits (disposing the attempt releases it). Audited
                // once per client IP and window, so an outage cannot grow the audit log without limit either.
                if (limiter.ShouldAuditBlock("unavailable:" + (remoteIp ?? "")))
                    await AuditFailureAsync(typed, "domain", "directory unavailable: " + result.Error +
                                                             " (further failures from this IP in this window are not logged)", remoteIp, ct);
                else
                    logger.LogWarning("Domain sign-in for {User}: directory unavailable: {Error}", typed, result.Error);
                return Fail(SignInSource.Domain, "The domain could not be reached. Try again, or use a local account.");
            case DomainAuthStatus.NotAuthorized:
                // The password was right, but the same message as a wrong one: no password oracle.
                attempt.Failed();
                await AuditFailureAsync(typed, "domain",
                    "not authorized: " + (result.Error ?? "not in an admin or viewer group") +
                    (result.UserName is { } who && !string.Equals(who, typed, StringComparison.OrdinalIgnoreCase) ? $" (bound as {who})" : ""),
                    remoteIp, ct);
                return Fail(SignInSource.Domain, GenericFailure);
            default:
                attempt.Failed();
                await AuditFailureAsync(typed, "domain", "invalid credentials", remoteIp, ct);
                return Fail(SignInSource.Domain, GenericFailure);
        }
    }

    /// <summary>Refused by the rate limiter: one audit row per blocking counter and window, not one per request.</summary>
    private async Task<SignInOutcome> RateLimitedAsync(string typed, string kind, string? blockedBy, string? remoteIp, CancellationToken ct)
    {
        if (limiter.ShouldAuditBlock(blockedBy))
            await AuditFailureAsync(typed, kind, "rate limited (further attempts in this window are not logged)", remoteIp, ct);
        return Fail(kind == "local" ? SignInSource.Local : SignInSource.Domain, TooManyAttempts);
    }

    private static SignInOutcome Fail(SignInSource source, string error) => new(false, Source: source, Error: error);

    private static string Details(string kind, string? detail, string? ip) =>
        string.Join("; ", new[] { kind, detail, ip is null ? null : "ip " + ip }.Where(s => !string.IsNullOrEmpty(s)));

    private async Task AuditFailureAsync(string actor, string kind, string reason, string? ip, CancellationToken ct)
    {
        try
        {
            var a = string.IsNullOrEmpty(actor) ? "(empty)" : actor.Length > 255 ? actor[..255] : actor;
            await audit.WriteAsync(a, AuditActions.SignInFailed, kind == "local" ? "LocalAccount" : "DomainAccount", null,
                Details(kind, reason, ip), ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "Could not write the sign-in failure to the audit log.");
        }
    }
}
