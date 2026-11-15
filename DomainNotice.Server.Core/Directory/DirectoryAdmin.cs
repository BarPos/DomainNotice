using DomainNotice.Server.Abstractions;
using DomainNotice.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using DirectoryOptions = DomainNotice.Server.Options.DirectoryOptions;

namespace DomainNotice.Server.Directory;

internal sealed class DirectoryAdmin(
    IOptions<DirectoryOptions> options,
    DirectorySyncCoordinator coordinator,
    IDbContextFactory<AppDbContext> dbFactory,
    ILdapSource source,
    LdapConnector connector,
    BindPasswordProvider passwords,
    ISettingsStore settings,
    IAuditLog audit) : IDirectoryAdmin
{
    public bool IsConfigured => options.Value.IsConfigured;

    public async Task<DirectorySyncStatus> GetStatusAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var lastSuccess = await db.DirectorySyncRuns.Where(r => r.Success).OrderByDescending(r => r.StartedAtUtc)
            .Select(r => r.FinishedAtUtc ?? r.StartedAtUtc).FirstOrDefaultAsync(ct);
        var last = await db.DirectorySyncRuns.OrderByDescending(r => r.StartedAtUtc)
            .Select(r => new { r.StartedAtUtc, r.Success, r.Error, r.FinishedAtUtc }).FirstOrDefaultAsync(ct);
        var count = await db.DirectoryObjects.CountAsync(o => !o.Deleted, ct);
        var error = last is { Success: false, FinishedAtUtc: not null } ? last.Error : coordinator.ConfigError;
        return new DirectorySyncStatus(IsConfigured, coordinator.Running, lastSuccess == default ? null : lastSuccess,
            last?.StartedAtUtc, error, count);
    }

    public async Task RequestSyncAsync(bool full, string actor, CancellationToken ct = default)
    {
        if (!IsConfigured) return;
        await audit.WriteAsync(actor, AuditActions.DirectorySyncRequested, "Directory", null, full ? "full" : "incremental", ct);
        coordinator.Request(full, actor);
    }

    public async Task<OperationResult> SetBindPasswordAsync(string password, string actor, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(password)) return OperationResult.Fail("Enter the password.");
        if (!passwords.IsDatabaseSource)
            return OperationResult.Fail("Directory:BindPasswordSource is not \"Database\"; the password is read from " +
                                        $"{options.Value.BindPasswordSource}. Change the setting to store it here.");
        await settings.SetSecretAsync(SettingKeys.DirectoryBindPassword, password, actor, ct);
        connector.ClearPool();
        await audit.WriteAsync(actor, AuditActions.DirectoryBindPasswordChanged, "Directory", null, null, ct);
        return OperationResult.Success;
    }

    public async Task<OperationResult> TestConnectionAsync(CancellationToken ct = default)
    {
        if (!IsConfigured) return OperationResult.Fail("The directory is not configured (Directory:Hosts and Directory:BaseDn).");
        try
        {
            await using var session = await source.OpenAsync(ct);
            await session.ReadRootDseAsync(ct);
            return OperationResult.Success;
        }
        catch (DirectoryConnectException e)
        {
            return OperationResult.Fail(e.Message);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return OperationResult.Fail($"{e.GetType().Name}: {e.Message}");
        }
    }
}
