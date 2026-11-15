using System.Text.Json;
using DomainNotice.Server.Abstractions;
using DomainNotice.Server.Options;
using DomainNotice.Shared;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DomainNotice.Server.Updates;

/// <summary>
/// The agent MSI cache in <c>Paths:Data/packages/agent/&lt;version&gt;/</c> (plan 11.3). Each version folder holds
/// <c>DomainNotice.Agent-&lt;version&gt;-x64.msi</c> and <c>package.json</c> (size, sha256, source, time).
/// Only the server's own version and the newest older version are kept.
/// </summary>
internal sealed class AgentPackageCache(IOptions<PathsOptions> paths, IReleaseSource releases, IAuditLog audit,
    TimeProvider time, ILogger<AgentPackageCache> logger) : IAgentPackageCache
{
    public const long MaxMsiBytes = 512L * 1024 * 1024;
    public const string MetadataFileName = "package.json";
    public const string SourceGitHub = "GitHub";
    public const string SourceImport = "Import";

    /// <summary>OLE compound file signature; every MSI starts with it.</summary>
    internal static ReadOnlySpan<byte> OleSignature => [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly SemaphoreSlim _lock = new(1, 1);
    private AgentPackageInfo? _current;

    private string Root => paths.Value.AgentPackagesDirectory;

    /// <summary>Size limit for imports and downloads (settable for tests).</summary>
    internal long MaxBytes { get; set; } = MaxMsiBytes;

    public Task<AgentPackageInfo?> GetCurrentAsync(CancellationToken ct = default)
    {
        var cached = _current;
        if (cached is not null && File.Exists(cached.FilePath)) return Task.FromResult<AgentPackageInfo?>(cached);
        _current = Load(AppVersion.Current);
        return Task.FromResult(_current);
    }

    public Task<IReadOnlyList<AgentPackageInfo>> ListAsync(CancellationToken ct = default)
    {
        var list = new List<AgentPackageInfo>();
        if (System.IO.Directory.Exists(Root))
        {
            foreach (var dir in System.IO.Directory.EnumerateDirectories(Root))
            {
                var name = Path.GetFileName(dir);
                if (!AppVersion.IsPlainTriplet(name)) continue;
                if (Load(name) is { } info) list.Add(info);
            }
        }
        IReadOnlyList<AgentPackageInfo> sorted = list.OrderByDescending(p => AppVersion.TryParse(p.Version, out var v) ? v : new Version()).ToList();
        return Task.FromResult(sorted);
    }

    public Stream OpenRead(AgentPackageInfo package) =>
        new FileStream(package.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 81920, useAsync: true);

    /// <summary>P13: import a locally built MSI as the package for the server's own version.</summary>
    public async Task<OperationResult> ImportAsync(Stream msi, string fileName, string actor, CancellationToken ct = default)
    {
        // fileName is only checked and logged, never used in a path.
        if (string.IsNullOrWhiteSpace(fileName) || !fileName.Trim().EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
            return OperationResult.Fail("Choose an .msi file.");

        await _lock.WaitAsync(ct);
        try
        {
            var version = AppVersion.Current;
            var dir = VersionDirectory(version);
            System.IO.Directory.CreateDirectory(dir);
            var temp = Path.Combine(dir, $".import-{Guid.NewGuid():N}.tmp");
            (long Size, string Sha256, byte[] Head) copied;
            try
            {
                copied = await FileHashing.CopyAndHashAsync(msi, temp, MaxBytes, ct);
            }
            catch (InvalidDataException ex)
            {
                return OperationResult.Fail(ex.Message);
            }
            if (copied.Size < OleSignature.Length || !copied.Head.AsSpan().SequenceEqual(OleSignature))
            {
                FileHashing.TryDelete(temp);
                return OperationResult.Fail("The file is not an MSI package (no OLE compound file signature).");
            }

            var info = Commit(version, temp, copied.Size, copied.Sha256, SourceImport);
            Prune();
            await audit.WriteAsync(actor, AuditActions.AgentPackageImported, "AgentPackage", version,
                $"file={Sanitize(fileName)}; size={copied.Size}; sha256={copied.Sha256}", ct);
            logger.LogInformation("Imported agent MSI {Version} ({Size} bytes, sha256 {Sha256}) by {Actor}", version, copied.Size, copied.Sha256, actor);
            _current = info;
            return OperationResult.Success;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Downloads the MSI for the server's own version from the release tagged <c>v&lt;version&gt;</c> (never
    /// "latest") when it is not cached yet, and checks it against that release's SHA256SUMS.txt.
    /// No network call when the release source is not configured or the package is already cached.
    /// </summary>
    public async Task<OperationResult> EnsureCurrentAsync(CancellationToken ct = default)
    {
        if (await GetCurrentAsync(ct) is not null) return OperationResult.Success;
        if (!releases.IsConfigured) return OperationResult.Fail("Updates are not configured.");

        await _lock.WaitAsync(ct);
        try
        {
            if (Load(AppVersion.Current) is { } existing)
            {
                _current = existing;
                return OperationResult.Success;
            }
            var version = AppVersion.Current;
            var release = await releases.GetByVersionAsync(version, ct);
            if (release is null) return OperationResult.Fail($"There is no release tagged {ReleaseAssetNames.Tag(version)}.");
            var msiName = ReleaseAssetNames.AgentMsi(version);
            var msiAsset = release.FindAsset(msiName);
            var sumsAsset = release.FindAsset(ReleaseAssetNames.Sums);
            if (msiAsset is null || sumsAsset is null)
                return OperationResult.Fail($"Release {release.Info.TagName} has no {msiName} or {ReleaseAssetNames.Sums}.");

            string sumsText;
            await using (var s = await releases.OpenAssetAsync(sumsAsset, ct))
                sumsText = await FileHashing.ReadSmallTextAsync(s, Sha256Sums.MaxBytes, ct);
            if (!Sha256Sums.Parse(sumsText).TryGetValue(msiName, out var expected))
                return OperationResult.Fail($"{ReleaseAssetNames.Sums} has no entry for {msiName}.");

            var dir = VersionDirectory(version);
            System.IO.Directory.CreateDirectory(dir);
            var temp = Path.Combine(dir, $".download-{Guid.NewGuid():N}.tmp");
            (long Size, string Sha256, byte[] Head) copied;
            await using (var s = await releases.OpenAssetAsync(msiAsset, ct))
                copied = await FileHashing.CopyAndHashAsync(s, temp, MaxBytes, ct);
            if (!string.Equals(copied.Sha256, expected, StringComparison.Ordinal))
            {
                FileHashing.TryDelete(temp);
                return OperationResult.Fail($"{msiName}: SHA-256 mismatch (expected {expected}, got {copied.Sha256}).");
            }
            if (!copied.Head.AsSpan().SequenceEqual(OleSignature))
            {
                FileHashing.TryDelete(temp);
                return OperationResult.Fail($"{msiName} is not an MSI package.");
            }

            _current = Commit(version, temp, copied.Size, copied.Sha256, SourceGitHub);
            Prune();
            await audit.WriteAsync("system", UpdateAuditActions.AgentPackageDownloaded, "AgentPackage", version,
                $"size={copied.Size}; sha256={copied.Sha256}", ct);
            logger.LogInformation("Downloaded agent MSI {Version} ({Size} bytes)", version, copied.Size);
            return OperationResult.Success;
        }
        catch (Exception ex) when (ex is ReleaseSourceException or HttpRequestException or IOException or InvalidDataException)
        {
            logger.LogWarning(ex, "Agent package download failed");
            return OperationResult.Fail(ex.Message);
        }
        finally
        {
            _lock.Release();
        }
    }

    private string VersionDirectory(string version) => Path.Combine(Root, version);

    private AgentPackageInfo Commit(string version, string tempFile, long size, string sha256, string source)
    {
        var dir = VersionDirectory(version);
        var target = Path.Combine(dir, ReleaseAssetNames.AgentMsi(version));
        File.Move(tempFile, target, overwrite: true);
        var info = new AgentPackageInfo(version, size, sha256, target, time.GetUtcNow().UtcDateTime, source);
        var meta = new PackageMetadata(version, size, sha256, info.AddedAtUtc, source);
        var metaTemp = Path.Combine(dir, MetadataFileName + ".tmp");
        File.WriteAllText(metaTemp, JsonSerializer.Serialize(meta, Json));
        File.Move(metaTemp, Path.Combine(dir, MetadataFileName), overwrite: true);
        return info;
    }

    private AgentPackageInfo? Load(string version)
    {
        var dir = VersionDirectory(version);
        var file = Path.Combine(dir, ReleaseAssetNames.AgentMsi(version));
        var metaPath = Path.Combine(dir, MetadataFileName);
        if (!File.Exists(file) || !File.Exists(metaPath)) return null;
        try
        {
            var meta = JsonSerializer.Deserialize<PackageMetadata>(File.ReadAllText(metaPath));
            if (meta is null || meta.Version != version || meta.Sha256.Length != 64) return null;
            if (new FileInfo(file).Length != meta.Size) return null;
            return new AgentPackageInfo(version, meta.Size, meta.Sha256, file, meta.AddedAtUtc, meta.Source);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            logger.LogWarning(ex, "Agent package metadata for {Version} is unreadable", version);
            return null;
        }
    }

    /// <summary>Keeps the server's version and the newest older version; removes everything else.</summary>
    internal void Prune()
    {
        if (!System.IO.Directory.Exists(Root)) return;
        var current = AppVersion.CurrentVersion;
        var versions = System.IO.Directory.EnumerateDirectories(Root)
            .Select(d => (Dir: d, Name: Path.GetFileName(d)))
            .Select(x => (x.Dir, Ok: AppVersion.TryParse(x.Name, out var v) && AppVersion.IsPlainTriplet(x.Name), V: v))
            .ToList();
        var previous = versions.Where(x => x.Ok && x.V < current && Load(Path.GetFileName(x.Dir)) is not null)
            .OrderByDescending(x => x.V).Select(x => x.Dir).FirstOrDefault();
        foreach (var x in versions)
        {
            if (x.Ok && x.V == current) continue;
            if (x.Dir == previous) continue;
            logger.LogInformation("Removing cached agent package folder {Dir}", x.Dir);
            FileHashing.TryDeleteDirectory(x.Dir);
        }
    }

    private static string Sanitize(string s) => new(s.Where(c => !char.IsControl(c)).Take(200).ToArray());

    private sealed record PackageMetadata(string Version, long Size, string Sha256, DateTime AddedAtUtc, string Source);
}

/// <summary>Audit actions of the Updates area that are not in <see cref="AuditActions"/>.</summary>
public static class UpdateAuditActions
{
    public const string AgentPackageDownloaded = "agent-package.downloaded";
    public const string ServerUpdateFinished = "server-update.finished";
    public const string ServerUpdateChecked = "server-update.checked";
}

/// <summary>Setting keys of the Updates area (stored with <see cref="ISettingsStore"/>).</summary>
public static class UpdateSettingKeys
{
    /// <summary>P11: the server version for which automatic agent updates were already requested.</summary>
    public const string AutoUpdateDoneForVersion = "Updates.AutoUpdateDoneForVersion";
}
