using System.Globalization;
using Microsoft.Extensions.Primitives;

namespace DomainNotice.Console.Grid;

/// <summary>Filter operators (plan 9.3). The URL token is the lowercase name in <see cref="GridQuery"/>.</summary>
public enum FilterOp
{
    /// <summary>Text: case-insensitive substring.</summary>
    Contains,
    /// <summary>Text (case-insensitive), enum/choice (value list separated by '|'), bool ("true"/"false").</summary>
    Equals,
    /// <summary>Date range: "fromIso..toIso", either side may be empty. Bounds are UTC instants.</summary>
    Between,
    /// <summary>Date older than N days (relative, resolved with the server clock at query time).</summary>
    OlderThanDays,
    /// <summary>Date within the last N days.</summary>
    WithinDays,
    /// <summary>Number ≥ value.</summary>
    AtLeast,
    /// <summary>Number ≤ value.</summary>
    AtMost,
}

/// <summary>One filter chip: field key, operator and the raw value as it appears in the URL.</summary>
public sealed record FilterClause(string Field, FilterOp Op, string Value)
{
    private static readonly Dictionary<FilterOp, string> Tokens = new()
    {
        [FilterOp.Contains] = "contains",
        [FilterOp.Equals] = "eq",
        [FilterOp.Between] = "between",
        [FilterOp.OlderThanDays] = "older",
        [FilterOp.WithinDays] = "within",
        [FilterOp.AtLeast] = "gte",
        [FilterOp.AtMost] = "lte",
    };

    public static string Token(FilterOp op) => Tokens[op];

    /// <summary>URL form: <c>field:op:value</c>. The value is everything after the second ':' (IPv6 and times contain ':').</summary>
    public string Serialize() => $"{Field}:{Token(Op)}:{Value}";

    public static FilterClause? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var first = text.IndexOf(':');
        if (first <= 0) return null;
        var second = text.IndexOf(':', first + 1);
        if (second < 0) return null;
        var field = text[..first];
        var opToken = text[(first + 1)..second];
        var value = text[(second + 1)..];
        var op = Tokens.FirstOrDefault(kv => kv.Value == opToken);
        if (op.Value is null) return null;
        return new FilterClause(field, op.Key, value);
    }

    /// <summary>Parses "from..to" (ISO 8601, UTC). Returned values have <see cref="DateTimeKind.Utc"/> (Npgsql requires it).</summary>
    public (DateTime? From, DateTime? To) ParseRange()
    {
        var parts = Value.Split("..", 2);
        return (ParseUtc(parts[0]), parts.Length > 1 ? ParseUtc(parts[1]) : null);
    }

    public static string FormatRange(DateTime? fromUtc, DateTime? toUtc) =>
        $"{FormatUtc(fromUtc)}..{FormatUtc(toUtc)}";

    public static string FormatUtc(DateTime? utc) =>
        utc is null ? "" : DateTime.SpecifyKind(utc.Value, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    public static DateTime? ParseUtc(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return DateTime.TryParse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d)
            ? DateTime.SpecifyKind(d, DateTimeKind.Utc)
            : null;
    }

    public int? ParseInt() =>
        int.TryParse(Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

    public double? ParseDouble() =>
        double.TryParse(Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : null;

    public IReadOnlyList<string> ParseList() =>
        Value.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>
/// The whole state of one grid: search, filters, sort, page, page size and visible columns. It lives in the URL
/// query string only (bookmarkable, plan 9.3); a key prefix keeps several grids on one page apart.
/// </summary>
public sealed class GridQuery
{
    public const int DefaultPageSize = 25;
    public const int MaxPageSize = 500;
    public static readonly int[] PageSizes = [25, 50, 100, 200];

    public string? Search { get; set; }
    public List<FilterClause> Filters { get; set; } = [];
    /// <summary>Field key; null = the list's default sort.</summary>
    public string? Sort { get; set; }
    public bool Descending { get; set; }
    /// <summary>1-based.</summary>
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = DefaultPageSize;
    /// <summary>Visible column keys; null = the list's defaults.</summary>
    public List<string>? Columns { get; set; }

    public GridQuery Clone() => new()
    {
        Search = Search, Filters = [.. Filters], Sort = Sort, Descending = Descending, Page = Page, PageSize = PageSize,
        Columns = Columns is null ? null : [.. Columns],
    };

    public static GridQuery Parse(IEnumerable<KeyValuePair<string, StringValues>> query, string prefix = "")
    {
        var dict = query.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        string? One(string key) => dict.TryGetValue(prefix + key, out var v) && !StringValues.IsNullOrEmpty(v) ? v.ToString() : null;

        var q = new GridQuery { Search = One("q") };
        if (dict.TryGetValue(prefix + "f", out var filters))
            foreach (var f in filters)
                if (FilterClause.TryParse(f) is { } clause) q.Filters.Add(clause);

        if (One("sort") is { } sort)
        {
            q.Descending = sort.StartsWith('-');
            q.Sort = sort.TrimStart('-', '+');
            if (q.Sort.Length == 0) q.Sort = null;
        }
        if (int.TryParse(One("page"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var page) && page > 0) q.Page = page;
        if (int.TryParse(One("size"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var size) && size > 0)
            q.PageSize = Math.Min(size, MaxPageSize);
        if (One("cols") is { } cols)
            q.Columns = cols.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        return q;
    }

    /// <summary>Parses a raw query string ("?a=b&amp;c=d" or "a=b").</summary>
    public static GridQuery Parse(string queryString, string prefix = "") =>
        Parse(Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(queryString), prefix);

    /// <summary>
    /// Parameters for <c>NavigationManager.GetUriWithQueryParameters</c>. Default values map to null, which removes
    /// the key, so URLs stay short.
    /// </summary>
    public Dictionary<string, object?> ToQueryParameters(string prefix = "")
    {
        return new Dictionary<string, object?>
        {
            [prefix + "q"] = string.IsNullOrWhiteSpace(Search) ? null : Search,
            [prefix + "f"] = Filters.Count == 0 ? null : Filters.Select(f => f.Serialize()).ToArray(),
            [prefix + "sort"] = Sort is null ? null : (Descending ? "-" : "") + Sort,
            [prefix + "page"] = Page <= 1 ? null : Page.ToString(CultureInfo.InvariantCulture),
            [prefix + "size"] = PageSize == DefaultPageSize ? null : PageSize.ToString(CultureInfo.InvariantCulture),
            [prefix + "cols"] = Columns is null ? null : string.Join(',', Columns),
        };
    }

    /// <summary>Query string without the leading '?', e.g. for CSV export links.</summary>
    public string ToQueryString(string prefix = "")
    {
        var parts = new List<string>();
        foreach (var (key, value) in ToQueryParameters(prefix))
        {
            switch (value)
            {
                case null: break;
                case string[] many:
                    parts.AddRange(many.Select(v => $"{Uri.EscapeDataString(key)}={Uri.EscapeDataString(v)}"));
                    break;
                default:
                    parts.Add($"{Uri.EscapeDataString(key)}={Uri.EscapeDataString(value.ToString()!)}");
                    break;
            }
        }
        return string.Join('&', parts);
    }
}
