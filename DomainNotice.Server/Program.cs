using System.Security.Cryptography.X509Certificates;
using DomainNotice.Console;
using DomainNotice.Server;
using DomainNotice.Server.Abstractions;
using DomainNotice.Server.Data;
using DomainNotice.Server.Options;
using DomainNotice.Shared;
using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;

// DomainNotice.Server — host process (plan 1, D1): Console (HTTPS), agent listener (TLS over TCP), background jobs.
//
// Command line:
//   --migrate-only                  apply pending EF Core migrations and exit (install.sh, update.sh)
//   --create-local-account <name>   create a local admin account; password read from stdin (first line), then exit
//   --print-fingerprint             print the agent-link key fingerprint and exit
//   --reset-local-password <name>   set a new password for a local account (and enable it); password read from stdin
//   --list-local-accounts           print the local accounts (name, enabled/disabled) and exit
//
// Configuration: appsettings.json, appsettings.{Environment}.json, then the file named by the
// DOMAINNOTICE_CONFIG environment variable (install.sh: /opt/domainnotice/config/appsettings.Production.json),
// then environment variables (DOMAINNOTICE_ prefix and standard ASP.NET Core ones).

var migrateOnly = args.Contains("--migrate-only");
var printFingerprint = args.Contains("--print-fingerprint");
var createAccountIndex = Array.IndexOf(args, "--create-local-account");
var createAccountName = createAccountIndex >= 0 && createAccountIndex + 1 < args.Length ? args[createAccountIndex + 1] : null;
if (createAccountIndex >= 0 && createAccountName is null)
{
    Console.Error.WriteLine("--create-local-account needs a user name.");
    return 2;
}
var resetIndex = Array.IndexOf(args, "--reset-local-password");
var resetAccountName = resetIndex >= 0 && resetIndex + 1 < args.Length ? args[resetIndex + 1] : null;
if (resetIndex >= 0 && resetAccountName is null)
{
    Console.Error.WriteLine("--reset-local-password needs a user name.");
    return 2;
}
var listAccounts = args.Contains("--list-local-accounts");
var cliOnly = migrateOnly || printFingerprint || createAccountName is not null || resetAccountName is not null || listAccounts;

var builder = WebApplication.CreateBuilder(args);
var extraConfig = Environment.GetEnvironmentVariable("DOMAINNOTICE_CONFIG");
if (!string.IsNullOrWhiteSpace(extraConfig))
    builder.Configuration.AddJsonFile(extraConfig, optional: false, reloadOnChange: false);
builder.Configuration.AddEnvironmentVariables("DOMAINNOTICE_");

builder.Host.UseSystemd();
// UseSystemd detects systemd by reading /proc/<ppid>/comm, which ProtectProc=invisible in domainnotice.service hides,
// so the Type=notify unit would never see READY=1. When systemd hands us a notify socket, use its lifetime anyway.
if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NOTIFY_SOCKET")) && !cliOnly)
{
    builder.Services.AddSingleton<Microsoft.Extensions.Hosting.Systemd.ISystemdNotifier, Microsoft.Extensions.Hosting.Systemd.SystemdNotifier>();
    builder.Services.AddSingleton<IHostLifetime, Microsoft.Extensions.Hosting.Systemd.SystemdLifetime>();
}

var consoleOptions = builder.Configuration.GetSection(ConsoleOptions.Section).Get<ConsoleOptions>() ?? new ConsoleOptions();
if (!cliOnly)
{
    builder.WebHost.UseUrls(consoleOptions.Urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    if (!string.IsNullOrWhiteSpace(consoleOptions.Certificate))
    {
        var cert = X509Certificate2.CreateFromPemFile(consoleOptions.Certificate,
            string.IsNullOrWhiteSpace(consoleOptions.CertificateKey) ? null : consoleOptions.CertificateKey);
        builder.WebHost.ConfigureKestrel(k => k.ConfigureHttpsDefaults(h => h.ServerCertificate = cert));
    }
}

var forwarded = consoleOptions.ForwardedHeaders;
if (forwarded.Enabled)
{
    // Optional (Console:ForwardedHeaders): trust X-Forwarded-* from the listed addresses (client IP, scheme, host).
    builder.Services.Configure<ForwardedHeadersOptions>(o =>
    {
        o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
        foreach (var proxy in forwarded.KnownProxies.Where(p => !string.IsNullOrWhiteSpace(p)))
            o.KnownProxies.Add(IPAddress.TryParse(proxy.Trim(), out var ip)
                ? ip
                : throw new InvalidOperationException($"Console:ForwardedHeaders:KnownProxies: '{proxy}' is not an IP address."));
        foreach (var network in forwarded.KnownNetworks.Where(n => !string.IsNullOrWhiteSpace(n)))
            o.KnownIPNetworks.Add(System.Net.IPNetwork.TryParse(network.Trim(), out var net)
                ? net
                : throw new InvalidOperationException($"Console:ForwardedHeaders:KnownNetworks: '{network}' is not a CIDR network."));
    });
}

builder.Services.AddDomainNoticeCore(builder.Configuration, builder.Environment);
builder.Services.AddDomainNoticeConsole(builder.Configuration);
if (cliOnly)
{
    // No listeners or background jobs for CLI runs.
    builder.Services.RemoveAll<IHostedService>();
}

var app = builder.Build();

if (printFingerprint)
{
    Console.WriteLine(app.Services.GetRequiredService<IServerIdentity>().Fingerprint);
    return 0;
}

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();
}
if (migrateOnly)
{
    Console.WriteLine("Migrations applied.");
    return 0;
}

if (listAccounts)
{
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    foreach (var a in await db.LocalAccounts.AsNoTracking().OrderBy(a => a.NormalizedUserName).ToListAsync())
        Console.WriteLine($"{a.UserName}\t{(a.Disabled ? "disabled" : "enabled")}");
    return 0;
}

if (resetAccountName is not null)
{
    var password = Console.In.ReadLine() ?? "";
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var normalized = resetAccountName.Trim().ToLowerInvariant();
    var account = await db.LocalAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.NormalizedUserName == normalized);
    if (account is null)
    {
        Console.Error.WriteLine($"There is no local account '{resetAccountName}'. Use --list-local-accounts, or --create-local-account.");
        return 1;
    }
    var accounts = app.Services.GetRequiredService<ILocalAccountService>();
    var reset = await accounts.ResetPasswordAsync(account.Id, password, "install.sh");
    if (!reset.Ok)
    {
        Console.Error.WriteLine(reset.Error);
        return 1;
    }
    if (account.Disabled) await accounts.SetDisabledAsync(account.Id, false, "install.sh");
    Console.WriteLine($"The password of '{account.UserName}' was reset{(account.Disabled ? " and the account enabled" : "")}.");
    return 0;
}

if (createAccountName is not null)
{
    var password = Console.In.ReadLine() ?? "";
    var accounts = app.Services.GetRequiredService<ILocalAccountService>();
    var result = await accounts.CreateAsync(createAccountName, password, "install");
    if (!result.Ok)
    {
        Console.Error.WriteLine(result.Error);
        return 1;
    }
    Console.WriteLine($"Local account '{createAccountName}' created.");
    return 0;
}

if (forwarded.Enabled) app.UseForwardedHeaders();

// Local health endpoint for update.sh (plan 11.2). Loopback only.
app.MapGet("/healthz", (HttpContext ctx) =>
    ctx.Connection.RemoteIpAddress is { } ip && System.Net.IPAddress.IsLoopback(ip)
        ? Results.Ok(new { status = "ok", version = AppVersion.Current })
        : Results.NotFound());

app.UseDomainNoticeConsole();

await app.RunAsync();
return 0;

public partial class Program;
