using DomainNotice.Server.Announcements;

namespace DomainNotice.Server.Tests.Announcements;

public class KeyedLocksTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Entries_are_removed_when_nothing_holds_or_waits()
    {
        var locks = new KeyedLocks<string>(StringComparer.Ordinal);
        for (var i = 0; i < 1000; i++)
            using (await locks.AcquireAsync($@"CORP\invented{i}", Ct)) { }
        Assert.Equal(0, locks.Count);
    }

    [Fact]
    public async Task Lock_excludes_and_hands_over_to_a_waiter()
    {
        var locks = new KeyedLocks<string>(StringComparer.Ordinal);
        var held = await locks.AcquireAsync("k", Ct);
        var waiter = locks.AcquireAsync("k", Ct);
        await Task.Delay(50, Ct);
        Assert.False(waiter.IsCompleted);
        Assert.Equal(1, locks.Count);

        held.Dispose();
        using (await waiter.WaitAsync(TimeSpan.FromSeconds(5), Ct))
            Assert.Equal(1, locks.Count);
        Assert.Equal(0, locks.Count);
    }

    [Fact]
    public async Task Cancelled_waiter_does_not_leak_its_entry()
    {
        var locks = new KeyedLocks<Guid>();
        var key = Guid.NewGuid();
        var held = await locks.AcquireAsync(key, Ct);
        using var cts = new CancellationTokenSource();
        var waiter = locks.AcquireAsync(key, cts.Token);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        held.Dispose();
        Assert.Equal(0, locks.Count);

        // The key still works afterwards.
        using (await locks.AcquireAsync(key, Ct)) Assert.Equal(1, locks.Count);
        Assert.Equal(0, locks.Count);
    }
}
