using DomainNotice.Server.Abstractions;
using DomainNotice.Server.Data;
using DomainNotice.Server.Data.Entities;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace DomainNotice.Server.Common;

internal sealed class SettingsStore(IDbContextFactory<AppDbContext> dbFactory, IDataProtectionProvider dataProtection, TimeProvider time)
    : ISettingsStore
{
    private readonly IDataProtector _protector = dataProtection.CreateProtector("DomainNotice.Settings");

    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == key, ct);
        return row is null || row.IsSecret ? null : row.Value;
    }

    public Task SetAsync(string key, string? value, string actor, CancellationToken ct = default) =>
        UpsertAsync(key, value, false, actor, ct);

    public async Task<string?> GetSecretAsync(string key, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == key, ct);
        if (row?.Value is null || !row.IsSecret) return null;
        return _protector.Unprotect(row.Value);
    }

    public Task SetSecretAsync(string key, string? value, string actor, CancellationToken ct = default) =>
        UpsertAsync(key, value is null ? null : _protector.Protect(value), true, actor, ct);

    private async Task UpsertAsync(string key, string? value, bool secret, string actor, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.Settings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (row is null)
        {
            row = new Setting { Key = key };
            db.Settings.Add(row);
        }
        row.Value = value;
        row.IsSecret = secret;
        row.UpdatedAtUtc = time.GetUtcNow().UtcDateTime;
        row.UpdatedBy = actor;
        await db.SaveChangesAsync(ct);
    }
}
