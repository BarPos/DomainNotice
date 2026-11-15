using System.Security.Cryptography;
using DomainNotice.Server.Abstractions;
using DomainNotice.Server.Data.Entities;
using DomainNotice.Server.Tests.Infrastructure;
using DomainNotice.Server.Updates;
using DomainNotice.Shared;
using Microsoft.EntityFrameworkCore;

namespace DomainNotice.Server.Tests.Updates;

public class ServerUpdateServiceTests(PostgresFixture pg)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (string Name, byte[] Bytes) Tarball(string version, string arch = "x64") =>
        (ReleaseAssetNames.ServerTarball(version, arch), RandomNumberGenerator.GetBytes(50_000));

    [Fact]
    public async Task Update_now_stages_package_and_writes_request_file()
    {
        await using var kit = await UpdatesKit.CreateAsync(pg);
        kit.Installation.Architecture = "arm64";
        var x64 = Tarball("1.1.0");
        var arm = Tarball("1.1.0", "arm64");
        kit.Releases.Latest = kit.Releases.MakeRelease("1.1.0", [x64, arm]);

        var result = await kit.Server.UpdateNowAsync("alice", Ct);

        Assert.True(result.Ok, result.Error);
        var dir = Path.Combine(kit.Paths.UpdatesDirectory, "1.1.0");
        Assert.Equal(arm.Bytes, await File.ReadAllBytesAsync(Path.Combine(dir, arm.Name), Ct)); // picked by architecture
        Assert.False(File.Exists(Path.Combine(dir, x64.Name)));
        Assert.True(File.Exists(Path.Combine(dir, "SHA256SUMS.txt")));
        Assert.Empty(System.IO.Directory.GetFiles(dir, "*.partial"));

        var request = UpdateRequestFile.ParseKeyValues(
            await File.ReadAllTextAsync(Path.Combine(kit.Paths.UpdatesDirectory, "request.conf"), Ct));
        Assert.Equal("1", request["format"]);
        Assert.Equal("1.1.0", request["version"]);
        Assert.Equal(AppVersion.Current, request["from_version"]);
        Assert.Equal("arm64", request["arch"]);
        Assert.Equal(arm.Name, request["file"]);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(arm.Bytes)), request["sha256"]);

        await using var db = kit.Db.CreateContext();
        var run = await db.ServerUpdateRuns.SingleAsync(Ct);
        Assert.Equal(Guid.Parse(request["run_id"]), run.Id);
        Assert.Equal(ServerUpdateState.Staged, run.State);
        Assert.Equal("alice", run.RequestedBy);
        Assert.Equal("1.1.0", run.ToVersion);
        Assert.Contains(await db.AuditLog.ToListAsync(Ct), a => a.Action == AuditActions.ServerUpdateRequested && a.Actor == "alice");

        // A second request while one is staged is refused.
        Assert.False((await kit.Server.UpdateNowAsync("alice", Ct)).Ok);
    }

    [Fact]
    public async Task Sha_mismatch_is_rejected_and_nothing_is_staged()
    {
        await using var kit = await UpdatesKit.CreateAsync(pg);
        kit.Releases.Latest = kit.Releases.MakeRelease("1.1.0", [Tarball("1.1.0")], corruptSums: true);

        var result = await kit.Server.UpdateNowAsync("alice", Ct);

        Assert.False(result.Ok);
        Assert.Contains("SHA-256 mismatch", result.Error);
        Assert.False(File.Exists(Path.Combine(kit.Paths.UpdatesDirectory, "request.conf")));
        Assert.False(System.IO.Directory.Exists(Path.Combine(kit.Paths.UpdatesDirectory, "1.1.0")));
        await using var db = kit.Db.CreateContext();
        Assert.Equal(ServerUpdateState.Failed, (await db.ServerUpdateRuns.SingleAsync(Ct)).State);
    }

    [Theory]
    [InlineData("1.0.0")]      // same as running
    [InlineData("0.9.0")]      // older
    [InlineData("1.1.0-beta")] // not plain
    [InlineData("1.1")]        // not a triplet
    [InlineData("1.1.0.1")]
    public async Task Update_now_refuses_bad_or_not_newer_versions(string version)
    {
        await using var kit = await UpdatesKit.CreateAsync(pg);
        kit.Releases.Latest = kit.Releases.MakeRelease(version, [Tarball(version)]);

        var result = await kit.Server.UpdateNowAsync("alice", Ct);

        Assert.False(result.Ok);
        Assert.DoesNotContain(kit.Releases.Calls, c => c.StartsWith("asset:"));
        Assert.False(File.Exists(Path.Combine(kit.Paths.UpdatesDirectory, "request.conf")));
        await using var db = kit.Db.CreateContext();
        Assert.Empty(await db.ServerUpdateRuns.ToListAsync(Ct));
    }

    [Fact]
    public async Task Update_now_refuses_when_not_configured_or_unsupported()
    {
        await using var kit = await UpdatesKit.CreateAsync(pg);
        kit.Releases.Latest = kit.Releases.MakeRelease("1.1.0", [Tarball("1.1.0")]);

        kit.Releases.IsConfigured = false;
        var r1 = await kit.Server.UpdateNowAsync("alice", Ct);
        Assert.False(r1.Ok);
        Assert.Contains("not configured", r1.Error);

        kit.Releases.IsConfigured = true;
        kit.Installation.SelfUpdateSupported = false;
        kit.Installation.UnsupportedReason = "Not installed with install.sh.";
        var r2 = await kit.Server.UpdateNowAsync("alice", Ct);
        Assert.False(r2.Ok);
        Assert.Equal("Not installed with install.sh.", r2.Error);
        Assert.Empty(kit.Releases.Calls);

        var status = await kit.Server.GetStatusAsync(Ct);
        Assert.False(status.SelfUpdateSupported);
    }

    [Fact]
    public async Task Check_now_stores_last_check_and_reports_update()
    {
        await using var kit = await UpdatesKit.CreateAsync(pg);
        kit.Releases.Latest = kit.Releases.MakeRelease("1.4.0", [Tarball("1.4.0")]);

        var status = await kit.Server.CheckNowAsync("alice", Ct);

        Assert.True(status.Configured);
        Assert.True(status.UpdateAvailable);
        Assert.Equal("1.4.0", status.Latest?.Version);
        Assert.Equal(kit.Time.GetUtcNow().UtcDateTime, status.LastCheckUtc);
        Assert.NotNull(await kit.Settings.GetAsync(SettingKeys.UpdatesLastCheck, Ct));
        // Check now also tries to fetch the agent MSI for the server's own version, by tag.
        Assert.Contains("tag:" + AppVersion.Current, kit.Releases.Calls);

        kit.Releases.Latest = kit.Releases.MakeRelease("1.4.0-rc1", []);
        Assert.False((await kit.Server.CheckNowAsync("alice", Ct)).UpdateAvailable);
    }

    [Theory]
    [InlineData("Succeeded", ServerUpdateState.Succeeded)]
    [InlineData("RolledBack", ServerUpdateState.RolledBack)]
    [InlineData("Failed", ServerUpdateState.Failed)]
    public async Task Startup_reads_result_file_and_finishes_run(string state, ServerUpdateState expected)
    {
        await using var kit = await UpdatesKit.CreateAsync(pg);
        var runId = Guid.NewGuid();
        await using (var db = kit.Db.CreateContext())
        {
            db.ServerUpdateRuns.Add(new ServerUpdateRun
            {
                Id = runId, FromVersion = "0.9.0", ToVersion = "1.1.0", RequestedBy = "alice",
                State = ServerUpdateState.Staged, RequestedAtUtc = kit.Time.GetUtcNow().UtcDateTime,
            });
            await db.SaveChangesAsync(Ct);
        }
        System.IO.Directory.CreateDirectory(Path.Combine(kit.Paths.UpdatesDirectory, "1.1.0"));
        await File.WriteAllTextAsync(Path.Combine(kit.Paths.UpdatesDirectory, "result.conf"),
            $"format=1\nrun_id={runId}\nversion=1.1.0\nfrom_version=0.9.0\nstate={state}\nmessage=Health check ok\nfinished_at=2026-11-15T12:05:00Z\n", Ct);

        await kit.Background().RunStartupAsync(Ct);

        await using (var db = kit.Db.CreateContext())
        {
            var run = await db.ServerUpdateRuns.SingleAsync(Ct);
            Assert.Equal(expected, run.State);
            Assert.Equal("Health check ok", run.Message);
            Assert.Equal(new DateTime(2026, 11, 15, 12, 5, 0, DateTimeKind.Utc), run.FinishedAtUtc);
        }
        Assert.False(File.Exists(Path.Combine(kit.Paths.UpdatesDirectory, "result.conf")));
        Assert.False(System.IO.Directory.Exists(Path.Combine(kit.Paths.UpdatesDirectory, "1.1.0")));
    }

    [Fact]
    public async Task Staged_run_for_running_version_succeeds_without_result_file()
    {
        await using var kit = await UpdatesKit.CreateAsync(pg);
        await using (var db = kit.Db.CreateContext())
        {
            db.ServerUpdateRuns.Add(new ServerUpdateRun
            {
                Id = Guid.NewGuid(), FromVersion = "0.9.0", ToVersion = AppVersion.Current, RequestedBy = "alice",
                State = ServerUpdateState.Staged, RequestedAtUtc = kit.Time.GetUtcNow().UtcDateTime,
            });
            await db.SaveChangesAsync(Ct);
        }

        var status = await kit.Server.GetStatusAsync(Ct);

        Assert.Equal(ServerUpdateState.Succeeded, status.LastRun?.State);
    }
}
