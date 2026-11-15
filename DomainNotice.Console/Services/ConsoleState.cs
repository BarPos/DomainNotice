using DomainNotice.Server.Abstractions;

namespace DomainNotice.Console.Services;

/// <summary>
/// The browser's time zone for one circuit (times are entered and shown in the browser's local time, stored UTC).
/// Filled once by the layout through JS interop; until then (prerender, CSV) times are shown as UTC.
/// </summary>
public sealed class BrowserClock
{
    public TimeZoneInfo Zone { get; private set; } = TimeZoneInfo.Utc;
    public bool Known { get; private set; }
    public event Action? Changed;

    public void SetZone(string? ianaId)
    {
        if (string.IsNullOrWhiteSpace(ianaId)) return;
        try
        {
            Zone = TimeZoneInfo.FindSystemTimeZoneById(ianaId);
            Known = true;
            Changed?.Invoke();
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
        }
    }

    public string ZoneLabel => Known ? Zone.Id : "UTC";

    public DateTime ToLocal(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zone);

    /// <summary>Local wall-clock time → UTC. A time inside a DST gap is moved forward by an hour.</summary>
    public DateTime ToUtc(DateTime local)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (Zone.IsInvalidTime(unspecified)) unspecified = unspecified.AddHours(1);
        return DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeToUtc(unspecified, Zone), DateTimeKind.Utc);
    }

    public string Format(DateTime? utc, bool seconds = false)
    {
        if (utc is null) return "";
        var format = seconds ? "yyyy-MM-dd HH:mm:ss" : "yyyy-MM-dd HH:mm";
        return Known
            ? ToLocal(utc.Value).ToString(format, System.Globalization.CultureInfo.InvariantCulture)
            : DateTime.SpecifyKind(utc.Value, DateTimeKind.Utc).ToString(format, System.Globalization.CultureInfo.InvariantCulture) + " UTC";
    }

    public string FormatDate(DateTime? utc) =>
        utc is null ? "" : ToLocal(utc.Value).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// Hands targets from the Computers grid to the announcement editor (P7, "Send announcement to selected") within one
/// circuit, so long id lists never go into the URL.
/// </summary>
public sealed class AnnouncementHandoff
{
    private List<TargetEdit>? _targets;

    public void SetTargets(IEnumerable<TargetEdit> targets) => _targets = targets.ToList();

    public List<TargetEdit>? Take()
    {
        var t = _targets;
        _targets = null;
        return t;
    }
}
