using DomainNotice.Server.Abstractions;
using DomainNotice.Server.Common;
using DomainNotice.Server.Data;
using DomainNotice.Server.Data.Entities;
using DomainNotice.Server.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace DomainNotice.Server.Accounts;

/// <summary>First-run rule shared by the sign-in service and the account service (plan 7.1).</summary>
internal sealed class FirstRunGate(IOptions<DirectoryOptions> directory, IDbContextFactory<AppDbContext> dbFactory)
{
    /// <summary>pg_advisory_xact_lock key that serialises first-account creation.</summary>
    public const long FirstAccountLockKey = 0x444E_4C41_4343_5431; // "DNLACCT1"

    public bool DirectoryConfigured => directory.Value.IsConfigured;

    public async Task<bool> IsAllowedAsync(CancellationToken ct)
    {
        if (DirectoryConfigured) return false;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return !await db.LocalAccounts.AnyAsync(ct);
    }
}

internal sealed class LocalAccountService(
    IDbContextFactory<AppDbContext> dbFactory,
    FirstRunGate firstRun,
    IAuditLog audit,
    TimeProvider time) : ILocalAccountService
{
    public const int MinPasswordLength = 8;
    public const int MaxPasswordLength = 1024;
    public const int MaxUserNameLength = 128;
    public const string SetupActor = "setup";

    public static string Normalize(string userName) => (userName ?? "").Trim().ToLowerInvariant();

    /// <summary>
    /// Session stamp of a password: part of the SHA-256 of the stored hash (which has a fresh salt on every reset), so
    /// a password reset in the Console or with --reset-local-password ends existing sessions. Reveals nothing usable.
    /// </summary>
    public static string PasswordStamp(string passwordHash) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(passwordHash ?? "")))[..32];

    internal static string? ValidateUserName(string? userName)
    {
        var name = userName?.Trim() ?? "";
        if (name.Length == 0) return "Enter a user name.";
        if (name.Length > MaxUserNameLength) return $"The user name can have at most {MaxUserNameLength} characters.";
        if (name.Any(char.IsControl)) return "The user name contains invalid characters.";
        return null;
    }

    internal static string? ValidatePassword(string? password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinPasswordLength)
            return $"Use a password of at least {MinPasswordLength} characters.";
        if (password.Length > MaxPasswordLength) return $"The password can have at most {MaxPasswordLength} characters.";
        return null;
    }

    public async Task<bool> AnyAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.LocalAccounts.AnyAsync(ct);
    }

    public async Task<OperationResult> CreateAsync(string userName, string password, string actor, CancellationToken ct = default)
    {
        if ((ValidateUserName(userName) ?? ValidatePassword(password)) is { } error) return OperationResult.Fail(error);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var result = await InsertAsync(db, userName, password, actor, ct);
        if (result.Result.Ok)
            await audit.WriteAsync(actor, AuditActions.LocalAccountCreated, "LocalAccount", result.Id.ToString(), userName.Trim(), ct);
        return result.Result;
    }

    public async Task<OperationResult> CreateFirstAsync(string userName, string password, CancellationToken ct = default)
    {
        if ((ValidateUserName(userName) ?? ValidatePassword(password)) is { } error) return OperationResult.Fail(error);
        if (firstRun.DirectoryConfigured)
            return OperationResult.Fail("A directory is configured; sign in with a domain account and add local accounts in Settings.");

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Serialise concurrent setup requests: the unique index alone does not stop two different names.
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({FirstRunGate.FirstAccountLockKey})", ct);
        if (await db.LocalAccounts.AnyAsync(ct))
            return OperationResult.Fail("Setup is already complete. Sign in instead.");
        var result = await InsertAsync(db, userName, password, SetupActor, ct);
        if (!result.Result.Ok) return result.Result;
        await tx.CommitAsync(ct);
        await audit.WriteAsync(SetupActor, AuditActions.LocalAccountCreated, "LocalAccount", result.Id.ToString(),
            userName.Trim() + " (first account)", ct);
        return OperationResult.Success;
    }

    private async Task<(OperationResult Result, Guid Id)> InsertAsync(AppDbContext db, string userName, string password, string actor, CancellationToken ct)
    {
        var name = userName.Trim();
        var normalized = Normalize(name);
        if (await db.LocalAccounts.AnyAsync(a => a.NormalizedUserName == normalized, ct))
            return (OperationResult.Fail("A local account with this name already exists."), Guid.Empty);
        var now = time.GetUtcNow().UtcDateTime;
        var account = new LocalAccount
        {
            Id = Guid.NewGuid(),
            UserName = name,
            NormalizedUserName = normalized,
            PasswordHash = PasswordHasher.Hash(password),
            CreatedAtUtc = now,
            CreatedBy = actor,
            PasswordChangedAtUtc = now,
        };
        db.LocalAccounts.Add(account);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return (OperationResult.Fail("A local account with this name already exists."), Guid.Empty);
        }
        return (OperationResult.Success, account.Id);
    }

    public async Task<OperationResult> ResetPasswordAsync(Guid id, string newPassword, string actor, CancellationToken ct = default)
    {
        if (ValidatePassword(newPassword) is { } error) return OperationResult.Fail(error);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var account = await db.LocalAccounts.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (account is null) return OperationResult.Fail("The local account was not found.");
        account.PasswordHash = PasswordHasher.Hash(newPassword);
        account.PasswordChangedAtUtc = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(actor, AuditActions.LocalAccountPasswordReset, "LocalAccount", id.ToString(), account.UserName, ct);
        return OperationResult.Success;
    }

    public async Task<OperationResult> SetDisabledAsync(Guid id, bool disabled, string actor, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var account = await db.LocalAccounts.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (account is null) return OperationResult.Fail("The local account was not found.");
        if (account.Disabled == disabled) return OperationResult.Success;
        if (disabled && !firstRun.DirectoryConfigured
            && !await db.LocalAccounts.AnyAsync(a => a.Id != id && !a.Disabled, ct))
            return OperationResult.Fail("This is the last enabled local account and no directory is configured; disabling it would lock everyone out.");
        account.Disabled = disabled;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(actor, disabled ? AuditActions.LocalAccountDisabled : AuditActions.LocalAccountEnabled,
            "LocalAccount", id.ToString(), account.UserName, ct);
        return OperationResult.Success;
    }
}
