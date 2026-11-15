using System.Globalization;
using System.Text;
using System.Text.Json;
using DomainNotice.Server.Abstractions;
using DomainNotice.Server.Data;
using DomainNotice.Server.Data.Entities;
using DomainNotice.Server.Options;
using DomainNotice.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DomainNotice.Server.Updates;

/// <summary>
/// Server and Console self-update (plan 11.2): release checks, staging the package and writing the request file
/// that <c>update.sh</c> applies, and recording the result <c>update.sh</c> leaves behind.
/// </summary>
internal sealed class ServerUpdateService(
    IReleaseSource releases,
    IServerInstallation installation,
    AgentPackageCache packages,
    ISettingsStore settings,
    IAuditLog audit,
    IDbContextFactory<AppDbContext> dbFactory,
    IOptions<PathsOptions> paths,
    TimeProvider time,
    ILogger<ServerUpdateService> logger) : IServerUpdateService
{
    public const long MaxTarballBytes = 1024L * 1024 * 1024;
    /// <summary>A staged/applying run older than this no longer blocks a new one.</summary>
    internal static readonly TimeSpan StaleRunAfter = TimeSpan.FromHours(1);

    private static readonly JsonSerializerOptions Json = new();
    private readonly SemaphoreSlim _updateLock = new(1, 1);
    private readonly SemaphoreSlim _checkLock = new(1, 1);

    private string UpdatesDir => paths.Value.UpdatesDirectory;

    public async Task<ServerUpdateStatus> GetStatusAsync(CancellationToken ct = default)
    {
        // Lazily pick up a result file written after this process started (update.sh writes it after the new
        // version is already healthy).
        await ProcessResultFileAsync(ct);
        var last = await ReadLastCheckAsync(ct);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var lastRun = await db.ServerUpdateRuns.AsNoTracking().OrderByDescending(r => r.RequestedAtUtc).FirstOrDefaultAsync(ct);
        var configured = releases.IsConfigured;
        var latest = configured ? last?.Latest : null;
        return new ServerUpdateStatus(
            configured,
            AppVersion.Current,
            latest,
            configured && IsUpdate(latest?.Version),
            last?.CheckedAtUtc,
            configured ? last?.Error : null,
            lastRun,
            installation.SelfUpdateSupported);
    }

    public async Task<ServerUpdateStatus> CheckNowAsync(string actor, CancellationToken ct = default)
    {
        if (!releases.IsConfigured) return await GetStatusAsync(ct);
        await _checkLock.WaitAsync(ct);
        try
        {
            var now = time.GetUtcNow().UtcDateTime;
            LastCheck record;
            try
            {
                var latest = await releases.GetLatestAsync(ct);
                record = new LastCheck(now, latest?.Info, latest is null ? "No release found." : null);
            }
            catch (Exception ex) when (ex is ReleaseSourceException or HttpRequestException
                                           || (ex is TaskCanceledException && !ct.IsCancellationRequested))
            {
                logger.LogWarning(ex, "Release check failed");
                record = new LastCheck(now, (await ReadLastCheckAsync(ct))?.Latest, ex.Message);
            }
            await settings.SetAsync(SettingKeys.UpdatesLastCheck, JsonSerializer.Serialize(record, Json), actor, ct);

            // Plan 11.3: the agent MSI for the server's own version is fetched once, also on "Check now".
            var ensure = await packages.EnsureCurrentAsync(ct);
            if (!ensure.Ok) logger.LogInformation("Agent package for {Version} not cached: {Error}", AppVersion.Current, ensure.Error);
        }
        finally
        {
            _checkLock.Release();
        }
        return await GetStatusAsync(ct);
    }

    public async Task<OperationResult> UpdateNowAsync(string actor, CancellationToken ct = default)
    {
        if (!releases.IsConfigured) return OperationResult.Fail("Updates are not configured.");
        if (!installation.SelfUpdateSupported)
            return OperationResult.Fail(installation.UnsupportedReason ?? "Self-update is not supported on this server.");
        var arch = installation.Architecture;
        if (arch is null) return OperationResult.Fail("Unsupported CPU architecture.");

        if (!await _updateLock.WaitAsync(0, ct)) return OperationResult.Fail("An update is already being prepared.");
        Guid? runId = null;
        string? versionDir = null;
        try
        {
            await using (var db = await dbFactory.CreateDbContextAsync(ct))
            {
                var cutoff = time.GetUtcNow().UtcDateTime - StaleRunAfter;
                var busy = await db.ServerUpdateRuns.AnyAsync(r =>
                    (r.State == ServerUpdateState.Downloading || r.State == ServerUpdateState.Staged || r.State == ServerUpdateState.Applying)
                    && r.RequestedAtUtc > cutoff, ct);
                if (busy) return OperationResult.Fail("An update is already in progress.");
            }

            Release? release;
            try
            {
                release = await releases.GetLatestAsync(ct);
            }
            catch (ReleaseSourceException ex)
            {
                return OperationResult.Fail(ex.Message);
            }
            if (release is null) return OperationResult.Fail("No release found.");
            var version = release.Info.Version;
            var refusal = CheckTargetVersion(version);
            if (refusal is not null) return OperationResult.Fail(refusal);

            var tarName = ReleaseAssetNames.ServerTarball(version, arch);
            var tarAsset = release.FindAsset(tarName);
            var sumsAsset = release.FindAsset(ReleaseAssetNames.Sums);
            if (tarAsset is null || sumsAsset is null)
                return OperationResult.Fail($"Release {release.Info.TagName} has no {tarName} or {ReleaseAssetNames.Sums}.");

            var run = new ServerUpdateRun
            {
                Id = Guid.NewGuid(),
                FromVersion = AppVersion.Current,
                ToVersion = version,
                RequestedBy = actor,
                State = ServerUpdateState.Downloading,
                RequestedAtUtc = time.GetUtcNow().UtcDateTime,
            };
            runId = run.Id;
            await using (var db = await dbFactory.CreateDbContextAsync(ct))
            {
                db.ServerUpdateRuns.Add(run);
                await db.SaveChangesAsync(ct);
            }
            await audit.WriteAsync(actor, AuditActions.ServerUpdateRequested, "ServerUpdateRun", run.Id.ToString(),
                $"{AppVersion.Current} -> {version}", ct);

            // Download: SHA256SUMS.txt first, then the package, hashing while writing.
            string sumsText;
            await using (var s = await releases.OpenAssetAsync(sumsAsset, ct))
                sumsText = await FileHashing.ReadSmallTextAsync(s, Sha256Sums.MaxBytes, ct);
            if (!Sha256Sums.Parse(sumsText).TryGetValue(tarName, out var expected))
                return await FailRunAsync(run.Id, $"{ReleaseAssetNames.Sums} has no entry for {tarName}.", ct);

            versionDir = Path.Combine(UpdatesDir, version);
            FileHashing.TryDeleteDirectory(versionDir);
            System.IO.Directory.CreateDirectory(versionDir);
            var partial = Path.Combine(versionDir, tarName + ".partial");
            (long Size, string Sha256, byte[] Head) copied;
            await using (var s = await releases.OpenAssetAsync(tarAsset, ct))
                copied = await FileHashing.CopyAndHashAsync(s, partial, MaxTarballBytes, ct);
            if (!string.Equals(copied.Sha256, expected, StringComparison.Ordinal))
            {
                FileHashing.TryDeleteDirectory(versionDir);
                return await FailRunAsync(run.Id, $"{tarName}: SHA-256 mismatch (expected {expected}, got {copied.Sha256}).", ct);
            }
            File.Move(partial, Path.Combine(versionDir, tarName), overwrite: true);
            await File.WriteAllTextAsync(Path.Combine(versionDir, ReleaseAssetNames.Sums), sumsText, ct);

            // Request file, atomically: the path unit fires as soon as request.conf exists.
            var request = UpdateRequestFile.BuildRequest(run.Id, version, AppVersion.Current, arch, tarName, copied.Sha256);
            var temp = Path.Combine(UpdatesDir, $".{UpdateRequestFile.RequestFileName}.{Guid.NewGuid():N}.tmp");
            await File.WriteAllTextAsync(temp, request, new UTF8Encoding(false), ct);
            File.Move(temp, Path.Combine(UpdatesDir, UpdateRequestFile.RequestFileName), overwrite: true);

            await SetRunStateAsync(run.Id, ServerUpdateState.Staged, $"Staged {tarName}; waiting for update.sh.", finished: false, ct);
            logger.LogInformation("Server update {From} -> {To} staged by {Actor}", AppVersion.Current, version, actor);
            return OperationResult.Success;
        }
        catch (Exception ex) when (ex is ReleaseSourceException or HttpRequestException or IOException or InvalidDataException
                                       or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Server update failed");
            if (versionDir is not null) FileHashing.TryDeleteDirectory(versionDir);
            if (runId is { } id) return await FailRunAsync(id, ex.Message, CancellationToken.None);
            return OperationResult.Fail(ex.Message);
        }
        finally
        {
            _updateLock.Release();
        }
    }

    /// <summary>Null when <paramref name="version"/> is an acceptable update target, else the reason.</summary>
    internal static string? CheckTargetVersion(string? version)
    {
        if (!AppVersion.IsPlainTriplet(version)) return $"Release version '{version}' is not a plain x.y.z version.";
        if (AppVersion.Compare(version!, AppVersion.Current) <= 0)
            return $"Release {version} is not newer than the running version {AppVersion.Current}.";
        return null;
    }

    private static bool IsUpdate(string? version) => CheckTargetVersion(version) is null;

    /// <summary>
    /// Reads <c>result.conf</c> left by update.sh and finishes the matching <see cref="ServerUpdateRun"/>.
    /// Without a result file, an open run whose target is the running version is marked Succeeded, and
    /// a run still Downloading from a previous process is marked Failed.
    /// </summary>
    internal async Task ProcessResultFileAsync(CancellationToken ct)
    {
        var resultPath = Path.Combine(UpdatesDir, UpdateRequestFile.ResultFileName);
        var now = time.GetUtcNow().UtcDateTime;
        Dictionary<string, string>? result = null;
        try
        {
            var fi = new FileInfo(resultPath);
            if (fi.Exists && fi.LinkTarget is null && fi.Length < 64 * 1024)
                result = UpdateRequestFile.ParseKeyValues(await File.ReadAllTextAsync(resultPath, ct));
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Cannot read {Path}", resultPath);
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (result is not null)
        {
            var state = result.GetValueOrDefault("state") switch
            {
                "Succeeded" => ServerUpdateState.Succeeded,
                "RolledBack" => ServerUpdateState.RolledBack,
                _ => ServerUpdateState.Failed,
            };
            var version = result.GetValueOrDefault("version") ?? "";
            var from = result.GetValueOrDefault("from_version") ?? "";
            var message = Truncate(result.GetValueOrDefault("message"), 2000);
            DateTime finished = now;
            if (result.GetValueOrDefault("finished_at") is { } f &&
                DateTime.TryParse(f, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt))
                finished = DateTime.SpecifyKind(dt, DateTimeKind.Utc);

            ServerUpdateRun? run = null;
            if (Guid.TryParse(result.GetValueOrDefault("run_id"), out var runId))
                run = await db.ServerUpdateRuns.FirstOrDefaultAsync(r => r.Id == runId, ct);
            if (run is null)
            {
                // E.g. an upgrade run by install.sh, which has no run of its own.
                run = new ServerUpdateRun
                {
                    Id = runId == Guid.Empty ? Guid.NewGuid() : runId,
                    FromVersion = Truncate(from, 32) ?? "",
                    ToVersion = Truncate(version, 32) ?? "",
                    RequestedBy = "update.sh",
                    RequestedAtUtc = finished,
                };
                db.ServerUpdateRuns.Add(run);
            }
            run.State = state;
            run.Message = message;
            run.FinishedAtUtc = finished;
            await db.SaveChangesAsync(ct);
            FileHashing.TryDelete(resultPath);
            if (AppVersion.IsPlainTriplet(run.ToVersion)) FileHashing.TryDeleteDirectory(Path.Combine(UpdatesDir, run.ToVersion));
            await audit.WriteAsync("update.sh", UpdateAuditActions.ServerUpdateFinished, "ServerUpdateRun", run.Id.ToString(),
                $"{run.FromVersion} -> {run.ToVersion}: {state}. {message}", ct);
            logger.LogInformation("Server update {From} -> {To} finished: {State} {Message}", run.FromVersion, run.ToVersion, state, message);
            return;
        }

        var open = await db.ServerUpdateRuns
            .Where(r => r.State == ServerUpdateState.Downloading || r.State == ServerUpdateState.Staged || r.State == ServerUpdateState.Applying)
            .ToListAsync(ct);
        var changed = false;
        foreach (var run in open)
        {
            if (run.ToVersion == AppVersion.Current)
            {
                run.State = ServerUpdateState.Succeeded;
                run.Message ??= "Running the new version.";
                run.FinishedAtUtc = now;
                changed = true;
            }
            else if (run.State == ServerUpdateState.Downloading && _updateLock.CurrentCount > 0)
            {
                run.State = ServerUpdateState.Failed;
                run.Message = "The download was interrupted.";
                run.FinishedAtUtc = now;
                changed = true;
            }
        }
        if (changed) await db.SaveChangesAsync(ct);
    }

    private async Task<OperationResult> FailRunAsync(Guid runId, string message, CancellationToken ct)
    {
        await SetRunStateAsync(runId, ServerUpdateState.Failed, message, finished: true, ct);
        return OperationResult.Fail(message);
    }

    private async Task SetRunStateAsync(Guid runId, ServerUpdateState state, string message, bool finished, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var run = await db.ServerUpdateRuns.FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run is null) return;
        run.State = state;
        run.Message = Truncate(message, 2000);
        if (finished) run.FinishedAtUtc = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
    }

    internal async Task<LastCheck?> ReadLastCheckAsync(CancellationToken ct)
    {
        var json = await settings.GetAsync(SettingKeys.UpdatesLastCheck, ct);
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<LastCheck>(json, Json); }
        catch (JsonException) { return null; }
    }

    private static string? Truncate(string? s, int max) => s is null ? null : s.Length <= max ? s : s[..max];

    internal sealed record LastCheck(DateTime CheckedAtUtc, ReleaseInfo? Latest, string? Error);
}
