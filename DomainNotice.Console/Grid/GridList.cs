using System.Globalization;
using System.Linq.Expressions;
using System.Text;
using DomainNotice.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace DomainNotice.Console.Grid;

public enum ColumnKind { Text, Date, Bool, Number, Percent, Status }

/// <summary>A displayed column of a list. <see cref="SortField"/> is the field key used when the header is clicked.</summary>
public sealed record GridColumn<TRow>(string Key, string Label, Func<TRow, object?> Value, ColumnKind Kind = ColumnKind.Text,
    string? SortField = null, bool DefaultVisible = true);

public sealed record GridSort<T>(LambdaExpression Selector, bool Descending = false);

public sealed record GridPage<TRow>(IReadOnlyList<TRow> Rows, int Total);

/// <summary>Per-query context: database, scope values from the URL (tree node, parent id), the clock and services.</summary>
public sealed class GridContext(AppDbContext db, IReadOnlyDictionary<string, string?> scope, DateTime nowUtc, IServiceProvider? services = null)
{
    public AppDbContext Db { get; } = db;
    public IReadOnlyDictionary<string, string?> Scope { get; } = scope;
    public DateTime NowUtc { get; } = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc);
    public IServiceProvider? Services { get; } = services;

    public string? Get(string key) => Scope.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;
    public Guid? GetGuid(string key) => Guid.TryParse(Get(key), out var g) ? g : null;
}

/// <summary>Non-generic view of a list, used by the CSV endpoint and the grid component.</summary>
public interface IGridList
{
    string Name { get; }
    /// <summary>Scope keys this list reads from the URL (unprefixed).</summary>
    IReadOnlyList<string> ScopeKeys { get; }
    IReadOnlyList<GridFieldInfo> GetFields(GridContext ctx);
    Task<byte[]> ExportCsvAsync(GridContext ctx, GridQuery query, CancellationToken ct = default);
}

public interface IGridList<TRow> : IGridList
{
    IReadOnlyList<GridColumn<TRow>> Columns { get; }
    string DefaultSort { get; }
    bool DefaultDescending { get; }
    /// <summary>False for lists without row identity (anonymous answers).</summary>
    bool Selectable { get; }
    object RowKey(TRow row);
    Task<GridPage<TRow>> QueryAsync(GridContext ctx, GridQuery query, CancellationToken ct = default);
}

/// <summary>
/// A list shown by the shared grid (plan 9.3). Filtering and sorting run on <typeparamref name="TSource"/> in the
/// database; the page is then projected to <typeparamref name="TRow"/>. Everything is server-side.
/// </summary>
public abstract class GridList<TSource, TRow> : IGridList<TRow>
{
    public const int MaxExportRows = 100_000;

    public abstract string Name { get; }
    public virtual IReadOnlyList<string> ScopeKeys => [];
    public abstract IReadOnlyList<GridColumn<TRow>> Columns { get; }
    public abstract string DefaultSort { get; }
    public virtual bool DefaultDescending => false;
    public virtual bool Selectable => true;
    public abstract object RowKey(TRow row);

    protected abstract ValueTask<IQueryable<TSource>> SourceAsync(GridContext ctx);
    public abstract IReadOnlyList<GridField<TSource>> Fields(GridContext ctx);
    protected abstract Expression<Func<TSource, TRow>> Projection(GridContext ctx);
    /// <summary>Appended after the primary sort so paging is stable. Content-only for anonymous answers.</summary>
    protected abstract IReadOnlyList<GridSort<TSource>> TieBreakers(GridContext ctx);

    public IReadOnlyList<GridFieldInfo> GetFields(GridContext ctx) => Fields(ctx).Select(f => f.Info).ToList();

    /// <summary>Source with search, filters and sort applied (no paging). Public for tests.</summary>
    public async Task<IQueryable<TSource>> BuildQueryAsync(GridContext ctx, GridQuery query)
    {
        var fields = Fields(ctx);
        var source = await SourceAsync(ctx);
        source = GridEngine.ApplyFilters(source, fields, query, ctx.NowUtc);
        return GridEngine.ApplySort(source, fields, query, DefaultSort, DefaultDescending, TieBreakers(ctx));
    }

    public async Task<IQueryable<TRow>> BuildPageQueryAsync(GridContext ctx, GridQuery query)
    {
        var q = await BuildQueryAsync(ctx, query);
        var size = Math.Clamp(query.PageSize, 1, GridQuery.MaxPageSize);
        var skip = (Math.Max(query.Page, 1) - 1) * size;
        return q.Skip(skip).Take(size).Select(Projection(ctx));
    }

    public async Task<GridPage<TRow>> QueryAsync(GridContext ctx, GridQuery query, CancellationToken ct = default)
    {
        var filtered = await BuildQueryAsync(ctx, query);
        var total = await filtered.CountAsync(ct);
        var rows = await (await BuildPageQueryAsync(ctx, query)).ToListAsync(ct);
        return new GridPage<TRow>(rows, total);
    }

    /// <summary>CSV of the whole filtered result (not just the page), UTF-8 with BOM, visible columns only.</summary>
    public async Task<byte[]> ExportCsvAsync(GridContext ctx, GridQuery query, CancellationToken ct = default)
    {
        var q = await BuildQueryAsync(ctx, query);
        var rows = await q.Take(MaxExportRows).Select(Projection(ctx)).ToListAsync(ct);
        var columns = VisibleColumns(query);
        return Csv.Write(columns.Select(c => c.Label), rows.Select(r => columns.Select(c => Csv.Format(c.Value(r), c.Kind))));
    }

    public IReadOnlyList<GridColumn<TRow>> VisibleColumns(GridQuery query) =>
        query.Columns is { Count: > 0 } keys
            ? Columns.Where(c => keys.Contains(c.Key, StringComparer.OrdinalIgnoreCase)).ToList() is { Count: > 0 } chosen ? chosen : Columns.Where(c => c.DefaultVisible).ToList()
            : Columns.Where(c => c.DefaultVisible).ToList();
}

public static class GridEngine
{
    /// <summary>AND of all valid clauses; free text is an OR across the searchable fields. Unknown fields are ignored.</summary>
    public static IQueryable<T> ApplyFilters<T>(IQueryable<T> source, IReadOnlyList<GridField<T>> fields, GridQuery query, DateTime nowUtc)
    {
        foreach (var clause in query.Filters)
        {
            var field = fields.FirstOrDefault(f => string.Equals(f.Key, clause.Field, StringComparison.OrdinalIgnoreCase));
            if (field is not { Filterable: true, Predicate: not null }) continue;
            if (!field.Ops.Contains(clause.Op)) continue;
            if (field.Predicate(clause, nowUtc) is { } predicate) source = source.Where(predicate);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            var parts = fields.Where(f => f.Search is not null).Select(f => f.Search!(term)).ToList();
            if (parts.Count > 0) source = source.Where(GridField.OrElse(parts));
        }
        return source;
    }

    public static IQueryable<T> ApplySort<T>(IQueryable<T> source, IReadOnlyList<GridField<T>> fields, GridQuery query,
        string defaultSort, bool defaultDescending, IReadOnlyList<GridSort<T>> tieBreakers)
    {
        var primary = Find(fields, query.Sort) is { SortSelector: not null } f
            ? new GridSort<T>(f.SortSelector, query.Descending)
            : Find(fields, defaultSort) is { SortSelector: not null } d ? new GridSort<T>(d.SortSelector, defaultDescending) : null;

        var sorts = new List<GridSort<T>>();
        if (primary is not null) sorts.Add(primary);
        sorts.AddRange(tieBreakers.Where(t => primary is null || t.Selector != primary.Selector));

        var expression = source.Expression;
        for (var i = 0; i < sorts.Count; i++)
        {
            var s = sorts[i];
            var method = (i == 0 ? "OrderBy" : "ThenBy") + (s.Descending ? "Descending" : "");
            expression = Expression.Call(typeof(Queryable), method, [typeof(T), s.Selector.ReturnType], expression, Expression.Quote(s.Selector));
        }
        return sorts.Count == 0 ? source : source.Provider.CreateQuery<T>(expression);
    }

    private static GridField<T>? Find<T>(IReadOnlyList<GridField<T>> fields, string? key) =>
        key is null ? null : fields.FirstOrDefault(f => string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase));
}

public static class Csv
{
    /// <summary>UTF-8 with BOM, CRLF lines. Cells that a spreadsheet would run as a formula get a leading apostrophe.</summary>
    public static byte[] Write(IEnumerable<string> header, IEnumerable<IEnumerable<string>> rows)
    {
        var sb = new StringBuilder();
        sb.Append(string.Join(',', header.Select(Escape))).Append("\r\n");
        foreach (var row in rows) sb.Append(string.Join(',', row.Select(Escape))).Append("\r\n");
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(sb.ToString())];
    }

    public static string Format(object? value, ColumnKind kind) => value switch
    {
        null => "",
        DateTime d => DateTime.SpecifyKind(d, DateTimeKind.Utc).ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture),
        bool b => b ? "Yes" : "No",
        double n when kind == ColumnKind.Percent => (n * 100).ToString("0.#", CultureInfo.InvariantCulture) + "%",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        IEnumerable<string> many => string.Join("; ", many),
        _ => value.ToString() ?? "",
    };

    private static string Escape(string cell)
    {
        if (cell.Length > 0 && cell[0] is '=' or '+' or '-' or '@' or '\t' or '\r') cell = "'" + cell;
        return cell.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + cell.Replace("\"", "\"\"") + "\"" : cell;
    }
}
