using DomainNotice.Shared.Protocol;

namespace DomainNotice.Agent.Logic;

public enum ShownState
{
    /// <summary>No Session UI has reported the window yet.</summary>
    NotShown,
    /// <summary>The window appeared but the server link was down; send Shown when it is back.</summary>
    Unsent,
    /// <summary>Shown was sent to the server.</summary>
    Sent,
}

public sealed class PendingDelivery
{
    public required Guid DeliveryId { get; init; }
    public required string SessionUser { get; set; }
    public required AnnouncementPayload Announcement { get; set; }
    public DateTime ReceivedUtc { get; init; }
    public ShownState Shown { get; set; }
}

/// <summary>
/// Announcements received from the server and not yet confirmed or withdrawn, keyed by delivery id.
/// Not thread-safe: <see cref="AnnouncementRouter"/> guards it with its lock.
/// </summary>
public sealed class PendingDeliveryStore
{
    private readonly Dictionary<Guid, PendingDelivery> _items = [];

    public int Count => _items.Count;

    public static bool SameUser(string? a, string? b) =>
        string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Adds or refreshes a delivery. Returns true when it is new. A refresh keeps the Shown state.</summary>
    public bool Upsert(Guid deliveryId, string sessionUser, AnnouncementPayload announcement, DateTime nowUtc)
    {
        if (_items.TryGetValue(deliveryId, out var existing))
        {
            existing.SessionUser = sessionUser;
            existing.Announcement = announcement;
            return false;
        }

        _items[deliveryId] = new PendingDelivery
        {
            DeliveryId = deliveryId,
            SessionUser = sessionUser,
            Announcement = announcement,
            ReceivedUtc = nowUtc,
        };
        return true;
    }

    public PendingDelivery? Get(Guid deliveryId) => _items.GetValueOrDefault(deliveryId);

    public bool Remove(Guid deliveryId) => _items.Remove(deliveryId);

    /// <summary>Pending deliveries for one user, oldest first.</summary>
    public List<PendingDelivery> ForUser(string user) =>
        _items.Values.Where(d => SameUser(d.SessionUser, user)).OrderBy(d => d.ReceivedUtc).ToList();

    public List<PendingDelivery> All() => _items.Values.OrderBy(d => d.ReceivedUtc).ToList();

    /// <summary>Drops everything for a user (their last session logged off; the server re-pushes at logon).</summary>
    public int RemoveUser(string user)
    {
        var ids = _items.Values.Where(d => SameUser(d.SessionUser, user)).Select(d => d.DeliveryId).ToList();
        foreach (var id in ids) _items.Remove(id);
        return ids.Count;
    }
}
