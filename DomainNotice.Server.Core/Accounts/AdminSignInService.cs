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
/// rate-limited per account and per IP; unknown local users cost one PBKDF2 like known ones.
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
        if (limiter.IsBlocked(key, remoteIp))
        {
            await AuditFailureAsync(typed, "local", "rate limited", remoteIp, ct);
            return Fail(SignInSource.Local, TooManyAttempts);
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var account = normalized.Length == 0 ? null
            : await db.LocalAccounts.FirstOrDefaultAsync(a => a.NormalizedUserName == normalized, ct);
        var ok = PasswordHasher.Verify(password ?? "", account?.PasswordHash ?? DummyHash.Value);
        if (account is null || !ok || account.Disabled)
        {
            limiter.RecordFailure(key, remoteIp);
            var reason = account is null ? "unknown user" : !ok ? "wrong password" : "account disabled";
            await AuditFailureAsync(typed, "local", reason, remoteIp, ct);
            return Fail(SignInSource.Local, GenericFailure);
        }

        limiter.ResetAccount(key);
        account.LastSignInAtUtc = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(account.UserName, AuditActions.SignIn, "LocalAccount", account.Id.ToString(), Details("local", null, remoteIp), ct);
        return new SignInOutcome(true, account.UserName, account.UserName, ConsoleRoles.Admin, SignInSource.Local);
    }

    public async Task<SignInOutcome> SignInDomainAsync(string userName, string password, string? remoteIp, CancellationToken ct = default)
    {
        var typed = (userName ?? "").Trim();
        if (!domain.IsAvailable)
            return Fail(SignInSource.Domain, "Domain sign-in is not available. Use a local account.");
        var key = SignInRateLimiter.AccountKey("domain", BindNames.RateLimitKey(typed));
        if (limiter.IsBlocked(key, remoteIp))
        {
            await AuditFailureAsync(typed, "domain", "rate limited", remoteIp, ct);
            return Fail(SignInSource.Domain, TooManyAttempts);
        }

        DomainAuthResult result;
        try
        {
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
                limiter.ResetAccount(key);
                await audit.WriteAsync(result.UserName ?? typed, AuditActions.SignIn, "DomainAccount", result.UserName ?? typed,
                    Details("domain", result.Role, remoteIp), ct);
                return new SignInOutcome(true, result.UserName ?? typed, result.DisplayName ?? result.UserName ?? typed,
                    result.Role, SignInSource.Domain);
            case DomainAuthStatus.Unavailable or DomainAuthStatus.NotConfigured:
                // Not the user's fault: not counted against the limits.
                await AuditFailureAsync(typed, "domain", "directory unavailable: " + result.Error, remoteIp, ct);
                return Fail(SignInSource.Domain, "The domain could not be reached. Try again, or use a local account.");
            case DomainAuthStatus.NotAuthorized:
                limiter.RecordFailure(key, remoteIp);
                await AuditFailureAsync(result.UserName ?? typed, "domain", "not in an admin or viewer group", remoteIp, ct);
                return Fail(SignInSource.Domain, "Your account is not allowed to use the DomainNotice Console.");
            default:
                limiter.RecordFailure(key, remoteIp);
                await AuditFailureAsync(typed, "domain", "invalid credentials", remoteIp, ct);
                return Fail(SignInSource.Domain, GenericFailure);
        }
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
