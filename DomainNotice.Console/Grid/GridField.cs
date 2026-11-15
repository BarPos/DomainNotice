using System.Linq.Expressions;

namespace DomainNotice.Console.Grid;

public enum FieldKind { Text, Date, Choice, Bool, Number }

public sealed record GridOption(string Value, string Label);

/// <summary>UI metadata of a field (no expressions): what the filter editor needs.</summary>
public sealed record GridFieldInfo(string Key, string Label, FieldKind Kind, IReadOnlyList<GridOption> Options,
    IReadOnlyList<FilterOp> Ops, bool Sortable, bool Filterable, bool Searchable);

/// <summary>
/// A filterable/sortable field of a list, defined on the query source type <typeparamref name="T"/> with typed
/// expressions. Factories in <see cref="GridField"/> build the predicate per operator; values are captured in
/// closures, so EF Core sends them as parameters.
/// </summary>
public sealed class GridField<T>
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public required FieldKind Kind { get; init; }
    public IReadOnlyList<GridOption> Options { get; init; } = [];
    public IReadOnlyList<FilterOp> Ops { get; init; } = [];
    /// <summary>Sort key selector (Expression&lt;Func&lt;T, TKey&gt;&gt;); null = not sortable.</summary>
    public LambdaExpression? SortSelector { get; init; }
    public bool Filterable { get; init; } = true;
    /// <summary>Builds the predicate for a clause, or null when the value is invalid (the clause is then ignored).</summary>
    public Func<FilterClause, DateTime, Expression<Func<T, bool>>?>? Predicate { get; init; }
    /// <summary>Free-text search contribution (OR-ed with the other searchable fields).</summary>
    public Func<string, Expression<Func<T, bool>>>? Search { get; init; }

    public GridFieldInfo Info => new(Key, Label, Kind, Options, Ops, SortSelector is not null,
        Filterable && Predicate is not null, Search is not null);
}

public static class GridField
{
    private static readonly IReadOnlyList<FilterOp> TextOps = [FilterOp.Contains, FilterOp.Equals];
    private static readonly IReadOnlyList<FilterOp> DateOps = [FilterOp.OlderThanDays, FilterOp.WithinDays, FilterOp.Between];
    private static readonly IReadOnlyList<FilterOp> EqOps = [FilterOp.Equals];
    private static readonly IReadOnlyList<FilterOp> NumberOps = [FilterOp.AtLeast, FilterOp.AtMost];

    public static GridField<T> Text<T>(string key, string label, Expression<Func<T, string?>> selector,
        bool search = false, bool sortable = true)
    {
        return new GridField<T>
        {
            Key = key, Label = label, Kind = FieldKind.Text, Ops = TextOps,
            SortSelector = sortable ? selector : null,
            Predicate = (c, _) => TextPredicate(selector, c),
            Search = search ? term => Compose(selector, ContainsTemplate(term)) : null,
        };
    }

    /// <summary>A text filter whose predicate is written by hand (collections such as IP lists or current users).</summary>
    public static GridField<T> CustomText<T>(string key, string label, Func<string, Expression<Func<T, bool>>> contains,
        LambdaExpression? sortSelector = null, bool search = false)
    {
        return new GridField<T>
        {
            Key = key, Label = label, Kind = FieldKind.Text, Ops = [FilterOp.Contains], SortSelector = sortSelector,
            Predicate = (c, _) => c.Op == FilterOp.Contains && !string.IsNullOrWhiteSpace(c.Value) ? contains(c.Value.Trim().ToLowerInvariant()) : null,
            Search = search ? term => contains(term.Trim().ToLowerInvariant()) : null,
        };
    }

    public static GridField<T> Date<T>(string key, string label, Expression<Func<T, DateTime?>> selector, bool sortable = true)
    {
        return new GridField<T>
        {
            Key = key, Label = label, Kind = FieldKind.Date, Ops = DateOps, SortSelector = sortable ? selector : null,
            Predicate = (c, now) => DatePredicate(selector, c, now),
        };
    }

    public static GridField<T> Date<T>(string key, string label, Expression<Func<T, DateTime>> selector, bool sortable = true)
    {
        var nullable = Expression.Lambda<Func<T, DateTime?>>(Expression.Convert(selector.Body, typeof(DateTime?)), selector.Parameters);
        return new GridField<T>
        {
            Key = key, Label = label, Kind = FieldKind.Date, Ops = DateOps, SortSelector = sortable ? selector : null,
            Predicate = (c, now) => DatePredicate(nullable, c, now),
        };
    }

    public static GridField<T> Enum<T, TEnum>(string key, string label, Expression<Func<T, TEnum>> selector,
        Func<TEnum, string>? optionLabel = null, bool sortable = true) where TEnum : struct, System.Enum
    {
        var options = System.Enum.GetValues<TEnum>()
            .Select(v => new GridOption(v.ToString(), optionLabel?.Invoke(v) ?? Humanize(v.ToString()))).ToList();
        return new GridField<T>
        {
            Key = key, Label = label, Kind = FieldKind.Choice, Options = options, Ops = EqOps,
            SortSelector = sortable ? selector : null,
            Predicate = (c, _) =>
            {
                if (c.Op != FilterOp.Equals) return null;
                var parts = new List<Expression<Func<T, bool>>>();
                foreach (var text in c.ParseList())
                {
                    if (!System.Enum.TryParse<TEnum>(text, true, out var value) || !System.Enum.IsDefined(value)) continue;
                    parts.Add(Compose(selector, EnumEquals(value)));
                }
                return parts.Count == 0 ? null : OrElse(parts);
            },
        };
    }

    /// <summary>A choice whose options map to hand-written predicates (e.g. a computed status).</summary>
    public static GridField<T> Choice<T>(string key, string label, IReadOnlyList<GridOption> options,
        Func<string, DateTime, Expression<Func<T, bool>>?> predicate, LambdaExpression? sortSelector = null)
    {
        return new GridField<T>
        {
            Key = key, Label = label, Kind = FieldKind.Choice, Options = options, Ops = EqOps, SortSelector = sortSelector,
            Predicate = (c, now) =>
            {
                if (c.Op != FilterOp.Equals) return null;
                var parts = c.ParseList()
                    .Where(v => options.Any(o => string.Equals(o.Value, v, StringComparison.OrdinalIgnoreCase)))
                    .Select(v => predicate(options.First(o => string.Equals(o.Value, v, StringComparison.OrdinalIgnoreCase)).Value, now))
                    .OfType<Expression<Func<T, bool>>>().ToList();
                return parts.Count == 0 ? null : OrElse(parts);
            },
        };
    }

    public static GridField<T> Bool<T>(string key, string label, Expression<Func<T, bool>> selector,
        string trueLabel = "Yes", string falseLabel = "No", bool sortable = true)
    {
        return new GridField<T>
        {
            Key = key, Label = label, Kind = FieldKind.Bool, Ops = EqOps, SortSelector = sortable ? selector : null,
            Options = [new GridOption("true", trueLabel), new GridOption("false", falseLabel)],
            Predicate = (c, _) =>
            {
                if (c.Op != FilterOp.Equals || !bool.TryParse(c.Value, out var wanted)) return null;
                return wanted ? selector : Compose(selector, (Expression<Func<bool, bool>>)(b => !b));
            },
        };
    }

    public static GridField<T> Number<T>(string key, string label, Expression<Func<T, double>> selector, bool sortable = true)
    {
        return new GridField<T>
        {
            Key = key, Label = label, Kind = FieldKind.Number, Ops = NumberOps, SortSelector = sortable ? selector : null,
            Predicate = (c, _) =>
            {
                if (c.ParseDouble() is not { } n) return null;
                return c.Op switch
                {
                    FilterOp.AtLeast => Compose(selector, (Expression<Func<double, bool>>)(v => v >= n)),
                    FilterOp.AtMost => Compose(selector, (Expression<Func<double, bool>>)(v => v <= n)),
                    _ => null,
                };
            },
        };
    }

    // ------------------------------------------------------------------ predicate templates

    private static Expression<Func<T, bool>>? TextPredicate<T>(Expression<Func<T, string?>> selector, FilterClause c)
    {
        if (string.IsNullOrWhiteSpace(c.Value)) return null;
        return c.Op switch
        {
            FilterOp.Contains => Compose(selector, ContainsTemplate(c.Value)),
            FilterOp.Equals => Compose(selector, EqualsTemplate(c.Value)),
            _ => null,
        };
    }

    /// <summary>
    /// Case-insensitive substring. <c>ToLower().Contains(p)</c> translates to <c>strpos(lower(x), @p) &gt; 0</c> in
    /// Npgsql, so '%', '_' and '\' in the value need no escaping, and it also runs in memory for tests.
    /// </summary>
    public static Expression<Func<string?, bool>> ContainsTemplate(string term)
    {
        var lowered = term.Trim().ToLowerInvariant();
        return s => s != null && s.ToLower().Contains(lowered);
    }

    private static Expression<Func<string?, bool>> EqualsTemplate(string term)
    {
        var lowered = term.Trim().ToLowerInvariant();
        return s => s != null && s.ToLower() == lowered;
    }

    private static Expression<Func<TEnum, bool>> EnumEquals<TEnum>(TEnum value) where TEnum : struct, System.Enum =>
        // Built by hand: C# cannot write == on an unconstrained generic enum in a lambda.
        Expression.Lambda<Func<TEnum, bool>>(
            Expression.Equal(Param<TEnum>(out var p), Expression.Constant(value, typeof(TEnum))), p);

    private static ParameterExpression Param<TP>(out ParameterExpression p)
    {
        p = Expression.Parameter(typeof(TP), "e");
        return p;
    }

    private static Expression<Func<T, bool>>? DatePredicate<T>(Expression<Func<T, DateTime?>> selector, FilterClause c, DateTime nowUtc)
    {
        switch (c.Op)
        {
            case FilterOp.OlderThanDays when c.ParseInt() is { } days and >= 0:
            {
                var cutoff = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc).AddDays(-days);
                return Compose(selector, (Expression<Func<DateTime?, bool>>)(d => d != null && d < cutoff));
            }
            case FilterOp.WithinDays when c.ParseInt() is { } days and >= 0:
            {
                var cutoff = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc).AddDays(-days);
                return Compose(selector, (Expression<Func<DateTime?, bool>>)(d => d != null && d >= cutoff));
            }
            case FilterOp.Between:
            {
                var (from, to) = c.ParseRange();
                if (from is null && to is null) return null;
                if (from is not null && to is not null)
                {
                    DateTime f = from.Value, t = to.Value;
                    return Compose(selector, (Expression<Func<DateTime?, bool>>)(d => d != null && d >= f && d <= t));
                }
                if (from is not null)
                {
                    var f = from.Value;
                    return Compose(selector, (Expression<Func<DateTime?, bool>>)(d => d != null && d >= f));
                }
                var only = to!.Value;
                return Compose(selector, (Expression<Func<DateTime?, bool>>)(d => d != null && d <= only));
            }
            default:
                return null;
        }
    }

    // ------------------------------------------------------------------ expression plumbing

    /// <summary>Substitutes <paramref name="selector"/>'s body for the parameter of <paramref name="predicate"/>.</summary>
    public static Expression<Func<T, bool>> Compose<T, TValue>(Expression<Func<T, TValue>> selector, Expression<Func<TValue, bool>> predicate)
    {
        var body = new ReplaceVisitor(predicate.Parameters[0], selector.Body).Visit(predicate.Body);
        return Expression.Lambda<Func<T, bool>>(body, selector.Parameters);
    }

    public static Expression<Func<T, bool>> AndAlso<T>(IReadOnlyList<Expression<Func<T, bool>>> parts) =>
        Combine(parts, Expression.AndAlso);

    public static Expression<Func<T, bool>> OrElse<T>(IReadOnlyList<Expression<Func<T, bool>>> parts) =>
        Combine(parts, Expression.OrElse);

    private static Expression<Func<T, bool>> Combine<T>(IReadOnlyList<Expression<Func<T, bool>>> parts,
        Func<Expression, Expression, BinaryExpression> join)
    {
        if (parts.Count == 1) return parts[0];
        var p = Expression.Parameter(typeof(T), "x");
        Expression? body = null;
        foreach (var part in parts)
        {
            var b = new ReplaceVisitor(part.Parameters[0], p).Visit(part.Body);
            body = body is null ? b : join(body, b);
        }
        return Expression.Lambda<Func<T, bool>>(body!, p);
    }

    /// <summary>"AdLinkState" → "Ad link state", "MatchedByName" → "Matched by name".</summary>
    public static string Humanize(string pascal)
    {
        if (string.IsNullOrEmpty(pascal)) return pascal;
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < pascal.Length; i++)
        {
            var ch = pascal[i];
            if (i > 0 && char.IsUpper(ch) && !char.IsUpper(pascal[i - 1])) sb.Append(' ').Append(char.ToLowerInvariant(ch));
            else sb.Append(ch);
        }
        return sb.ToString();
    }

    private sealed class ReplaceVisitor(ParameterExpression from, Expression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : base.VisitParameter(node);
    }
}
