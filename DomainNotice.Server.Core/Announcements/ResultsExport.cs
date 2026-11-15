using System.Globalization;
using System.Text;
using DomainNotice.Server.Abstractions;
using DomainNotice.Server.Data;
using DomainNotice.Shared;
using Microsoft.EntityFrameworkCore;

namespace DomainNotice.Server.Announcements;

// ---------------------------------------------------------------- Console results API (public)

public sealed record OptionSummary(Guid OptionId, string Label, bool Hidden, int Count);

public sealed record QuestionSummary(Guid QuestionId, string Prompt, QuestionKind Kind, bool Required, bool Hidden,
    int AnswerCount, IReadOnlyList<OptionSummary> Options);

public sealed record ButtonSummary(Guid ButtonId, string Label, bool Hidden, int Count);

/// <summary>
/// Totals for the results page. Safe for anonymous announcements: counts per option only, no user, computer or time.
/// Anonymous answers still in the memory buffer (not yet flushed, plan 8.4) are not included.
/// </summary>
public sealed record AnnouncementResultsSummary(
    Guid AnnouncementId,
    bool AnonymousAnswers,
    int Confirmations,
    int DistinctUsers,
    int DeliveriesSent,
    int DeliveriesShown,
    int DeliveriesOpen,
    IReadOnlyList<ButtonSummary> Buttons,
    IReadOnlyList<QuestionSummary> Questions);

public interface IAnnouncementResults
{
    Task<AnnouncementResultsSummary?> GetSummaryAsync(Guid announcementId, CancellationToken ct = default);
}

/// <summary>CSV exports, UTF-8 with BOM. Times are server local time "yyyy-MM-dd HH:mm:ss".</summary>
internal sealed class ResultsExport(IDbContextFactory<AppDbContext> dbFactory) : IResultsExport, IAnnouncementResults
{
    private static readonly Encoding Utf8Bom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

    public async Task<byte[]> ExportConfirmationsCsvAsync(Guid announcementId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var title = await db.Announcements.AsNoTracking().Where(a => a.Id == announcementId).Select(a => a.Title).FirstOrDefaultAsync(ct) ?? "";
        var rows = await db.Confirmations.AsNoTracking()
            .Where(c => c.AnnouncementId == announcementId)
            .OrderBy(c => c.ConfirmedAtUtc).ThenBy(c => c.UserKey)
            .Select(c => new { c.UserName, c.ComputerName, c.ConfirmedAtUtc, c.ButtonLabel })
            .ToListAsync(ct);

        var sb = new StringBuilder();
        AppendRow(sb, "Announcement", "User", "Computer", "Time", "Button");
        foreach (var r in rows)
            AppendRow(sb, title, r.UserName, r.ComputerName, Display(r.ConfirmedAtUtc), r.ButtonLabel);
        return Encode(sb);
    }

    public async Task<byte[]> ExportAnswersCsvAsync(Guid announcementId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var a = await db.Announcements.AsNoTracking().Include(x => x.Questions)
            .FirstOrDefaultAsync(x => x.Id == announcementId, ct);
        var sb = new StringBuilder();
        if (a is null)
        {
            AppendRow(sb, "Question", "Selected options");
            return Encode(sb);
        }
        var questionOrder = a.Questions.ToDictionary(q => q.Id, q => q.SortOrder);

        if (a.AnonymousAnswers)
        {
            // Plan 8.4: no user/computer/time, never joined with confirmations, sorted by content (not row order).
            var answers = await db.Answers.AsNoTracking()
                .Where(x => x.AnswerSet!.AnnouncementId == announcementId && x.AnswerSet.ConfirmationId == null)
                .Select(x => new { x.QuestionPrompt, Selections = x.Selections.OrderBy(s => s.SortOrder).Select(s => s.OptionLabel).ToList() })
                .ToListAsync(ct);
            AppendRow(sb, "Question", "Selected options");
            foreach (var r in answers
                         .Select(x => (Question: x.QuestionPrompt, Selected: string.Join("; ", x.Selections)))
                         .OrderBy(x => x.Question, StringComparer.Ordinal).ThenBy(x => x.Selected, StringComparer.Ordinal))
                AppendRow(sb, r.Question, r.Selected);
            return Encode(sb);
        }

        var named = await db.Answers.AsNoTracking()
            .Where(x => x.AnswerSet!.AnnouncementId == announcementId && x.AnswerSet.ConfirmationId != null)
            .Select(x => new
            {
                x.QuestionId,
                x.QuestionPrompt,
                x.AnswerSet!.Confirmation!.UserName,
                x.AnswerSet.Confirmation.ComputerName,
                x.AnswerSet.Confirmation.ConfirmedAtUtc,
                Selections = x.Selections.OrderBy(s => s.SortOrder).Select(s => s.OptionLabel).ToList(),
            })
            .ToListAsync(ct);
        AppendRow(sb, "User", "Computer", "Time", "Question", "Selected options");
        foreach (var r in named.OrderBy(x => x.ConfirmedAtUtc).ThenBy(x => x.UserName, StringComparer.Ordinal)
                     .ThenBy(x => questionOrder.GetValueOrDefault(x.QuestionId, int.MaxValue)).ThenBy(x => x.QuestionPrompt, StringComparer.Ordinal))
            AppendRow(sb, r.UserName, r.ComputerName, Display(r.ConfirmedAtUtc), r.QuestionPrompt, string.Join("; ", r.Selections));
        return Encode(sb);
    }

    public async Task<AnnouncementResultsSummary?> GetSummaryAsync(Guid announcementId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var a = await db.Announcements.AsNoTracking().AsSplitQuery()
            .Include(x => x.Buttons)
            .Include(x => x.Questions).ThenInclude(q => q.Options)
            .FirstOrDefaultAsync(x => x.Id == announcementId, ct);
        if (a is null) return null;

        var confirmations = await db.Confirmations.CountAsync(c => c.AnnouncementId == announcementId, ct);
        var distinctUsers = await db.Confirmations.Where(c => c.AnnouncementId == announcementId).Select(c => c.UserKey).Distinct().CountAsync(ct);
        var buttonCounts = await db.Confirmations.Where(c => c.AnnouncementId == announcementId)
            .GroupBy(c => c.ButtonId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, ct);
        var sent = await db.Deliveries.CountAsync(d => d.AnnouncementId == announcementId, ct);
        var shown = await db.Deliveries.CountAsync(d => d.AnnouncementId == announcementId && d.ShownAtUtc != null, ct);
        var open = await db.Deliveries.CountAsync(d => d.AnnouncementId == announcementId && d.ConfirmedAtUtc == null && d.WithdrawnAtUtc == null, ct);

        // Only the answers that match the announcement's mode (anonymous sets have no confirmation).
        var answerQuery = db.Answers.Where(x => x.AnswerSet!.AnnouncementId == announcementId
                                                && (a.AnonymousAnswers ? x.AnswerSet.ConfirmationId == null : x.AnswerSet.ConfirmationId != null));
        var answerCounts = await answerQuery.GroupBy(x => x.QuestionId).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Key, g => g.Count, ct);
        var optionCounts = await answerQuery.SelectMany(x => x.Selections).GroupBy(s => s.OptionId)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, ct);

        return new AnnouncementResultsSummary(a.Id, a.AnonymousAnswers, confirmations, distinctUsers, sent, shown, open,
            a.Buttons.OrderBy(b => b.SortOrder).Select(b => new ButtonSummary(b.Id, b.Label, b.Hidden, buttonCounts.GetValueOrDefault(b.Id))).ToList(),
            a.Questions.OrderBy(q => q.SortOrder).Select(q => new QuestionSummary(q.Id, q.Prompt, q.Kind, q.Required, q.Hidden,
                answerCounts.GetValueOrDefault(q.Id),
                q.Options.OrderBy(o => o.SortOrder).Select(o => new OptionSummary(o.Id, o.Label, o.Hidden, optionCounts.GetValueOrDefault(o.Id))).ToList()))
                .ToList());
    }

    /// <summary>UTF-8 with BOM (GetBytes alone does not write the preamble).</summary>
    private static byte[] Encode(StringBuilder sb) => [.. Utf8Bom.GetPreamble(), .. Utf8Bom.GetBytes(sb.ToString())];

    private static void AppendRow(StringBuilder sb, params string?[] fields)
    {
        for (var i = 0; i < fields.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(CsvField(fields[i]));
        }
        sb.Append("\r\n");
    }

    /// <summary>RFC 4180 quoting. Fields starting with = + - @ are prefixed with ' against spreadsheet formula injection.</summary>
    public static string CsvField(string? value)
    {
        value ??= "";
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
            value = "'" + value;
        if (value.IndexOfAny(['"', ',', '\n', '\r', ';']) >= 0 || value != value.Trim())
            return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        return value;
    }

    private static string Display(DateTime utc)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), TimeZoneInfo.Local);
        return local.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    }
}
