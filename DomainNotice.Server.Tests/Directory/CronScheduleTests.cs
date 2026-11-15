using DomainNotice.Server.Directory;

namespace DomainNotice.Server.Tests.Directory;

public class CronScheduleTests
{
    private static DateTime U(int y, int mo, int d, int h, int mi) => new(y, mo, d, h, mi, 0, DateTimeKind.Utc);

    private static DateTime? Next(string expr, DateTime after, TimeZoneInfo? zone = null) =>
        CronSchedule.Parse(expr).GetNextOccurrenceUtc(after, zone ?? TimeZoneInfo.Utc);

    [Fact]
    public void Nightly_default()
    {
        Assert.Equal(U(2026, 11, 16, 2, 0), Next("0 2 * * *", U(2026, 11, 15, 10, 0)));
        Assert.Equal(U(2026, 11, 15, 2, 0), Next("0 2 * * *", U(2026, 11, 15, 1, 59)));
        // Strictly after: at 02:00 exactly the next one is tomorrow.
        Assert.Equal(U(2026, 11, 16, 2, 0), Next("0 2 * * *", U(2026, 11, 15, 2, 0)));
        Assert.Equal(U(2026, 11, 16, 2, 0), Next("0 2 * * *", new DateTime(2026, 11, 15, 2, 0, 30, DateTimeKind.Utc)));
    }

    [Fact]
    public void Steps_lists_and_ranges()
    {
        Assert.Equal(U(2026, 1, 1, 0, 15), Next("*/15 * * * *", U(2026, 1, 1, 0, 0)));
        Assert.Equal(U(2026, 1, 1, 0, 45), Next("*/15 * * * *", U(2026, 1, 1, 0, 31)));
        Assert.Equal(U(2026, 1, 1, 1, 5), Next("5,35 * * * *", U(2026, 1, 1, 0, 40)));
        Assert.Equal(U(2026, 1, 1, 9, 0), Next("0 9-17/4 * * *", U(2026, 1, 1, 8, 0)));
        Assert.Equal(U(2026, 1, 1, 13, 0), Next("0 9-17/4 * * *", U(2026, 1, 1, 9, 0)));
        Assert.Equal(U(2026, 1, 2, 9, 0), Next("0 9-17/4 * * *", U(2026, 1, 1, 17, 0)));
        Assert.Equal(U(2026, 1, 1, 0, 50), Next("20/30 * * * *", U(2026, 1, 1, 0, 21))); // n/step = n..max
    }

    [Fact]
    public void Day_of_week_and_month()
    {
        // 2026-11-15 is a Sunday.
        Assert.Equal(U(2026, 11, 16, 3, 0), Next("0 3 * * 1-5", U(2026, 11, 15, 0, 0)));
        Assert.Equal(U(2026, 11, 22, 3, 0), Next("0 3 * * 0", U(2026, 11, 15, 4, 0)));
        Assert.Equal(U(2026, 11, 22, 3, 0), Next("0 3 * * 7", U(2026, 11, 15, 4, 0))); // 7 = Sunday too
        Assert.Equal(U(2027, 1, 1, 0, 0), Next("0 0 1 1 *", U(2026, 11, 15, 0, 0)));
        // Both day fields restricted: either matches (the 20th, or any Monday).
        Assert.Equal(U(2026, 11, 16, 0, 0), Next("0 0 20 * 1", U(2026, 11, 15, 0, 0)));
        Assert.Equal(U(2026, 11, 20, 0, 0), Next("0 0 20 * 1", U(2026, 11, 17, 0, 0)));
        Assert.Equal(U(2028, 2, 29, 0, 0), Next("0 0 29 2 *", U(2026, 11, 15, 0, 0)));
    }

    [Fact]
    public void Never_matching_returns_null() => Assert.Null(Next("0 0 31 2 *", U(2026, 1, 1, 0, 0)));

    [Fact]
    public void Local_time_zone_is_used()
    {
        var tz = TimeZoneInfo.CreateCustomTimeZone("Plus2", TimeSpan.FromHours(2), "Plus2", "Plus2");
        Assert.Equal(U(2026, 11, 16, 0, 0), Next("0 2 * * *", U(2026, 11, 15, 10, 0), tz));
    }

    [Fact]
    public void Dst_gap_and_overlap()
    {
        TimeZoneInfo tz;
        try
        {
            tz = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");
        }
        catch (TimeZoneNotFoundException)
        {
            Assert.Skip("Europe/Warsaw time zone data not available.");
            return;
        }
        // 2026-03-29 02:00 → 03:00 local does not exist; the next valid 02:30 is the next day (01:30 CET+1 → 00:30Z).
        Assert.Equal(U(2026, 3, 30, 0, 30), Next("30 2 * * *", U(2026, 3, 28, 12, 0), tz));
        // 2026-10-25 02:30 local happens twice; the first (CEST, +2) is 00:30Z.
        Assert.Equal(U(2026, 10, 25, 0, 30), Next("30 2 * * *", U(2026, 10, 24, 12, 0), tz));
    }

    [Theory]
    [InlineData("")]
    [InlineData("* * * *")]
    [InlineData("* * * * * *")]
    [InlineData("60 * * * *")]
    [InlineData("* 24 * * *")]
    [InlineData("* * 0 * *")]
    [InlineData("* * * 13 *")]
    [InlineData("* * * * 8")]
    [InlineData("5-1 * * * *")]
    [InlineData("*/0 * * * *")]
    [InlineData("a * * * *")]
    [InlineData("1,,2 * * * *")]
    [InlineData("-1 * * * *")]
    public void Invalid_expressions_are_rejected(string expr)
    {
        Assert.Throws<FormatException>(() => CronSchedule.Parse(expr));
        Assert.False(CronSchedule.TryParse(expr, out _, out var error));
        Assert.NotNull(error);
    }
}
