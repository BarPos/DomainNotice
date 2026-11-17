using System.Threading.Channels;
using DomainNotice.Shared.Protocol;

namespace DomainNotice.Server.AgentLink;

/// <summary>
/// The ordered per-connection queue between the read loop and the worker (see <see cref="AgentProtocol"/>).
/// <list type="bullet">
/// <item>At most <see cref="MaxItems"/> messages and <see cref="MaxBodyBytes"/> of frame bodies wait at once; the read
/// loop waits when it is full (TCP back-pressure on the agent). A single message larger than the byte budget is still
/// accepted when the queue is empty.</item>
/// <item>Status is coalesced: only the latest pending Status is kept, outside the item budget, and the worker applies
/// Status at most once per <see cref="AgentLinkTimings.StatusMinInterval"/>. The final state is never lost: a Status
/// that arrives while one is pending replaces it, and one that arrives later is applied after the interval.</item>
/// </list>
/// </summary>
internal sealed class InboundQueue
{
    public const int MaxItems = 16;
    /// <summary>Frame body bytes waiting at once (decoded messages take a few times that in memory).</summary>
    public const long MaxBodyBytes = 1024 * 1024;

    private readonly Channel<Item> _channel = Channel.CreateBounded<Item>(new BoundedChannelOptions(MaxItems)
    {
        SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait,
    });
    private readonly Lock _budgetLock = new();
    private readonly TimeProvider _time;
    private readonly TimeSpan _statusInterval;
    private long _bytes;
    private TaskCompletionSource _budgetFreed = NewSignal();
    private IncomingFrame? _pendingStatus;
    private DateTime _lastStatusUtc = DateTime.MinValue;

    public InboundQueue(TimeProvider time, TimeSpan statusInterval)
    {
        _time = time;
        _statusInterval = statusInterval;
    }

    /// <summary>Taken by the worker: one queued message, the online marker, or the latest Status.</summary>
    public readonly record struct Item(IncomingFrame Frame, long Bytes, bool IsStatus);

    private static readonly IncomingFrame StatusMarker = IncomingFrame.FromChunk(new PackageChunk(Guid.Empty, 0, default));

    internal long QueuedBytes
    {
        get { lock (_budgetLock) return _bytes; }
    }

    /// <summary>Queues a marker or control item that takes no byte budget.</summary>
    public ValueTask WriteMarkerAsync(IncomingFrame marker, CancellationToken ct) => _channel.Writer.WriteAsync(new Item(marker, 0, false), ct);

    /// <summary>Read loop: queue one decoded message. Waits while the queue is full.</summary>
    public async ValueTask EnqueueAsync(IncomingFrame frame, CancellationToken ct)
    {
        if (frame.Message is Status)
        {
            // Latest wins; a marker is queued only when no Status was pending (the worker takes the latest one).
            if (Interlocked.Exchange(ref _pendingStatus, frame) is null)
                await _channel.Writer.WriteAsync(new Item(StatusMarker, 0, true), ct).ConfigureAwait(false);
            return;
        }
        long size = Math.Max(1, frame.BodyLength);
        await ReserveAsync(size, ct).ConfigureAwait(false);
        try
        {
            await _channel.Writer.WriteAsync(new Item(frame, size, false), ct).ConfigureAwait(false);
        }
        catch
        {
            Release(size);
            throw;
        }
    }

    public void Complete() => _channel.Writer.TryComplete();

    /// <summary>
    /// Worker: the next item. For a Status item, waits for the rate limit and returns the latest pending Status
    /// (or null when there is none any more); the caller then calls <see cref="Done"/>.
    /// </summary>
    public async IAsyncEnumerable<Item> ReadAllAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var item in _channel.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
        {
            if (!item.IsStatus)
            {
                yield return item;
                continue;
            }
            var wait = _lastStatusUtc + _statusInterval - _time.GetUtcNow().UtcDateTime;
            if (wait > TimeSpan.Zero && _lastStatusUtc != DateTime.MinValue)
            {
                // Not due yet: the Status stays pending (newer ones replace it) and its marker comes back when it is
                // due. Messages behind it (Shown, Confirm, ...) are not held up.
                _ = RequeueStatusMarkerAsync(wait, ct);
                continue;
            }
            if (Interlocked.Exchange(ref _pendingStatus, null) is not { } latest) continue;
            _lastStatusUtc = _time.GetUtcNow().UtcDateTime;
            yield return new Item(latest, 0, true);
        }
    }

    private async Task RequeueStatusMarkerAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, _time, ct).ConfigureAwait(false);
            await _channel.Writer.WriteAsync(new Item(StatusMarker, 0, true), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ChannelClosedException)
        {
            // Connection gone.
        }
    }

    /// <summary>Worker: the item was handled; frees its budget.</summary>
    public void Done(Item item)
    {
        if (item.Bytes > 0) Release(item.Bytes);
    }

    private async ValueTask ReserveAsync(long size, CancellationToken ct)
    {
        while (true)
        {
            Task wait;
            lock (_budgetLock)
            {
                if (_bytes == 0 || _bytes + size <= MaxBodyBytes)
                {
                    _bytes += size;
                    return;
                }
                wait = _budgetFreed.Task;
            }
            await wait.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    private void Release(long size)
    {
        TaskCompletionSource signal;
        lock (_budgetLock)
        {
            _bytes -= size;
            signal = _budgetFreed;
            _budgetFreed = NewSignal();
        }
        signal.TrySetResult();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
