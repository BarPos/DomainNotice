using System.Globalization;
using System.Security.Claims;
using DomainNotice.Console.Services;
using DomainNotice.Server.Abstractions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MudBlazor;

namespace DomainNotice.Console.Auth;

public static class ConsoleAuth
{
    /// <summary>Cookie scheme and cookie name.</summary>
    public const string Scheme = "DomainNotice.Console";
    public const string AdminPolicy = "Admin";
    /// <summary>Admin or Viewer (P4 read-only).</summary>
    public const string ViewerPolicy = "Viewer";
    public const string SourceClaim = "domainnotice:source";
    public const string DisplayNameClaim = "domainnotice:display-name";
    /// <summary>Sign-in time, Unix seconds (UTC). The absolute session lifetime counts from it.</summary>
    public const string IssuedAtClaim = "domainnotice:issued-at";
    /// <summary>Domain: objectGUID of the user ("D" format).</summary>
    public const string ObjectGuidClaim = "domainnotice:object-guid";
    /// <summary>Local: stamp of the password the session signed in with.</summary>
    public const string PasswordStampClaim = "domainnotice:password-stamp";
    public const string LoginPath = "/account/login";

    public static ClaimsPrincipal CreatePrincipal(SignInOutcome outcome)
    {
        var role = outcome.Role is ConsoleRoles.Admin or ConsoleRoles.Viewer ? outcome.Role : ConsoleRoles.Viewer;
        var issued = outcome.IssuedAtUtc ?? DateTime.UtcNow;
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, outcome.UserName ?? ""),
            new(ClaimTypes.Role, role),
            new(SourceClaim, outcome.Source.ToString()),
            new(IssuedAtClaim, new DateTimeOffset(DateTime.SpecifyKind(issued, DateTimeKind.Utc)).ToUnixTimeSeconds()
                .ToString(CultureInfo.InvariantCulture)),
        };
        if (!string.IsNullOrWhiteSpace(outcome.DisplayName)) claims.Add(new Claim(DisplayNameClaim, outcome.DisplayName));
        if (outcome.DirectoryObjectGuid is { } guid) claims.Add(new Claim(ObjectGuidClaim, guid.ToString("D")));
        if (!string.IsNullOrEmpty(outcome.PasswordStamp)) claims.Add(new Claim(PasswordStampClaim, outcome.PasswordStamp));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme, ClaimTypes.Name, ClaimTypes.Role));
    }

    public static bool IsAdmin(this ClaimsPrincipal user) => user.Identity?.IsAuthenticated == true && user.IsInRole(ConsoleRoles.Admin);

    public static bool IsConsoleUser(this ClaimsPrincipal user) =>
        user.Identity?.IsAuthenticated == true && (user.IsInRole(ConsoleRoles.Admin) || user.IsInRole(ConsoleRoles.Viewer));

    /// <summary>The session a principal claims, or null when it is not a complete Console principal.</summary>
    public static ConsoleSession? SessionOf(ClaimsPrincipal user)
    {
        if (user.Identity?.IsAuthenticated != true) return null;
        if (!Enum.TryParse<SignInSource>(user.FindFirst(SourceClaim)?.Value, out var source) || !Enum.IsDefined(source)) return null;
        var role = user.FindFirst(ClaimTypes.Role)?.Value;
        if (role is not (ConsoleRoles.Admin or ConsoleRoles.Viewer)) return null;
        DateTime? issued = long.TryParse(user.FindFirst(IssuedAtClaim)?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s)
            ? DateTimeOffset.FromUnixTimeSeconds(s).UtcDateTime
            : null;
        Guid? guid = Guid.TryParse(user.FindFirst(ObjectGuidClaim)?.Value, out var g) ? g : null;
        return new ConsoleSession(user.Identity.Name ?? "", source, role, issued, guid, user.FindFirst(PasswordStampClaim)?.Value);
    }

    /// <summary>
    /// Whether a signed-in principal is still valid (absolute lifetime, local account and password unchanged, domain
    /// user still enabled and in a group of the same role): see <see cref="IConsoleSessionValidator"/>. Principals
    /// without the claims of this version (older cookies) are not valid.
    /// </summary>
    public static Task<bool> IsStillValidAsync(ClaimsPrincipal user, IConsoleSessionValidator validator, CancellationToken ct) =>
        SessionOf(user) is { } session ? validator.IsValidAsync(session, ct) : Task.FromResult(false);

    internal static async Task ValidatePrincipalAsync(CookieValidatePrincipalContext context)
    {
        if (context.Principal is null) return;
        var validator = context.HttpContext.RequestServices.GetRequiredService<IConsoleSessionValidator>();
        bool valid;
        try
        {
            valid = await IsStillValidAsync(context.Principal, validator, context.HttpContext.RequestAborted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The validator handles database problems itself; anything else should not lock everyone out.
            context.HttpContext.RequestServices.GetRequiredService<ILogger<ConsoleUser>>()
                .LogWarning(ex, "Could not re-check the session of {User}", context.Principal.Identity?.Name);
            return;
        }
        if (!valid)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(Scheme);
        }
    }
}

/// <summary>Re-checks the session of open circuits every minute (same rules as each request).</summary>
internal sealed class ConsoleRevalidatingAuthStateProvider(ILoggerFactory loggerFactory, IConsoleSessionValidator validator,
    ILogger<ConsoleRevalidatingAuthStateProvider> logger)
    : RevalidatingServerAuthenticationStateProvider(loggerFactory)
{
    protected override TimeSpan RevalidationInterval => TimeSpan.FromMinutes(1);

    protected override async Task<bool> ValidateAuthenticationStateAsync(AuthenticationState authenticationState, CancellationToken cancellationToken)
    {
        try
        {
            return await ConsoleAuth.IsStillValidAsync(authenticationState.User, validator, cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // An exception would end the revalidation loop for this circuit for good; keep it and check again later.
            logger.LogWarning(e, "Could not re-check a Console circuit.");
            return true;
        }
    }
}

/// <summary>
/// The signed-in Console user for components. Every mutating handler calls <see cref="RequireAdminAsync"/> first:
/// hidden buttons are not enforcement (P4).
/// </summary>
public sealed class ConsoleUser(AuthenticationStateProvider authState, ISnackbar snackbar, Texts T)
{
    public async Task<ClaimsPrincipal> PrincipalAsync() => (await authState.GetAuthenticationStateAsync()).User;

    public async Task<string> ActorAsync() => (await PrincipalAsync()).Identity?.Name ?? "unknown";

    public async Task<bool> IsAdminAsync() => (await PrincipalAsync()).IsAdmin();

    /// <summary>Returns the actor (user name) when the user is an Admin; otherwise shows a message and returns null.</summary>
    public async Task<string?> RequireAdminAsync()
    {
        var user = await PrincipalAsync();
        if (user.IsAdmin()) return user.Identity!.Name ?? "unknown";
        snackbar.Add(T["Read-only access: this action needs the Admin role."], Severity.Warning);
        return null;
    }
}
