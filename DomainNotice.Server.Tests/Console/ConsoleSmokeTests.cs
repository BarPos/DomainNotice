using System.Net;
using System.Text.RegularExpressions;
using DomainNotice.Console.Endpoints;
using DomainNotice.Server.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using DomainNotice.Server.Abstractions;
using DomainNotice.Server.Common;
using DomainNotice.Server.Data;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DomainNotice.Server.Tests.Console;

/// <summary>
/// Runs the real host (Program) in Development with every real area registered; the schema comes from the
/// initial migration (Program runs MigrateAsync). Checks sign-in, the auth gate and that every page prerenders.
/// </summary>
public partial class ConsoleSmokeTests(PostgresFixture pg)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [GeneratedRegex("name=\"__RequestVerificationToken\" value=\"([^\"]+)\"")]
    private static partial Regex TokenRegex();

    private sealed class Factory(string connectionString, string dataDir) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:Default", connectionString);
            builder.UseSetting("Paths:Data", dataDir);
            builder.UseSetting("Agents:ListenAddress", "127.0.0.1");
            builder.UseSetting("Agents:Port", "0");
            // Test-owned sign-in, so the P4 (Viewer) checks do not depend on which IAdminSignInService is registered.
            builder.ConfigureTestServices(s => s.Replace(ServiceDescriptor.Singleton<IAdminSignInService, TestSignInService>()));
        }
    }

    /// <summary>Local accounts from the database; names starting with "viewer" get the Viewer role.</summary>
    private sealed class TestSignInService(IDbContextFactory<AppDbContext> dbFactory) : IAdminSignInService
    {
        public bool DomainSignInAvailable => false;
        public bool LocalSignInEnabled => true;

        public Task<SignInOutcome> SignInDomainAsync(string userName, string password, string? remoteIp, CancellationToken ct = default) =>
            Task.FromResult(new SignInOutcome(false));

        public async Task<SignInOutcome> SignInLocalAsync(string userName, string password, string? remoteIp, CancellationToken ct = default)
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var normalized = userName.Trim().ToLowerInvariant();
            var account = await db.LocalAccounts.FirstOrDefaultAsync(a => a.NormalizedUserName == normalized, ct);
            if (account is null || account.Disabled || !PasswordHasher.Verify(password, account.PasswordHash)) return new SignInOutcome(false);
            return new SignInOutcome(true, account.UserName, account.UserName,
                normalized.StartsWith("viewer", StringComparison.Ordinal) ? ConsoleRoles.Viewer : ConsoleRoles.Admin, SignInSource.Local);
        }

        public async Task<bool> IsFirstRunSetupAllowedAsync(CancellationToken ct = default)
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            return !await db.LocalAccounts.AnyAsync(ct);
        }
    }

    [Fact]
    public async Task Sign_in_gate_and_every_page_renders()
    {
        var db = await pg.CreateDatabaseAsync(createSchema: false);
        var dataDir = Path.Combine(Path.GetTempPath(), "dn-console-" + Guid.NewGuid().ToString("N"));
        await using var factory = new Factory(db.ConnectionString, dataDir);
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost"), HandleCookies = true,
        });

        // Anonymous: pages redirect to sign-in, the sign-in page and static assets are reachable.
        var root = await client.GetAsync("/", Ct);
        Assert.Equal(HttpStatusCode.Redirect, root.StatusCode);
        Assert.StartsWith("/account/login", root.Headers.Location!.PathAndQuery);
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/export/computers.csv", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/_framework/blazor.web.js", Ct)).StatusCode);
        Assert.NotEqual(HttpStatusCode.Redirect, (await client.GetAsync("/healthz", Ct)).StatusCode);

        // First-run setup is open (no directory, no account), then closed.
        var setupPage = await client.GetStringAsync("/account/setup", Ct);
        var created = await client.PostAsync("/account/setup", Form(setupPage, ("username", "admin"), ("password", "Passw0rd!x"), ("confirm", "Passw0rd!x")), Ct);
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/account/setup", Ct)).StatusCode);

        // Wrong password and a missing antiforgery token are refused; the error never says which part was wrong.
        var loginPage = await client.GetStringAsync("/account/login", Ct);
        var bad = await client.PostAsync("/account/login", Form(loginPage, ("source", "local"), ("username", "admin"), ("password", "nope")), Ct);
        Assert.Contains("error=1", bad.Headers.Location!.ToString());
        var noToken = await client.PostAsync("/account/login",
            new FormUrlEncodedContent([new("source", "local"), new("username", "admin"), new("password", "Passw0rd!x")]), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, noToken.StatusCode);

        var ok = await client.PostAsync("/account/login", Form(loginPage, ("source", "local"), ("username", "admin"),
            ("password", "Passw0rd!x"), ("returnUrl", "//evil.example/")), Ct);
        Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);
        Assert.Equal("/", ok.Headers.Location!.ToString());
        var cookie = string.Join(";", ok.Headers.GetValues("Set-Cookie"));
        Assert.Contains("DomainNotice.Console=", cookie);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);

        foreach (var path in new[]
                 {
                     "/", "/computers", "/computers?node=awaiting&f=name:contains:pc", "/computers/awaiting", $"/computers/{Guid.NewGuid()}",
                     "/announcements", "/announcements/new", $"/announcements/{Guid.NewGuid()}", "/confirmations", "/answers?tab=1",
                     "/history", "/agent-updates", "/directory", "/settings", "/settings?tab=about", "/audit", "/search?q=pc",
                 })
        {
            var response = await client.GetAsync(path, Ct);
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{path}: {(int)response.StatusCode}");
            var html = await response.Content.ReadAsStringAsync(Ct);
            Assert.Contains("DomainNotice", html);
            Assert.Contains("Audit log", html); // the navigation rendered
        }

        foreach (var listName in DomainNotice.Console.Lists.ConsoleLists.All.Keys)
        {
            var csv = await client.GetAsync($"/export/{listName}.csv?f=name:contains:x&sort=-name", Ct);
            Assert.True(csv.StatusCode == HttpStatusCode.OK, $"{listName}: {(int)csv.StatusCode}");
            Assert.Equal("text/csv", csv.Content.Headers.ContentType?.MediaType);
        }
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/export/nope.csv", Ct)).StatusCode);

        var logoutPage = await client.GetStringAsync("/", Ct);
        var logout = await client.PostAsync("/account/logout", Form(logoutPage), Ct);
        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/settings", Ct)).StatusCode);

        // P4: a Viewer sees no mutating actions, and the server refuses them.
        var accounts = factory.Services.GetRequiredService<ILocalAccountService>();
        Assert.True((await accounts.CreateAsync("viewer1", "Passw0rd!y", "test", Ct)).Ok); // TestSignInService: "viewer*" = Viewer
        var viewerLogin = await client.GetStringAsync("/account/login", Ct);
        var signedIn = await client.PostAsync("/account/login", Form(viewerLogin, ("source", "local"), ("username", "viewer1"), ("password", "Passw0rd!y")), Ct);
        Assert.Equal("/", signedIn.Headers.Location!.ToString());
        var list = await client.GetStringAsync("/announcements", Ct);
        Assert.Contains("Read-only", list);
        Assert.DoesNotContain("New announcement", list);
        var settings = await client.GetStringAsync("/settings?tab=agent", Ct);
        Assert.DoesNotContain("/settings/agent-package", settings);
        using var upload = new MultipartFormDataContent { { new StringContent(TokenRegex().Match(settings).Groups[1].Value), "__RequestVerificationToken" } };
        var refused = await client.PostAsync("/settings/agent-package", upload, Ct);
        Assert.True(refused.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Redirect, $"{(int)refused.StatusCode}");
        if (refused.StatusCode == HttpStatusCode.Redirect) Assert.StartsWith("/account/denied", refused.Headers.Location!.PathAndQuery);

        try { System.IO.Directory.Delete(dataDir, true); } catch (IOException) { }
    }

    [Theory]
    [InlineData(null, "/")]
    [InlineData("/computers?node=revoked", "/computers?node=revoked")]
    [InlineData("//evil.example", "/")]
    [InlineData("/\\evil.example", "/")]
    [InlineData("https://evil.example/", "/")]
    [InlineData("/account/logout", "/")]
    public void Return_url_must_be_local(string? input, string expected) =>
        Assert.Equal(expected, ConsoleEndpoints.SafeReturnUrl(input));

    private static FormUrlEncodedContent Form(string html, params (string Key, string Value)[] fields)
    {
        var token = TokenRegex().Match(html).Groups[1].Value;
        Assert.False(string.IsNullOrEmpty(token), "No antiforgery token in the page.");
        return new FormUrlEncodedContent([new("__RequestVerificationToken", WebUtility.HtmlDecode(token)),
            .. fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value))]);
    }
}
