using System.Collections.Concurrent;
using DomainNotice.Server.Abstractions;
using DomainNotice.Server.Data;
using DomainNotice.Server.Options;
using DomainNotice.Shared;
using DomainNotice.Shared.Protocol;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DomainNotice.Server.Updates;

/// <summary>
/// Streams agent MSIs over the agent link in binary chunks (plan 11.3). Transfers run in the background, so the
/// connection's read loop keeps going (pings, UpdateStatus, a resume request). One transfer per task: a new
/// PackageRequest for the same task replaces the old stream. At most <c>Agents:MaxParallelUpdates</c> transfers send
/// at once; the rest wait for a slot. <c>Agents:UpdateBandwidthMbps</c> &gt; 0 caps the total rate.
/// </summary>
internal sealed class PackageTransferManager : IDisposable
{
    /// <summary>BytesSent is written to the database every this many chunks (and at the end).</summary>
    private const int ProgressEveryChunks = 16;

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly TimeProvider _time;
    private readonly ILogger<PackageTransferManager> _logger;
    private readonly SemaphoreSlim _slots;
    private readonly BandwidthLimiter? _limiter;
    private readonly int _chunkSize;
    private readonly ConcurrentDictionary<Guid, Transfer> _active = new();
    private readonly CancellationTokenSource _shutdown = new();
    private int _sending;
    private int _maxObserved;

    public PackageTransferManager(IOptions<AgentsOptions> options, IDbContextFactory<AppDbContext> dbFactory, TimeProvider time,
        ILogger<PackageTransferManager> logger)
    {
        _dbFactory = dbFactory;
        _time = time;
        _logger = logger;
        var o = options.Value;
        MaxParallel = Math.Max(1, o.MaxParallelUpdates);
        _slots = new SemaphoreSlim(MaxParallel, MaxParallel);
        if (o.UpdateBandwidthMbps > 0) _limiter = new BandwidthLimiter(o.UpdateBandwidthMbps * 1_000_000L / 8, time);
        var maxFrame = o.MaxMessageBytes > 0 ? o.MaxMessageBytes : ProtocolInfo.DefaultMaxFrameBytes;
        _chunkSize = Math.Max(1, Math.Min(ProtocolInfo.ChunkSize, maxFrame - PackageChunk.HeaderSize));
    }

    public int MaxParallel { get; }
    /// <summary>Transfers holding a slot right now.</summary>
    public int Sending => Volatile.Read(ref _sending);
    /// <summary>Highest number of transfers that held a slot at the same time (diagnostics, tests).</summary>
    public int MaxObservedParallel => Volatile.Read(ref _maxObserved);
    public int ChunkSize => _chunkSize;

    /// <summary>Starts (or restarts) the transfer of <paramref name="package"/> for a task from <paramref name="offset"/>.</summary>
    public Task Start(IAgentConnection connection, Guid taskId, AgentPackageInfo package, Func<Stream> open, long offset)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        var transfer = new Transfer(taskId, connection.ComputerId, cts);
        _active.AddOrUpdate(taskId, transfer, (_, old) =>
        {
            old.Cancel();
            return transfer;
        });
        transfer.Completion = Task.Run(() => RunAsync(transfer, connection, package, open, offset));
        return transfer.Completion;
    }

    public void Cancel(Guid taskId)
    {
        if (_active.TryGetValue(taskId, out var t)) t.Cancel();
    }

    public void CancelComputer(Guid computerId)
    {
        foreach (var t in _active.Values)
            if (t.ComputerId == computerId) t.Cancel();
    }

    /// <summary>Completes when no transfer is running (tests).</summary>
    public async Task WhenIdleAsync()
    {
        while (true)
        {
            var active = _active.Values.ToArray();
            if (active.Length == 0) return;
            if (active.Any(t => t.Completion is null))
            {
                await Task.Delay(1);
                continue;
            }
            await Task.WhenAll(active.Select(t => t.Completion!));
        }
    }

    private async Task RunAsync(Transfer transfer, IAgentConnection connection, AgentPackageInfo package, Func<Stream> open, long offset)
    {
        var ct = transfer.Token;
        var acquired = false;
        var position = offset;
        try
        {
            await _slots.WaitAsync(ct);
            acquired = true;
            var now = Interlocked.Increment(ref _sending);
            int seen;
            while (now > (seen = Volatile.Read(ref _maxObserved)) && Interlocked.CompareExchange(ref _maxObserved, now, seen) != seen) { }

            await MarkDownloadingAsync(transfer.TaskId, position, ct);
            await using var stream = open();
            if (offset > 0) stream.Seek(offset, SeekOrigin.Begin);
            var buffer = new byte[_chunkSize];
            var chunks = 0;
            while (position < package.Size)
            {
                ct.ThrowIfCancellationRequested();
                var want = (int)Math.Min(_chunkSize, package.Size - position);
                var n = await stream.ReadAtLeastAsync(buffer.AsMemory(0, want), want, throwOnEndOfStream: false, ct);
                if (n == 0) break;
                if (_limiter is not null) await _limiter.WaitAsync(n, ct);
                await connection.SendChunkAsync(transfer.TaskId, position, buffer.AsMemory(0, n), ct);
                position += n;
                if (++chunks % ProgressEveryChunks == 0) await SaveProgressAsync(transfer.TaskId, position, CancellationToken.None);
            }
            await SaveProgressAsync(transfer.TaskId, position, CancellationToken.None);
            _logger.LogInformation("Sent agent package {Version} for task {TaskId} to computer {ComputerId} ({From}..{To} bytes)",
                package.Version, transfer.TaskId, transfer.ComputerId, offset, position);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Package transfer for task {TaskId} stopped at {Position}", transfer.TaskId, position);
            if (position > offset) await TrySaveProgressAsync(transfer.TaskId, position);
        }
        catch (Exception ex)
        {
            // Typically the connection dropped; the agent resumes with a new PackageRequest.
            _logger.LogWarning(ex, "Package transfer for task {TaskId} failed at {Position}", transfer.TaskId, position);
            if (position > offset) await TrySaveProgressAsync(transfer.TaskId, position);
        }
        finally
        {
            if (acquired)
            {
                Interlocked.Decrement(ref _sending);
                _slots.Release();
            }
            _active.TryRemove(new KeyValuePair<Guid, Transfer>(transfer.TaskId, transfer));
            transfer.Dispose();
        }
    }

    private async Task MarkDownloadingAsync(Guid taskId, long position, CancellationToken ct)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await db.AgentUpdateTasks
            .Where(t => t.Id == taskId && (t.State == AgentUpdateState.Pending || t.State == AgentUpdateState.Sent))
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.State, AgentUpdateState.Downloading)
                .SetProperty(t => t.BytesSent, position)
                .SetProperty(t => t.UpdatedAtUtc, now), ct);
    }

    private async Task SaveProgressAsync(Guid taskId, long position, CancellationToken ct)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await db.AgentUpdateTasks.Where(t => t.Id == taskId)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.BytesSent, position).SetProperty(t => t.UpdatedAtUtc, now), ct);
    }

    private async Task TrySaveProgressAsync(Guid taskId, long position)
    {
        try { await SaveProgressAsync(taskId, position, CancellationToken.None); }
        catch (Exception ex) { _logger.LogDebug(ex, "Could not save transfer progress for {TaskId}", taskId); }
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        _shutdown.Dispose();
    }

    private sealed class Transfer(Guid taskId, Guid computerId, CancellationTokenSource cts) : IDisposable
    {
        private int _disposed;
        public Guid TaskId { get; } = taskId;
        public Guid ComputerId { get; } = computerId;
        public CancellationToken Token { get; } = cts.Token;
        public Task? Completion { get; set; }

        public void Cancel()
        {
            if (Volatile.Read(ref _disposed) == 0)
                try { cts.Cancel(); } catch (ObjectDisposedException) { }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) cts.Dispose();
        }
    }
}

/// <summary>
/// Token bucket shared by all transfers: <c>bytesPerSecond</c> refill, one second of burst. A sender that overdraws
/// waits until the bucket would be back at zero, so the long-run total rate stays at the cap.
/// </summary>
internal sealed class BandwidthLimiter
{
    private readonly double _rate;
    private readonly double _capacity;
    private readonly TimeProvider _time;
    private readonly Lock _lock = new();
    private double _tokens;
    private long _last;

    public BandwidthLimiter(long bytesPerSecond, TimeProvider time)
    {
        if (bytesPerSecond <= 0) throw new ArgumentOutOfRangeException(nameof(bytesPerSecond));
        _rate = bytesPerSecond;
        _capacity = bytesPerSecond;
        _time = time;
        _tokens = _capacity;
        _last = time.GetTimestamp();
    }

    /// <summary>Takes <paramref name="bytes"/> from the bucket and returns how long the caller must wait first.</summary>
    public TimeSpan Reserve(int bytes)
    {
        lock (_lock)
        {
            var now = _time.GetTimestamp();
            var elapsed = _time.GetElapsedTime(_last, now).TotalSeconds;
            _last = now;
            _tokens = Math.Min(_capacity, _tokens + elapsed * _rate);
            _tokens -= bytes;
            return _tokens >= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(-_tokens / _rate);
        }
    }

    public async ValueTask WaitAsync(int bytes, CancellationToken ct)
    {
        var delay = Reserve(bytes);
        if (delay > TimeSpan.Zero) await Task.Delay(delay, _time, ct);
    }
}
