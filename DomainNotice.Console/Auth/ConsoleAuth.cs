using System.Security.Claims;
using DomainNotice.Server.Abstractions;
using DomainNotice.Server.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
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
    public const string LoginPath = "/account/login";

    public static ClaimsPrincipal CreatePrincipal(SignInOutcome outcome)
    {
        var role = outcome.Role is ConsoleRoles.Admin or ConsoleRoles.Viewer ? outcome.Role : ConsoleRoles.Viewer;
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, outcome.UserName ?? ""),
            new(ClaimTypes.Role, role),
            new(SourceClaim, outcome.Source.ToString()),
        };
        if (!string.IsNullOrWhiteSpace(outcome.DisplayName)) claims.Add(new Claim(DisplayNameClaim, outcome.DisplayName));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme, ClaimTypes.Name, ClaimTypes.Role));
    }

    public static bool IsAdmin(this ClaimsPrincipal user) => user.Identity?.IsAuthenticated == true && user.IsInRole(ConsoleRoles.Admin);

    public static bool IsConsoleUser(this ClaimsPrincipal user) =>
        user.Identity?.IsAuthenticated == true && (user.IsInRole(ConsoleRoles.Admin) || user.IsInRole(ConsoleRoles.Viewer));

    /// <summary>
    /// Local accounts can be disabled or deleted while signed in; such a principal stops being valid. Domain accounts
    /// cannot be re-checked without an LDAP bind and stay valid until the cookie expires.
    /// </summary>
    public static async Task<bool> IsStillValidAsync(ClaimsPrincipal user, IDbContextFactory<AppDbContext> dbFactory, CancellationToken ct)
    {
        if (user.FindFirst(SourceClaim)?.Value != nameof(SignInSource.Local)) return true;
        var normalized = (user.Identity?.Name ?? "").Trim().ToLowerInvariant();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.LocalAccounts.AnyAsync(a => a.NormalizedUserName == normalized && !a.Disabled, ct);
    }

    internal static async Task ValidatePrincipalAsync(CookieValidatePrincipalContext context)
    {
        if (context.Principal is null) return;
        var dbFactory = context.HttpContext.RequestServices.GetRequiredService<IDbContextFactory<AppDbContext>>();
        try
        {
            if (!await IsStillValidAsync(context.Principal, dbFactory, context.HttpContext.RequestAborted))
            {
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync(Scheme);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Database down: do not lock everyone out of read-only pages; the next request checks again.
            context.HttpContext.RequestServices.GetRequiredService<ILogger<ConsoleUser>>()
                .LogWarning(ex, "Could not re-check the local account of {User}", context.Principal.Identity?.Name);
        }
    }
}

/// <summary>Re-checks local accounts of open circuits every few minutes (disabled accounts lose access).</summary>
internal sealed class ConsoleRevalidatingAuthStateProvider(ILoggerFactory loggerFactory, IDbContextFactory<AppDbContext> dbFactory)
    : RevalidatingServerAuthenticationStateProvider(loggerFactory)
{
    protected override TimeSpan RevalidationInterval => TimeSpan.FromMinutes(5);

    protected override Task<bool> ValidateAuthenticationStateAsync(AuthenticationState authenticationState, CancellationToken cancellationToken) =>
        ConsoleAuth.IsStillValidAsync(authenticationState.User, dbFactory, cancellationToken);
}

/// <summary>
/// The signed-in Console user for components. Every mutating handler calls <see cref="RequireAdminAsync"/> first:
/// hidden buttons are not enforcement (P4).
/// </summary>
public sealed class ConsoleUser(AuthenticationStateProvider authState, ISnackbar snackbar)
{
    public async Task<ClaimsPrincipal> PrincipalAsync() => (await authState.GetAuthenticationStateAsync()).User;

    public async Task<string> ActorAsync() => (await PrincipalAsync()).Identity?.Name ?? "unknown";

    public async Task<bool> IsAdminAsync() => (await PrincipalAsync()).IsAdmin();

    /// <summary>Returns the actor (user name) when the user is an Admin; otherwise shows a message and returns null.</summary>
    public async Task<string?> RequireAdminAsync()
    {
        var user = await PrincipalAsync();
        if (user.IsAdmin()) return user.Identity!.Name ?? "unknown";
        snackbar.Add("Read-only access: this action needs the Admin role.", Severity.Warning);
        return null;
    }
}
