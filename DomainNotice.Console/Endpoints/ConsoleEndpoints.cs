using DomainNotice.Console.Auth;
using DomainNotice.Console.Grid;
using DomainNotice.Console.Lists;
using DomainNotice.Server.Abstractions;
using DomainNotice.Server.Data;
using DomainNotice.Shared;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DomainNotice.Console.Endpoints;

/// <summary>
/// Plain HTTP endpoints for what interactive Blazor pages cannot do: setting and clearing the sign-in cookie,
/// file downloads and the MSI upload. Form posts validate the antiforgery token explicitly.
/// </summary>
internal static class ConsoleEndpoints
{
    /// <summary>P13 upload limit. Agent MSIs are a few MB; this leaves plenty of room.</summary>
    public const long MaxMsiBytes = 256L * 1024 * 1024;

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
        app.MapPost("/settings/agent-package", UploadMsiAsync)
            .RequireAuthorization(ConsoleAuth.AdminPolicy)
            .WithMetadata(new RequestSizeLimitAttribute(MaxMsiBytes + 1024 * 1024),
                new RequestFormLimitsAttribute { MultipartBodyLengthLimit = MaxMsiBytes });
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
        return Results.Redirect(returnUrl);
    }

    private static async Task<IResult> LogoutAsync(HttpContext http, IAntiforgery antiforgery)
    {
        if (!await antiforgery.IsRequestValidAsync(http)) return Results.BadRequest();
        await http.SignOutAsync(ConsoleAuth.Scheme);
        return Results.Redirect(ConsoleAuth.LoginPath);
    }

    private static async Task<IResult> SetupAsync(HttpContext http, IAntiforgery antiforgery, IAdminSignInService signIn,
        ILocalAccountService accounts)
    {
        if (!await signIn.IsFirstRunSetupAllowedAsync(http.RequestAborted)) return Results.NotFound();
        if (!await antiforgery.IsRequestValidAsync(http)) return Results.BadRequest();
        var form = await http.Request.ReadFormAsync(http.RequestAborted);
        var userName = form["username"].ToString().Trim();
        var password = form["password"].ToString();
        if (password != form["confirm"].ToString())
            return Results.Redirect("/account/setup?error=" + Uri.EscapeDataString("The passwords do not match."));
        var result = await accounts.CreateFirstAsync(userName, password, http.RequestAborted);
        if (!result.Ok)
            return Results.Redirect("/account/setup?error=" + Uri.EscapeDataString(result.Error ?? "The account could not be created."));
        return Results.Redirect(ConsoleAuth.LoginPath + "?created=1&source=local");
    }

    private static async Task<IResult> ExportGridAsync(string list, HttpContext http, IDbContextFactory<AppDbContext> dbFactory,
        TimeProvider time, IServiceProvider services, CancellationToken ct)
    {
        if (!ConsoleLists.All.TryGetValue(list, out var grid)) return Results.NotFound();
        var query = GridQuery.Parse(http.Request.Query);
        var scope = grid.ScopeKeys.ToDictionary(k => k, k => (string?)http.Request.Query[k].ToString());
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var bytes = await grid.ExportCsvAsync(new GridContext(db, scope, time.GetUtcNow().UtcDateTime, services), query, ct);
        var stamp = time.GetUtcNow().UtcDateTime.ToString("yyyyMMdd-HHmm");
        return Results.File(bytes, "text/csv; charset=utf-8", $"{grid.Name}-{stamp}.csv");
    }

    private static async Task<IResult> DownloadMsiAsync(IAgentPackageCache cache, CancellationToken ct)
    {
        var package = await cache.GetCurrentAsync(ct);
        if (package is null) return Results.NotFound("No agent package is cached for this server version.");
        return Results.Stream(cache.OpenRead(package), "application/x-msi", $"DomainNotice.Agent-{package.Version}-x64.msi");
    }

    private static async Task<IResult> UploadMsiAsync(HttpContext http, IAntiforgery antiforgery, IAgentPackageCache cache)
    {
        if (!await antiforgery.IsRequestValidAsync(http)) return Results.BadRequest();
        string Back(string message, bool ok) =>
            $"/settings?tab=agent&{(ok ? "imported" : "importError")}={Uri.EscapeDataString(message)}";
        IFormCollection form;
        try
        {
            form = await http.Request.ReadFormAsync(http.RequestAborted);
        }
        catch (Exception ex) when (ex is InvalidDataException or BadHttpRequestException)
        {
            return Results.Redirect(Back($"The file is larger than {MaxMsiBytes / (1024 * 1024)} MB.", false));
        }
        var file = form.Files["file"];
        if (file is null || file.Length == 0) return Results.Redirect(Back("Choose an MSI file.", false));
        if (!file.FileName.EndsWith(".msi", StringComparison.OrdinalIgnoreCase)) return Results.Redirect(Back("Only .msi files can be imported.", false));
        if (file.Length > MaxMsiBytes) return Results.Redirect(Back("The file is too large.", false));

        var actor = http.User.Identity?.Name ?? "unknown";
        await using var stream = file.OpenReadStream();
        var result = await cache.ImportAsync(stream, Path.GetFileName(file.FileName), actor, http.RequestAborted);
        return Results.Redirect(result.Ok
            ? Back($"Imported {Path.GetFileName(file.FileName)} for version {AppVersion.Current}.", true)
            : Back(result.Error ?? "The package could not be imported.", false));
    }

    /// <summary>Only local paths ("/x", not "//host" or "/\host").</summary>
    public static string SafeReturnUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "/";
        if (!url.StartsWith('/') || url.StartsWith("//") || url.StartsWith("/\\")) return "/";
        if (url.StartsWith("/account/", StringComparison.OrdinalIgnoreCase)) return "/";
        return url;
    }
}
