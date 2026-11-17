using DomainNotice.Console.Grid;
using DomainNotice.Server.Common;
using DomainNotice.Shared;

namespace DomainNotice.Server.Tests.Console;

public class GridQueryTests
{
    [Fact]
    public void Url_round_trip_keeps_search_filters_sort_page_size_and_columns()
    {
        var q = new GridQuery
        {
            Search = "pc 12 & co",
            Filters =
            [
                new FilterClause("name", FilterOp.Contains, "pc-12"),
                new FilterClause("ip", FilterOp.Contains, "fe80::1"),
                new FilterClause("lastSeen", FilterOp.OlderThanDays, "7"),
                new FilterClause("adLink", FilterOp.Equals, "Proven|MatchedByName"),
                new FilterClause("enrolled", FilterOp.Between, FilterClause.FormatRange(
                    new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 2, 1, 23, 59, 59, DateTimeKind.Utc))),
            ],
            Sort = "lastSeen", Descending = true, Page = 3, PageSize = 50, Columns = ["name", "status"],
        };

        var text = q.ToQueryString("c.");
        var back = GridQuery.Parse(text, "c.");

        Assert.Equal(q.Search, back.Search);
        Assert.Equal(q.Filters, back.Filters);
        Assert.Equal("lastSeen", back.Sort);
        Assert.True(back.Descending);
        Assert.Equal(3, back.Page);
        Assert.Equal(50, back.PageSize);
        Assert.Equal(["name", "status"], back.Columns);
        // Another grid's prefix sees nothing.
        Assert.Empty(GridQuery.Parse(text, "x.").Filters);
    }

    [Fact]
    public void Defaults_are_left_out_of_the_url()
    {
        Assert.Equal("", new GridQuery().ToQueryString());
        var p = new GridQuery().ToQueryParameters("a.");
        Assert.All(p.Values, Assert.Null);
    }

    [Fact]
    public void Bad_clauses_and_sizes_are_ignored_or_clamped()
    {
        var q = GridQuery.Parse("f=nocolon&f=name:bogus:x&f=name:contains:ok&size=100000&page=-4");
        Assert.Single(q.Filters);
        Assert.Equal(GridQuery.MaxPageSize, q.PageSize);
        Assert.Equal(1, q.Page);
    }

    [Fact]
    public void Range_bounds_are_utc()
    {
        var clause = new FilterClause("x", FilterOp.Between, "2026-03-01T10:00:00+02:00..");
        var (from, to) = clause.ParseRange();
        Assert.Equal(DateTimeKind.Utc, from!.Value.Kind);
        Assert.Equal(new DateTime(2026, 3, 1, 8, 0, 0, DateTimeKind.Utc), from);
        Assert.Null(to);
    }

    [Fact]
    public void Csv_has_bom_and_neutralises_formulas()
    {
        var bytes = Csv.Write(["Name", "Note"], [["=cmd()", "a,\"b\""]]);
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        var text = System.Text.Encoding.UTF8.GetString(bytes[3..]);
        Assert.Equal("Name,Note\r\n'=cmd(),\"a,\"\"b\"\"\"\r\n", text);
    }

    [Fact]
    public void Csv_polish_uses_semicolon_and_keeps_a_hostile_value_in_one_cell()
    {
        const string hostile = "x;=cmd|' /C calc'!A0;";
        var bytes = Csv.Write(["Nazwa", "Uwagi"], [[hostile, "1,5"], ["=1+1", "a\r\nb"]], CsvFormat.SeparatorFor(AppLanguages.Polish));
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        var text = System.Text.Encoding.UTF8.GetString(bytes[3..]);
        Assert.Equal("Nazwa;Uwagi\r\n\"x;=cmd|' /C calc'!A0;\";\"1,5\"\r\n'=1+1;\"a\r\nb\"\r\n", text);
        // A spreadsheet splitting on ';' sees exactly two cells in the first data row, the first one the whole value.
        Assert.Equal([hostile, "1,5"], SplitRecord(text.Split("\r\n")[1], ';'));
    }

    [Fact]
    public void Csv_english_uses_comma_and_quotes_both_separators()
    {
        const string hostile = "x;=cmd|' /C calc'!A0;";
        var text = System.Text.Encoding.UTF8.GetString(Csv.Write(["Name"], [[hostile], ["a,b"], ["-2+3"], ["@SUM(A1)"], ["\tx"], ["plain"]],
            CsvFormat.SeparatorFor(AppLanguages.English))[3..]);
        Assert.Equal("Name\r\n\"x;=cmd|' /C calc'!A0;\"\r\n\"a,b\"\r\n'-2+3\r\n'@SUM(A1)\r\n'\tx\r\nplain\r\n", text);
        Assert.Equal(',', CsvFormat.SeparatorFor(null));
        Assert.Equal(',', CsvFormat.SeparatorFor("de"));
        Assert.Throws<ArgumentOutOfRangeException>(() => Csv.Write(["a"], [], '\t'));
    }

    /// <summary>RFC 4180 reader for one record without line breaks, as a spreadsheet would split it.</summary>
    private static List<string> SplitRecord(string line, char separator)
    {
        var cells = new List<string>();
        var sb = new System.Text.StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else sb.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == separator) { cells.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        cells.Add(sb.ToString());
        return cells;
    }
}
