using System.Security.Cryptography;
using DomainNotice.Server.Abstractions;
using DomainNotice.Server.Tests.Infrastructure;
using DomainNotice.Server.Updates;
using DomainNotice.Shared;
using Microsoft.EntityFrameworkCore;

namespace DomainNotice.Server.Tests.Updates;

public class AgentPackageCacheTests(PostgresFixture pg)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Ensure_downloads_msi_from_release_tagged_with_server_version_never_latest()
    {
        await using var kit = await UpdatesKit.CreateAsync(pg);
        var msi = UpdatesKit.FakeMsi(70_000);
        kit.Releases.Tagged.Add(kit.Releases.MakeRelease(AppVersion.Current, [(ReleaseAssetNames.AgentMsi(AppVersion.Current), msi)]));
        kit.Releases.Latest = kit.Releases.MakeRelease("9.0.0", [(ReleaseAssetNames.AgentMsi("9.0.0"), UpdatesKit.FakeMsi(10, 2))]);

        var result = await kit.Cache.EnsureCurrentAsync(Ct);

        Assert.True(result.Ok, result.Error);
        Assert.DoesNotContain("latest", kit.Releases.Calls);
        Assert.Contains("tag:" + AppVersion.Current, kit.Releases.Calls);
        var current = await kit.Cache.GetCurrentAsync(Ct);
        Assert.NotNull(current);
        Assert.Equal(AppVersion.Current, current.Version);
        Assert.Equal(msi.Length, current.Size);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(msi)), current.Sha256);
        Assert.Equal(AgentPackageCache.SourceGitHub, current.Source);
        Assert.Equal(Path.Combine(kit.Paths.AgentPackagesDirectory, AppVersion.Current, ReleaseAssetNames.AgentMsi(AppVersion.Current)), current.FilePath);

        // Downloaded once: a second call makes no source calls.
        var calls = kit.Releases.Calls.Count;
        Assert.True((await kit.Cache.EnsureCurrentAsync(Ct)).Ok);
        Assert.Equal(calls, kit.Releases.Calls.Count);
    }

    [Fact]
    public async Task Ensure_rejects_sha_mismatch()
    {
        await using var kit = await UpdatesKit.CreateAsync(pg);
        kit.Releases.Tagged.Add(kit.Releases.MakeRelease(AppVersion.Current,
            [(ReleaseAssetNames.AgentMsi(AppVersion.Current), UpdatesKit.FakeMsi(5000))], corruptSums: true));

        var result = await kit.Cache.EnsureCurrentAsync(Ct);

        Assert.False(result.Ok);
        Assert.Contains("SHA-256 mismatch", result.Error);
        Assert.Null(await kit.Cache.GetCurrentAsync(Ct));
        Assert.Empty(System.IO.Directory.GetFiles(Path.Combine(kit.Paths.AgentPackagesDirectory, AppVersion.Current)));
    }

    [Fact]
    public async Task Keeps_current_and_previous_version_only()
    {
        await using var kit = await UpdatesKit.CreateAsync(pg);
        kit.WriteCachedVersion("0.8.0");
        kit.WriteCachedVersion("0.9.0");
        kit.WriteCachedVersion("0.9.5");
        kit.WriteCachedVersion("1.5.0"); // newer than the server (e.g. after a rollback)
        System.IO.Directory.CreateDirectory(Path.Combine(kit.Paths.AgentPackagesDirectory, "junk"));

        await kit.SeedPackageAsync(2000);

        var versions = (await kit.Cache.ListAsync(Ct)).Select(p => p.Version).ToList();
        Assert.Equal([AppVersion.Current, "0.9.5"], versions);
        Assert.Equal(2, System.IO.Directory.GetDirectories(kit.Paths.AgentPackagesDirectory).Length);
    }

    [Fact]
    public async Task Ensure_rejects_release_asset_that_is_not_an_msi_or_too_large()
    {
        await using var kit = await UpdatesKit.CreateAsync(pg);
        var notMsi = new byte[1000];
        new Random(3).NextBytes(notMsi);
        notMsi[0] = (byte)'M';
        notMsi[1] = (byte)'Z';
        kit.Releases.Tagged.Add(kit.Releases.MakeRelease(AppVersion.Current, [(ReleaseAssetNames.AgentMsi(AppVersion.Current), notMsi)]));

        var bad = await kit.Cache.EnsureCurrentAsync(Ct);
        Assert.False(bad.Ok);
        Assert.Contains("not an MSI", bad.Error);
        Assert.Null(await kit.Cache.GetCurrentAsync(Ct));
        Assert.Empty(System.IO.Directory.GetFiles(Path.Combine(kit.Paths.AgentPackagesDirectory, AppVersion.Current)));

        kit.Releases.Tagged.Clear();
        kit.Releases.Tagged.Add(kit.Releases.MakeRelease(AppVersion.Current, [(ReleaseAssetNames.AgentMsi(AppVersion.Current), UpdatesKit.FakeMsi(10_000))]));
        kit.Cache.MaxBytes = 4096;
        var large = await kit.Cache.EnsureCurrentAsync(Ct);
        Assert.False(large.Ok);
        Assert.Contains("larger", large.Error);
        Assert.Null(await kit.Cache.GetCurrentAsync(Ct));
        Assert.Empty(System.IO.Directory.GetFiles(Path.Combine(kit.Paths.AgentPackagesDirectory, AppVersion.Current)));
    }

    [Fact]
    public async Task Test_seeding_uses_the_download_checks()
    {
        await using var kit = await UpdatesKit.CreateAsync(pg);
        var msi = UpdatesKit.FakeMsi(10_000);

        var wrongSha = await kit.Cache.StoreVerifiedAsync(new NonSeekableStream(msi), new string('0', 64), "test", Ct);
        Assert.False(wrongSha.Ok);
        Assert.Contains("SHA-256 mismatch", wrongSha.Error);
        Assert.Null(await kit.Cache.GetCurrentAsync(Ct));

        var ok = await kit.Cache.StoreVerifiedAsync(new NonSeekableStream(msi), Convert.ToHexStringLower(SHA256.HashData(msi)), "test", Ct);
        Assert.True(ok.Ok, ok.Error);
        var current = await kit.Cache.GetCurrentAsync(Ct);
        Assert.NotNull(current);
        Assert.StartsWith(kit.Paths.AgentPackagesDirectory, current.FilePath);
        await using var s = kit.Cache.OpenRead(current);
        using var ms = new MemoryStream();
        await s.CopyToAsync(ms, Ct);
        Assert.Equal(msi, ms.ToArray());
    }

    [Fact]
    public void Package_cache_has_no_import_or_upload_entry_point()
    {
        // 1.0.5: the MSI only comes from the release download; the Console cannot put a file into the cache.
        Assert.DoesNotContain(typeof(IAgentPackageCache).GetMethods(),
            m => m.Name.Contains("Import", StringComparison.OrdinalIgnoreCase) || m.Name.Contains("Upload", StringComparison.OrdinalIgnoreCase)
                 || m.GetParameters().Any(p => p.ParameterType == typeof(Stream)));
        Assert.DoesNotContain(typeof(AgentPackageCache).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance),
            m => m.GetParameters().Any(p => p.ParameterType == typeof(Stream)));
    }

    private sealed class NonSeekableStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
    }
}
