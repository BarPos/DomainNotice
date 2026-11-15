namespace DomainNotice.Server.Directory;

/// <summary>
/// Minimal 5-field cron: minute hour day-of-month month day-of-week. Each field supports <c>*</c>, numbers,
/// lists (<c>1,5</c>), ranges (<c>1-5</c>) and steps (<c>*/15</c>, <c>1-10/2</c>, <c>5/10</c>). Day-of-week 0 and 7
/// are Sunday. As in classic cron, when both day fields are restricted a day matches if either matches.
/// Evaluated in the given time zone (the server's local time for Directory:FullSyncCron).
/// </summary>
public sealed class CronSchedule
{
    private readonly bool[] _minutes = new bool[60];
    private readonly bool[] _hours = new bool[24];
    private readonly bool[] _daysOfMonth = new bool[32];
    private readonly bool[] _months = new bool[13];
    private readonly bool[] _daysOfWeek = new bool[7];
    private readonly bool _domRestricted;
    private readonly bool _dowRestricted;

    public string Expression { get; }

    private CronSchedule(string expression, string[] f)
    {
        Expression = expression;
        ParseField(f[0], 0, 59, _minutes, "minute");
        ParseField(f[1], 0, 23, _hours, "hour");
        _domRestricted = ParseField(f[2], 1, 31, _daysOfMonth, "day of month");
        ParseField(f[3], 1, 12, _months, "month");
        var dow = new bool[8];
        _dowRestricted = ParseField(f[4], 0, 7, dow, "day of week");
        for (var i = 0; i < 7; i++) _daysOfWeek[i] = dow[i];
        if (dow[7]) _daysOfWeek[0] = true;
    }

    public static CronSchedule Parse(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression)) throw new FormatException("The cron expression is empty.");
        var fields = expression.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 5) throw new FormatException($"Cron expression '{expression}' must have 5 fields.");
        return new CronSchedule(expression.Trim(), fields);
    }

    public static bool TryParse(string expression, out CronSchedule? schedule, out string? error)
    {
        try
        {
            schedule = Parse(expression);
            error = null;
            return true;
        }
        catch (FormatException e)
        {
            schedule = null;
            error = e.Message;
            return false;
        }
    }

    /// <summary>Returns true when the field restricts values (is not a bare <c>*</c>).</summary>
    private static bool ParseField(string field, int min, int max, bool[] set, string name)
    {
        var restricted = field != "*";
        foreach (var item in field.Split(','))
        {
            if (item.Length == 0) throw new FormatException($"Empty item in cron {name} field '{field}'.");
            var step = 1;
            var rangePart = item;
            var slash = item.IndexOf('/');
            if (slash >= 0)
            {
                step = ParseNumber(item[(slash + 1)..], 1, max - min + 1, name);
                rangePart = item[..slash];
            }
            int from, to;
            if (rangePart == "*")
            {
                from = min;
                to = max;
            }
            else
            {
                var dash = rangePart.IndexOf('-');
                if (dash >= 0)
                {
                    from = ParseNumber(rangePart[..dash], min, max, name);
                    to = ParseNumber(rangePart[(dash + 1)..], min, max, name);
                    if (to < from) throw new FormatException($"Cron {name} range '{rangePart}' is reversed.");
                }
                else
                {
                    from = ParseNumber(rangePart, min, max, name);
                    to = slash >= 0 ? max : from; // "5/10" = from 5 to max, every 10
                }
            }
            for (var v = from; v <= to; v += step) set[v] = true;
        }
        return restricted;
    }

    private static int ParseNumber(string text, int min, int max, string name)
    {
        if (!int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var v)
            || v < min || v > max)
            throw new FormatException($"Cron {name} value '{text}' must be a number from {min} to {max}.");
        return v;
    }

    private bool DayMatches(DateTime d)
    {
        var dom = _daysOfMonth[d.Day];
        var dow = _daysOfWeek[(int)d.DayOfWeek];
        if (_domRestricted && _dowRestricted) return dom || dow;
        return dom && dow;
    }

    /// <summary>
    /// The first occurrence strictly after <paramref name="after"/>, as UTC, evaluated in <paramref name="zone"/>.
    /// Wall-clock times skipped by a DST change are skipped; ambiguous times use the first occurrence.
    /// Null when nothing matches within 5 years (e.g. "0 0 31 2 *").
    /// </summary>
    public DateTime? GetNextOccurrenceUtc(DateTime after, TimeZoneInfo zone)
    {
        var afterUtc = after.Kind == DateTimeKind.Utc ? after : after.ToUniversalTime();
        var local = TimeZoneInfo.ConvertTimeFromUtc(afterUtc, zone);
        // Start at the next whole minute.
        var start = new DateTime(local.Year, local.Month, local.Day, local.Hour, local.Minute, 0, DateTimeKind.Unspecified).AddMinutes(1);
        var day = start.Date;
        var limit = day.AddYears(5);
        for (; day <= limit; day = day.AddDays(1))
        {
            if (!_months[day.Month] || !DayMatches(day)) continue;
            for (var h = 0; h < 24; h++)
            {
                if (!_hours[h]) continue;
                for (var m = 0; m < 60; m++)
                {
                    if (!_minutes[m]) continue;
                    var candidate = day.AddHours(h).AddMinutes(m);
                    if (candidate < start) continue;
                    if (zone.IsInvalidTime(candidate)) continue;
                    DateTime utc;
                    if (zone.IsAmbiguousTime(candidate))
                    {
                        var offsets = zone.GetAmbiguousTimeOffsets(candidate);
                        var maxOffset = offsets.Max();
                        utc = DateTime.SpecifyKind(candidate - maxOffset, DateTimeKind.Utc);
                    }
                    else
                    {
                        utc = TimeZoneInfo.ConvertTimeToUtc(candidate, zone);
                    }
                    if (utc > afterUtc) return utc;
                }
            }
        }
        return null;
    }

    public override string ToString() => Expression;
}
