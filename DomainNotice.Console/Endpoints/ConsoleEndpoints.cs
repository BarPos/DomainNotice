using DomainNotice.Console.Auth;
using DomainNotice.Console.Grid;
using DomainNotice.Console.Lists;
using DomainNotice.Console.Services;
using DomainNotice.Server.Abstractions;
using DomainNotice.Server.Common;
using DomainNotice.Server.Data;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DomainNotice.Console.Endpoints;

/// <summary>
/// Plain HTTP endpoints for what interactive Blazor pages cannot do: setting and clearing the sign-in cookie,
/// file downloads. Form posts validate the antiforgery token explicitly. There is no upload endpoint: the agent MSI
/// only ever comes from the release download (verified against SHA256SUMS.txt) into the server's package cache.
/// </summary>
internal static class ConsoleEndpoints
{
    public const string GenericSignInError = "Sign-in failed. Check the account type, user name and password.";

    public static void MapConsoleEndpoints(this IEndpointRouteBuilder app)
    {
        var account = app.MapGroup("/account").AllowAnonymous();
        // Order -1: the page components at the same paths also accept POST (Blazor form handling); these win.
        account.MapPost("/login", LoginAsync).WithOrder(-1);
        account.MapPost("/setup", SetupAsync).WithOrder(-1);
        app.MapPost("/account/logout", LogoutAsync).RequireAuthorization().WithOrder(-1);

        app.MapGet("/export/announcements/{id:guid}/confirmations.csv", async (Guid id, IResultsExport export, CancellationToken ct) =>
                Results.File(await export.ExportConfirmationsCsvAsync(id, ct), "text/csv; charset=utf-8", $"confirmations-{id:N}.csv"))
            .RequireAuthorization(ConsoleAuth.ViewerPolicy);
        app.MapGet("/export/announcements/{id:guid}/answers.csv", async (Guid id, IResultsExport export, CancellationToken ct) =>
                Results.File(await export.ExportAnswersCsvAsync(id, ct), "text/csv; charset=utf-8", $"answers-{id:N}.csv"))
            .RequireAuthorization(ConsoleAuth.ViewerPolicy);
        app.MapGet("/export/{list}.csv", ExportGridAsync).RequireAuthorization(ConsoleAuth.ViewerPolicy);

        app.MapGet("/download/agent-msi", DownloadMsiAsync).RequireAuthorization(ConsoleAuth.ViewerPolicy);
    }

    private static async Task<IResult> LoginAsync(HttpContext http, IAntiforgery antiforgery, IAdminSignInService signIn,
        ILogger<ConsoleUser> logger)
    {
        if (!await antiforgery.IsRequestValidAsync(http)) return Results.BadRequest();
        var form = await http.Request.ReadFormAsync(http.RequestAborted);
        var source = form["source"].ToString();
        var userName = form["username"].ToString().Trim();
        var password = form["password"].ToString();
        var returnUrl = SafeReturnUrl(form["returnUrl"].ToString());
        var remoteIp = http.Connection.RemoteIpAddress?.ToString();

        string Back(string kind) =>
            $"{ConsoleAuth.LoginPath}?error=1&source={kind}&returnUrl={Uri.EscapeDataString(returnUrl)}";

        var useDomain = source == "domain";
        if (userName.Length == 0 || password.Length == 0 || userName.Length > 256 || password.Length > 1024)
            return Results.Redirect(Back(useDomain ? "domain" : "local"));

        SignInOutcome outcome;
        try
        {
            if (useDomain)
                outcome = signIn.DomainSignInAvailable
                    ? await signIn.SignInDomainAsync(userName, password, remoteIp, http.RequestAborted)
                    : new SignInOutcome(false);
            else
                outcome = signIn.LocalSignInEnabled
                    ? await signIn.SignInLocalAsync(userName, password, remoteIp, http.RequestAborted)
                    : new SignInOutcome(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Sign-in failed with an error");
            outcome = new SignInOutcome(false);
        }

        // One generic message whatever the reason: no user enumeration.
        if (!outcome.Succeeded || string.IsNullOrWhiteSpace(outcome.UserName))
            return Results.Redirect(Back(useDomain ? "domain" : "local"));

        await http.SignInAsync(ConsoleAuth.Scheme, ConsoleAuth.CreatePrincipal(outcome),
            new AuthenticationProperties { IsPersistent = false, AllowRefresh = true });
        // LocalRedirect refuses anything that is not a local URL, a second check behind SafeReturnUrl.
        return Results.LocalRedirect(returnUrl);
    }

    private static async Task<IResult> LogoutAsync(HttpContext http, IAntiforgery antiforgery)
    {
        if (!await antiforgery.IsRequestValidAsync(http)) return Results.BadRequest();
        await http.SignOutAsync(ConsoleAuth.Scheme);
        return Results.Redirect(ConsoleAuth.LoginPath);
    }

    private static async Task<IResult> SetupAsync(HttpContext http, IAntiforgery antiforgery, IAdminSignInService signIn,
        ILocalAccountService accounts, Texts T)
    {
        if (!await signIn.IsFirstRunSetupAllowedAsync(http.RequestAborted)) return Results.NotFound();
        if (!await antiforgery.IsRequestValidAsync(http)) return Results.BadRequest();
        var form = await http.Request.ReadFormAsync(http.RequestAborted);
        var userName = form["username"].ToString().Trim();
        var password = form["password"].ToString();
        if (password != form["confirm"].ToString())
            return Results.Redirect("/account/setup?error=" + Uri.EscapeDataString(T["The passwords do not match."]));
        var result = await accounts.CreateFirstAsync(userName, password, http.RequestAborted);
        if (!result.Ok)
            return Results.Redirect("/account/setup?error=" + Uri.EscapeDataString(T.Maybe(result.Error) ?? T["The account could not be created."]));
        return Results.Redirect(ConsoleAuth.LoginPath + "?created=1&source=local");
    }

    private static async Task<IResult> ExportGridAsync(string list, HttpContext http, IDbContextFactory<AppDbContext> dbFactory,
        TimeProvider time, IServiceProvider services, Texts T, CancellationToken ct)
    {
        if (!ConsoleLists.All.TryGetValue(list, out var grid)) return Results.NotFound();
        var query = GridQuery.Parse(http.Request.Query);
        var scope = grid.ScopeKeys.ToDictionary(k => k, k => (string?)http.Request.Query[k].ToString());
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var bytes = await grid.ExportCsvAsync(new GridContext(db, scope, time.GetUtcNow().UtcDateTime, services), query,
            CsvStyleFor(T), ct);
        var stamp = time.GetUtcNow().UtcDateTime.ToString("yyyyMMdd-HHmm");
        return Results.File(bytes, "text/csv; charset=utf-8", $"{grid.Name}-{stamp}.csv");
    }

    /// <summary>
    /// Grid CSVs in the Console language: header labels translated before writing (list labels are English in
    /// <see cref="ConsoleLists"/>; data rows stay as they are) and the language's list separator.
    /// </summary>
    internal static CsvStyle CsvStyleFor(Texts T) => new(CsvFormat.SeparatorFor(T.Language), label => T[label]);

    private static async Task<IResult> DownloadMsiAsync(IAgentPackageCache cache, Texts T, CancellationToken ct)
    {
        var package = await cache.GetCurrentAsync(ct);
        if (package is null) return Results.NotFound(T["No agent package is cached for this server version."]);
        return Results.Stream(cache.OpenRead(package), "application/x-msi", $"DomainNotice.Agent-{package.Version}-x64.msi");
    }

    /// <summary>
    /// Only local paths: one leading '/', then not '/' ("//host"), no '\' anywhere ("/\host"; browsers read '\' as
    /// '/') and no control characters anywhere. Browsers drop tab, CR and LF inside a URL, so "/&lt;TAB&gt;/host" would
    /// become "//host"; Kestrel lets a tab through in a header value. Anything else, and the /account/ pages, give "/".
    /// Used for the hidden field of the sign-in page and for the redirect after sign-in.
    /// </summary>
    public static string SafeReturnUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "/";
        if (url[0] != '/' || (url.Length > 1 && url[1] == '/')) return "/";
        foreach (var c in url)
            if (c < 0x20 || c == 0x7F || c == '\\') return "/";
        if (url.StartsWith("/account/", StringComparison.OrdinalIgnoreCase)) return "/";
        return url;
    }
}
