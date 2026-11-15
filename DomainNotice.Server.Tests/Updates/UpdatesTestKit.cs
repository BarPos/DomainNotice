using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DomainNotice.Server.Abstractions;
using DomainNotice.Server.Common;
using DomainNotice.Server.Data.Entities;
using DomainNotice.Server.Options;
using DomainNotice.Server.Tests.Infrastructure;
using DomainNotice.Server.Updates;
using DomainNotice.Shared;
using DomainNotice.Shared.Protocol;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DomainNotice.Server.Tests.Updates;

/// <summary>Builds the Updates services by hand over a fresh database and a temp data folder, with fakes.</summary>
internal sealed class UpdatesKit : IAsyncDisposable
{
    public required TestDatabase Db { get; init; }
    public required string DataDir { get; init; }
    public ManualTime Time { get; } = new(new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero));
    public FakeReleaseSource Releases { get; } = new();
    public FakeInstallation Installation { get; } = new();
    public FakeHub Hub { get; } = new();
    public PathsOptions Paths { get; private init; } = null!;
    public UpdatesOptions UpdatesOptions { get; } = new() { GitHubRepository = "example/domainnotice" };
    public AgentsOptions AgentsOptions { get; } = new();

    private ISettingsStore? _settings;
    private IAuditLog? _audit;
    private AgentPackageCache? _cache;
    private ServerUpdateService? _server;
    private PackageTransferManager? _transfers;
    private AgentUpdateService? _agentUpdates;
    private AgentUpdateCoordinator? _coordinator;

    public static async Task<UpdatesKit> CreateAsync(PostgresFixture pg)
    {
        var dir = Path.Combine(Path.GetTempPath(), "dn-updates-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        return new UpdatesKit
        {
            Db = await pg.CreateDatabaseAsync(),
            DataDir = dir,
            Paths = new PathsOptions { Data = dir, Backups = Path.Combine(dir, "backups") },
        };
    }

    public ISettingsStore Settings => _settings ??= new SettingsStore(Db, new EphemeralDataProtectionProvider(), Time);
    public IAuditLog Audit => _audit ??= new AuditLog(Db, Time, NullLogger<AuditLog>.Instance);

    public AgentPackageCache Cache => _cache ??=
        new AgentPackageCache(Microsoft.Extensions.Options.Options.Create(Paths), Releases, Audit, Time, NullLogger<AgentPackageCache>.Instance);

    public ServerUpdateService Server => _server ??= new ServerUpdateService(Releases, Installation, Cache, Settings, Audit, Db,
        Microsoft.Extensions.Options.Options.Create(Paths), Time, NullLogger<ServerUpdateService>.Instance);

    public PackageTransferManager Transfers => _transfers ??= new PackageTransferManager(
        Microsoft.Extensions.Options.Options.Create(AgentsOptions), Db, Time, NullLogger<PackageTransferManager>.Instance);

    public AgentUpdateService AgentUpdates => _agentUpdates ??=
        new AgentUpdateService(Db, Cache, Transfers, Hub, Audit, Time, NullLogger<AgentUpdateService>.Instance);

    public AgentUpdateCoordinator Coordinator => _coordinator ??=
        new AgentUpdateCoordinator(Db, Cache, Transfers, Time, NullLogger<AgentUpdateCoordinator>.Instance);

    public AgentUpdateTimeoutService TimeoutJob() =>
        new(Db, Transfers, Microsoft.Extensions.Options.Options.Create(AgentsOptions), Time, NullLogger<AgentUpdateTimeoutService>.Instance);

    public UpdatesBackgroundService Background() => new(Server, Cache, AgentUpdates, Releases, Settings,
        Microsoft.Extensions.Options.Options.Create(UpdatesOptions), Microsoft.Extensions.Options.Options.Create(AgentsOptions), Time,
        NullLogger<UpdatesBackgroundService>.Instance);

    public async Task<Guid> AddComputerAsync(string agentVersion, bool revoked = false)
    {
        var id = Guid.NewGuid();
        await using var ctx = Db.CreateContext();
        ctx.Computers.Add(new Computer
        {
            Id = id,
            Name = "PC-" + id.ToString("N")[..6],
            AgentKeyFingerprint = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)),
            EnrolledAtUtc = Time.GetUtcNow().UtcDateTime,
            AgentVersion = agentVersion,
            RevokedAtUtc = revoked ? Time.GetUtcNow().UtcDateTime : null,
        });
        await ctx.SaveChangesAsync();
        return id;
    }

    public async Task<AgentUpdateTask> AddTaskAsync(Guid computerId, AgentUpdateState state, DateTime? sentAt = null)
    {
        var now = Time.GetUtcNow().UtcDateTime;
        var task = new AgentUpdateTask
        {
            Id = Guid.NewGuid(), ComputerId = computerId, FromVersion = "0.9.0", ToVersion = AppVersion.Current,
            RequestedBy = "test", State = state, CreatedAtUtc = now, UpdatedAtUtc = now, SentAtUtc = sentAt,
        };
        await using var ctx = Db.CreateContext();
        ctx.AgentUpdateTasks.Add(task);
        await ctx.SaveChangesAsync();
        return task;
    }

    public async Task<AgentUpdateTask> GetTaskAsync(Guid id)
    {
        await using var ctx = Db.CreateContext();
        return ctx.AgentUpdateTasks.Single(t => t.Id == id);
    }

    /// <summary>Imports an MSI-looking file of <paramref name="size"/> bytes as the package for the server's version.</summary>
    public async Task<(AgentPackageInfo Info, byte[] Bytes)> SeedPackageAsync(int size = 600_000)
    {
        var bytes = FakeMsi(size);
        var r = await Cache.ImportAsync(new MemoryStream(bytes), "DomainNotice.Agent.msi", "test");
        Assert.True(r.Ok, r.Error);
        return ((await Cache.GetCurrentAsync())!, bytes);
    }

    public static byte[] FakeMsi(int size, int seed = 1)
    {
        var bytes = new byte[size];
        new Random(seed).NextBytes(bytes);
        AgentPackageCache.OleSignature.CopyTo(bytes);
        return bytes;
    }

    /// <summary>Writes a cache folder for <paramref name="version"/> directly (as an older server would have left it).</summary>
    public void WriteCachedVersion(string version, byte[]? bytes = null)
    {
        bytes ??= FakeMsi(1000, seed: version.GetHashCode());
        var dir = Path.Combine(Paths.AgentPackagesDirectory, version);
        System.IO.Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, ReleaseAssetNames.AgentMsi(version)), bytes);
        File.WriteAllText(Path.Combine(dir, AgentPackageCache.MetadataFileName), JsonSerializer.Serialize(new
        {
            Version = version, Size = (long)bytes.Length, Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)),
            AddedAtUtc = DateTime.UtcNow, Source = "test",
        }));
    }

    public async ValueTask DisposeAsync()
    {
        await Transfers.WhenIdleAsync();
        _transfers?.Dispose();
        try { System.IO.Directory.Delete(DataDir, recursive: true); } catch (IOException) { }
    }
}

internal sealed class ManualTime(DateTimeOffset start) : TimeProvider
{
    private long _ticks = start.UtcTicks;
    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
    public override long GetTimestamp() => Interlocked.Read(ref _ticks);
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
}

internal sealed class FakeInstallation : IServerInstallation
{
    public string? Architecture { get; set; } = "x64";
    public bool SelfUpdateSupported { get; set; } = true;
    public string? UnsupportedReason { get; set; }
}

internal sealed class FakeReleaseSource : IReleaseSource
{
    private readonly Dictionary<string, byte[]> _content = new();
    public bool IsConfigured { get; set; } = true;
    public Release? Latest { get; set; }
    public List<Release> Tagged { get; } = [];
    public ConcurrentQueue<string> Calls { get; } = new();

    public Task<Release?> GetLatestAsync(CancellationToken ct = default)
    {
        Calls.Enqueue("latest");
        return Task.FromResult(Latest);
    }

    public Task<Release?> GetByVersionAsync(string version, CancellationToken ct = default)
    {
        Calls.Enqueue("tag:" + version);
        return Task.FromResult(Tagged.FirstOrDefault(r => r.Info.TagName == "v" + version));
    }

    public Task<Stream> OpenAssetAsync(ReleaseAsset asset, CancellationToken ct = default)
    {
        Calls.Enqueue("asset:" + asset.Name);
        return Task.FromResult<Stream>(new MemoryStream(_content[asset.DownloadUrl], writable: false));
    }

    /// <summary>Builds a release with the given files and a SHA256SUMS.txt (optionally with wrong hashes).</summary>
    public Release MakeRelease(string version, IEnumerable<(string Name, byte[] Bytes)> files, bool corruptSums = false,
        bool prerelease = false)
    {
        var list = files.ToList();
        var sums = new StringBuilder();
        foreach (var (name, bytes) in list)
        {
            var hash = Convert.ToHexStringLower(SHA256.HashData(corruptSums ? [.. bytes, 1] : bytes));
            sums.Append(hash).Append("  ").Append(name).Append('\n');
        }
        list.Add((ReleaseAssetNames.Sums, Encoding.UTF8.GetBytes(sums.ToString())));
        var assets = new List<ReleaseAsset>();
        foreach (var (name, bytes) in list)
        {
            var url = $"https://fake.invalid/v{version}/{name}";
            _content[url] = bytes;
            assets.Add(new ReleaseAsset(name, bytes.Length, url));
        }
        return new Release(new ReleaseInfo(version, "v" + version, "DomainNotice " + version, "Notes", DateTime.UtcNow, prerelease, null), assets);
    }
}

internal sealed class FakeConnection(Guid computerId, string agentVersion) : IAgentConnection
{
    public Guid ComputerId { get; } = computerId;
    public string AgentVersion { get; } = agentVersion;
    public int ProtocolVersion => ProtocolInfo.Version;
    public IPEndPoint RemoteEndPoint { get; } = new(IPAddress.Loopback, 50000);
    public byte[]? ChannelBinding => null;
    public DateTime ConnectedAtUtc { get; } = DateTime.UtcNow;
    public IReadOnlyList<SessionInfo> Sessions { get; } = [];

    public ConcurrentQueue<object> Sent { get; } = new();
    public ConcurrentQueue<(Guid TransferId, long Offset, byte[] Data)> Chunks { get; } = new();
    /// <summary>Awaited before each chunk is "sent" (lets tests hold transfers open).</summary>
    public Func<Task>? BeforeChunk { get; set; }

    public Task SendAsync<T>(T message, Guid? replyTo = null, CancellationToken ct = default) where T : IAgentMessage
    {
        Sent.Enqueue(message);
        return Task.CompletedTask;
    }

    public async Task SendChunkAsync(Guid transferId, long offset, ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        if (BeforeChunk is { } gate) await gate().WaitAsync(ct);
        Chunks.Enqueue((transferId, offset, data.ToArray()));
    }

    public Task CloseAsync(string? refusedReason = null, string? message = null) => Task.CompletedTask;

    public byte[] Received(Guid transferId) =>
        Chunks.Where(c => c.TransferId == transferId).OrderBy(c => c.Offset).SelectMany(c => c.Data).ToArray();
}

internal sealed class FakeHub : IAgentHub
{
    public ConcurrentDictionary<Guid, FakeConnection> Connections { get; } = new();
    public bool IsOnline(Guid computerId) => Connections.ContainsKey(computerId);
    public IReadOnlyCollection<Guid> OnlineComputerIds => Connections.Keys.ToList();
    public IAgentConnection? Get(Guid computerId) => Connections.GetValueOrDefault(computerId);

    public async Task<bool> TrySendAsync<T>(Guid computerId, T message, CancellationToken ct = default) where T : IAgentMessage
    {
        if (!Connections.TryGetValue(computerId, out var c)) return false;
        await c.SendAsync(message, ct: ct);
        return true;
    }

    public Task DisconnectAsync(Guid computerId, string refusedReason, string? message = null)
    {
        Connections.TryRemove(computerId, out _);
        return Task.CompletedTask;
    }
}

/// <summary>Records requests; answers with <see cref="Responder"/> or fails the test when <see cref="FailOnRequest"/>.</summary>
internal sealed class RecordingHandler : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];
    public bool FailOnRequest { get; set; }
    public Func<HttpRequestMessage, HttpResponseMessage> Responder { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.NotFound);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (Requests) Requests.Add(request);
        if (FailOnRequest) throw new InvalidOperationException($"Unexpected network call: {request.Method} {request.RequestUri}");
        return Task.FromResult(Responder(request));
    }
}

internal sealed class RecordingHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public int Created;
    public HttpClient CreateClient(string name)
    {
        Interlocked.Increment(ref Created);
        return new HttpClient(handler, disposeHandler: false);
    }
}
