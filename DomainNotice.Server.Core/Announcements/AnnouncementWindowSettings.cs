using DomainNotice.Server.Abstractions;

namespace DomainNotice.Server.Announcements;

/// <summary>
/// Console setting "text to the right of the title" in the announcement window (Settings → Announcement window).
/// Read by the Console preview and sent to agents with every Show. Cached for a short time; saving clears the cache.
/// </summary>
internal sealed class AnnouncementWindowSettings(ISettingsStore settings, IAuditLog audit, TimeProvider time) : IAnnouncementWindowSettings
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(30);
    private (string Value, DateTimeOffset At)? _cached;

    public async Task<string> GetHeaderTextAsync(CancellationToken ct = default)
    {
        if (_cached is { } c && time.GetUtcNow() - c.At < CacheFor) return c.Value;
        var value = await settings.GetAsync(SettingKeys.AnnouncementHeaderText, ct);
        // Not set yet: the default. Set to an empty string: no text.
        var text = value ?? AnnouncementWindowDefaults.HeaderText;
        _cached = (text, time.GetUtcNow());
        return text;
    }

    public async Task<OperationResult> SetHeaderTextAsync(string? text, string actor, CancellationToken ct = default)
    {
        var value = (text ?? "").Trim();
        if (value.Length > AnnouncementWindowDefaults.HeaderTextMax)
            return OperationResult.Fail($"The text must be {AnnouncementWindowDefaults.HeaderTextMax} characters or fewer.");
        await settings.SetAsync(SettingKeys.AnnouncementHeaderText, value, actor, ct);
        _cached = null;
        await audit.WriteAsync(actor, AuditActions.SettingChanged, "Setting", SettingKeys.AnnouncementHeaderText, value, ct);
        return OperationResult.Success;
    }
}
