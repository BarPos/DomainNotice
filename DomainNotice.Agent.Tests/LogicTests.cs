using System.Security.Cryptography;
using System.Text;
using DomainNotice.Agent.Logic;
using DomainNotice.Shared;
using DomainNotice.Shared.LocalIpc;
using DomainNotice.Shared.Protocol;

namespace DomainNotice.Agent.Tests;

public sealed class BackoffTests
{
    [Fact]
    public void Doubles_up_to_the_cap_and_jitter_only_shortens()
    {
        var backoff = new ExponentialBackoff(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30), 0.25, new Random(42));
        var delays = Enumerable.Range(0, 10).Select(_ => backoff.Next()).ToList();

        double[] nominal = [1, 2, 4, 8, 16, 30, 30, 30, 30, 30];
        for (var i = 0; i < delays.Count; i++)
        {
            Assert.InRange(delays[i].TotalSeconds, nominal[i] * 0.75 - 0.001, nominal[i] + 0.001);
        }

        backoff.Reset();
        Assert.InRange(backoff.Next().TotalSeconds, 0.75, 1.0);
    }

    [Fact]
    public void Survives_many_attempts_without_overflow()
    {
        var backoff = new ExponentialBackoff(TimeSpan.FromSeconds(5), TimeSpan.FromHours(1));
        for (var i = 0; i < 1000; i++) Assert.True(backoff.Next() <= TimeSpan.FromHours(1));
    }
}

public sealed class PackageDownloadTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dn-agent-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (System.IO.Directory.Exists(_dir)) System.IO.Directory.Delete(_dir, true);
    }

    private static (UpdateAgent Command, byte[] Data) MakePackage(int size = 600_000)
    {
        var data = new byte[size];
        new Random(7).NextBytes(data);
        return (new UpdateAgent
        {
            TaskId = Guid.NewGuid(),
            Version = "1.2.0",
            Size = size,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(data)),
        }, data);
    }

    private static PackageChunk Chunk(Guid id, byte[] data, long offset, int length) =>
        new(id, offset, data.AsMemory((int)offset, length));

    [Fact]
    public void Assembles_in_order_and_verifies()
    {
        var (cmd, data) = MakePackage();
        using var download = PackageDownload.OpenOrResume(_dir, cmd);
        Assert.Equal(0, download.Offset);

        long offset = 0;
        ChunkResult result = ChunkResult.Accepted;
        while (offset < data.Length)
        {
            var len = (int)Math.Min(ProtocolInfo.ChunkSize, data.Length - offset);
            result = download.Append(Chunk(cmd.TaskId, data, offset, len));
            offset += len;
        }

        Assert.Equal(ChunkResult.Complete, result);
        var verify = download.Verify();
        Assert.True(verify.Ok, verify.Error);
        Assert.True(File.Exists(download.PackagePath));
        Assert.False(File.Exists(download.PartPath));
        Assert.Equal(data, File.ReadAllBytes(download.PackagePath));
    }

    [Fact]
    public void Rejects_wrong_transfer_out_of_order_and_oversized_chunks()
    {
        var (cmd, data) = MakePackage(1000);
        using var download = PackageDownload.OpenOrResume(_dir, cmd);

        Assert.Equal(ChunkResult.WrongTransfer, download.Append(Chunk(Guid.NewGuid(), data, 0, 100)));
        Assert.Equal(ChunkResult.OutOfOrder, download.Append(Chunk(cmd.TaskId, data, 100, 100)));
        Assert.Equal(ChunkResult.Accepted, download.Append(Chunk(cmd.TaskId, data, 0, 100)));
        Assert.Equal(ChunkResult.OutOfOrder, download.Append(Chunk(cmd.TaskId, data, 0, 100))); // duplicate
        var tooBig = new PackageChunk(cmd.TaskId, 100, new byte[1000]);
        Assert.Equal(ChunkResult.TooLarge, download.Append(tooBig));
        Assert.Equal(100, download.Offset);
    }

    [Fact]
    public void Resumes_from_the_bytes_already_on_disk()
    {
        var (cmd, data) = MakePackage(300_000);
        using (var first = PackageDownload.OpenOrResume(_dir, cmd))
        {
            first.Append(Chunk(cmd.TaskId, data, 0, 262_144));
        } // connection dropped / service restarted

        using var resumed = PackageDownload.OpenOrResume(_dir, cmd);
        Assert.Equal(262_144, resumed.Offset);
        Assert.Equal(ChunkResult.Complete, resumed.Append(Chunk(cmd.TaskId, data, 262_144, data.Length - 262_144)));
        Assert.True(resumed.Verify().Ok);
    }

    [Fact]
    public void Hash_mismatch_fails_and_deletes_the_file()
    {
        var (cmd, data) = MakePackage(1000);
        cmd = cmd with { Sha256 = new string('0', 64) };
        using var download = PackageDownload.OpenOrResume(_dir, cmd);
        download.Append(Chunk(cmd.TaskId, data, 0, data.Length));

        var verify = download.Verify();
        Assert.False(verify.Ok);
        Assert.Contains("SHA-256", verify.Error);
        Assert.False(File.Exists(download.PartPath));
        Assert.False(File.Exists(download.PackagePath));
    }

    [Fact]
    public void Oversized_leftover_is_truncated()
    {
        var (cmd, _) = MakePackage(1000);
        System.IO.Directory.CreateDirectory(_dir);
        File.WriteAllBytes(Path.Combine(_dir, cmd.TaskId.ToString("D") + ".part"), new byte[5000]);
        using var download = PackageDownload.OpenOrResume(_dir, cmd);
        Assert.Equal(0, download.Offset);
    }

    [Fact]
    public void CleanFolder_keeps_only_the_current_task()
    {
        System.IO.Directory.CreateDirectory(_dir);
        var keep = Guid.NewGuid();
        var other = Guid.NewGuid();
        File.WriteAllText(Path.Combine(_dir, keep + ".part"), "x");
        File.WriteAllText(Path.Combine(_dir, other + ".part"), "x");
        File.WriteAllText(Path.Combine(_dir, other + ".msi"), "x");
        File.WriteAllText(Path.Combine(_dir, other + ".log"), "x");

        PackageDownload.CleanFolder(_dir, keep, TimeSpan.FromDays(7), DateTime.UtcNow);

        Assert.True(File.Exists(Path.Combine(_dir, keep + ".part")));
        Assert.False(File.Exists(Path.Combine(_dir, other + ".part")));
        Assert.False(File.Exists(Path.Combine(_dir, other + ".msi")));
        Assert.True(File.Exists(Path.Combine(_dir, other + ".log"))); // logs only go when old
    }

    [Theory]
    [InlineData("1.1.0", "1.0.0", UpdateDecision.Install)]
    [InlineData("1.0.0", "1.0.0", UpdateDecision.AlreadyCurrent)]
    [InlineData("0.9.0", "1.0.0", UpdateDecision.Older)]
    [InlineData("garbage", "1.0.0", UpdateDecision.Invalid)]
    public void Update_policy(string target, string current, UpdateDecision expected) =>
        Assert.Equal(expected, UpdatePolicy.Decide(target, current));

    [Fact]
    public void Validate_rejects_bad_commands()
    {
        var (cmd, _) = MakePackage(10);
        Assert.Null(UpdatePolicy.Validate(cmd));
        Assert.NotNull(UpdatePolicy.Validate(cmd with { Sha256 = "abc" }));
        Assert.NotNull(UpdatePolicy.Validate(cmd with { Size = 0 }));
        Assert.NotNull(UpdatePolicy.Validate(cmd with { TaskId = Guid.Empty }));
    }
}

public sealed class InstallMarkerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dn-agent-marker-" + Guid.NewGuid().ToString("N"));

    public InstallMarkerTests() => System.IO.Directory.CreateDirectory(_dir);

    public void Dispose() => System.IO.Directory.Delete(_dir, true);

    [Fact]
    public void Round_trips_and_judges_success_by_running_version()
    {
        var path = Path.Combine(_dir, InstallMarker.FileName);
        var marker = new InstallMarker(Guid.NewGuid(), "1.2.0", @"C:\ProgramData\DomainNotice\updates\x.log", new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        marker.Write(path);

        var read = InstallMarker.TryRead(path);
        Assert.Equal(marker, read);
        Assert.True(read!.Succeeded("1.2.0"));
        Assert.True(read.Succeeded("1.3.0"));
        Assert.False(read.Succeeded("1.1.9"));
    }

    [Fact]
    public void Missing_or_broken_marker_reads_as_null()
    {
        Assert.Null(InstallMarker.TryRead(Path.Combine(_dir, "none.txt")));
        var path = Path.Combine(_dir, "broken.txt");
        File.WriteAllText(path, "not a marker");
        Assert.Null(InstallMarker.TryRead(path));
    }

    [Fact]
    public void Log_tail_reads_utf16_and_ansi_logs()
    {
        var lines = string.Join("\r\n", Enumerable.Range(1, 2000).Select(i => $"MSI (s) line {i}")) + "\r\nMainEngineThread is returning 1603\r\n";

        var unicodePath = Path.Combine(_dir, "u.log");
        File.WriteAllText(unicodePath, lines, Encoding.Unicode); // with BOM, like msiexec /l*v
        var tail = MsiLogTail.Read(unicodePath, 200)!;
        Assert.EndsWith("returning 1603", tail);
        Assert.True(tail.Length <= 200);
        Assert.DoesNotContain('\0', tail);

        var ansiPath = Path.Combine(_dir, "a.log");
        File.WriteAllText(ansiPath, lines, new UTF8Encoding(false));
        Assert.EndsWith("returning 1603", MsiLogTail.Read(ansiPath, 200)!);

        var small = Path.Combine(_dir, "s.log");
        File.WriteAllText(small, "short", Encoding.Unicode);
        Assert.Equal("short", MsiLogTail.Read(small));
    }
}

public sealed class SessionSnapshotTests
{
    private static SessionInfo S(int id, string user, SessionState state = SessionState.Active) =>
        new() { SessionId = id, UserName = user, State = state, LogonTimeUtc = new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc) };

    [Fact]
    public void Diff_detects_logon_logoff_lock_and_reused_ids()
    {
        List<SessionInfo> before = [S(1, @"CORP\alice"), S(2, @"CORP\bob")];
        List<SessionInfo> after = [S(1, @"corp\ALICE", SessionState.Locked), S(2, @"CORP\carol"), S(3, @"CORP\dave")];

        var diff = SessionSnapshot.Diff(before, after);

        Assert.Equal([1], diff.StateChanged.Select(s => s.SessionId));
        Assert.Equal([2, 3], diff.Added.Select(s => s.SessionId).Order());
        Assert.Equal(@"CORP\bob", Assert.Single(diff.Removed).UserName);
        Assert.True(SessionSnapshot.Diff(after, after).IsEmpty);
    }

    [Fact]
    public void UsersGone_counts_only_users_without_any_session()
    {
        List<SessionInfo> before = [S(1, @"CORP\alice"), S(2, @"CORP\alice"), S(3, @"CORP\bob")];
        List<SessionInfo> after = [S(2, @"CORP\Alice")];
        Assert.Equal([@"CORP\bob"], SessionSnapshot.UsersGone(before, after));
    }

    [Theory]
    [InlineData("CORP", "alice", @"CORP\alice")]
    [InlineData("", "alice", "alice")]
    [InlineData("CORP", "", null)]
    public void FormatUser(string domain, string user, string? expected) =>
        Assert.Equal(expected, SessionSnapshot.FormatUser(domain, user));

    [Fact]
    public void Os_version_text_fixes_the_windows_11_product_name()
    {
        Assert.Equal("Windows 11 Pro 23H2 (10.0.22631.4317)",
            OsVersionText.Format("Windows 10 Pro", "23H2", "22631", 4317, new Version(10, 0, 22631)));
        Assert.Equal("Windows 10 Enterprise 22H2 (10.0.19045.1)",
            OsVersionText.Format("Windows 10 Enterprise", "22H2", "19045", 1, new Version(10, 0, 19045)));
        Assert.Equal("Windows (10.0.20348)", OsVersionText.Format(null, null, null, null, new Version(10, 0, 20348)));
    }
}

public sealed class LaunchThrottleTests
{
    [Fact]
    public void Restarts_quickly_after_a_long_run_and_slows_down_in_a_crash_loop()
    {
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var throttle = new LaunchThrottle();
        Assert.True(throttle.CanStart(t0));

        throttle.Started(t0);
        throttle.Exited(t0.AddMinutes(10)); // killed in Task Manager after a while
        Assert.Equal(0, throttle.QuickExits);
        Assert.False(throttle.CanStart(t0.AddMinutes(10).AddSeconds(1)));
        Assert.True(throttle.CanStart(t0.AddMinutes(10).AddSeconds(2)));

        var now = t0.AddHours(1);
        TimeSpan lastDelay = TimeSpan.Zero;
        for (var i = 0; i < 10; i++)
        {
            throttle.Started(now);
            now = now.AddSeconds(1);
            throttle.Exited(now);
            lastDelay = throttle.NextAllowedUtc - now;
            now = throttle.NextAllowedUtc;
        }

        Assert.Equal(LaunchThrottle.MaxDelay, lastDelay);
    }
}

public sealed class LocalIpcTests
{
    [Fact]
    public async Task Messages_round_trip_over_a_stream()
    {
        var stream = new MemoryStream();
        var writer = new LocalMessageStream(stream);
        var show = new ShowAnnouncement
        {
            DeliveryId = Guid.NewGuid(),
            Announcement = new AnnouncementPayload { Title = "T", UnlockMode = UnlockMode.AfterTimeAndRequired, MinDismissSeconds = 5 },
        };
        await writer.SendAsync(show);
        await writer.SendAsync(new ConfirmRequest { DeliveryId = show.DeliveryId, ButtonId = Guid.NewGuid(), Answers = [new AnswerItem { QuestionId = Guid.NewGuid(), OptionIds = [Guid.NewGuid()] }] });
        await writer.SendAsync(new RequestPending());

        stream.Position = 0;
        var reader = new LocalMessageStream(stream);
        var first = Assert.IsType<ShowAnnouncement>((await reader.ReadAsync())!.Message);
        Assert.Equal(show.DeliveryId, first.DeliveryId);
        Assert.Equal(UnlockMode.AfterTimeAndRequired, first.Announcement.UnlockMode);
        var confirm = Assert.IsType<ConfirmRequest>((await reader.ReadAsync())!.Message);
        Assert.Single(confirm.Answers);
        Assert.IsType<RequestPending>((await reader.ReadAsync())!.Message);
        Assert.Null(await reader.ReadAsync());
    }

    [Fact]
    public void Unknown_type_decodes_to_null()
    {
        var body = Encoding.UTF8.GetBytes("""{"type":"FromTheFuture","payload":{}}""");
        Assert.Null(LocalIpcCodec.Decode(body));
        Assert.Throws<ProtocolException>(() => LocalIpcCodec.Decode(Encoding.UTF8.GetBytes("{")));
    }

    [Fact]
    public void Pipe_name_is_per_session() =>
        Assert.Equal("DomainNotice.Agent.Session.3", LocalPipe.NameFor(3));
}
