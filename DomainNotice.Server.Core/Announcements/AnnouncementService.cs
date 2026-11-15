using System.Text.Json;
using DomainNotice.Server.Abstractions;
using DomainNotice.Server.Data;
using DomainNotice.Server.Data.Entities;
using DomainNotice.Server.Options;
using DomainNotice.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DomainNotice.Server.Announcements;

/// <summary>
/// Create/edit/delete announcements (plan 8.1). Edits keep button, question and option ids stable; removed buttons
/// and options are hidden; a question with answers is hidden instead of deleted.
/// </summary>
internal sealed class AnnouncementService(
    IDbContextFactory<AppDbContext> dbFactory,
    IAuditLog audit,
    DeliveryCoordinator coordinator,
    AnonymousAnswerBuffer anonymousBuffer,
    IOptions<DirectoryOptions> directoryOptions,
    TimeProvider time,
    ILogger<AnnouncementService> logger) : IAnnouncementService
{
    public const int TitleMax = 200;
    public const int ButtonLabelMax = 100;
    public const int PromptMax = 1000;
    public const int OptionLabelMax = 500;
    public const int TargetValueMax = 512;

    private bool DirectoryConfigured => directoryOptions.Value.IsConfigured;

    public async Task<AnnouncementEditModel?> GetForEditAsync(Guid? id, CancellationToken ct = default)
    {
        if (id is null)
        {
            var now = time.GetUtcNow().UtcDateTime;
            now = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, DateTimeKind.Utc);
            return new AnnouncementEditModel
            {
                StartUtc = now,
                EndUtc = now.AddDays(7),
                MinDismissSeconds = 10,
                UnlockMode = UnlockMode.AfterTimeAndRequired,
                MaxDisplays = 1,
                RepeatIntervalMinutes = 0,
                Buttons = [new ButtonEdit { Label = "OK" }],
            };
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var a = await db.Announcements.AsNoTracking().AsSplitQuery()
            .Include(x => x.Buttons)
            .Include(x => x.Questions).ThenInclude(q => q.Options)
            .Include(x => x.Targets)
            .FirstOrDefaultAsync(x => x.Id == id, ct);
        if (a is null) return null;

        var answered = await AnsweredQuestionIdsAsync(db, a.Id, ct);
        return new AnnouncementEditModel
        {
            Id = a.Id,
            Title = a.Title,
            BodyMarkdown = a.BodyMarkdown,
            MinDismissSeconds = a.MinDismissSeconds,
            UnlockMode = a.UnlockMode,
            AnonymousAnswers = a.AnonymousAnswers,
            StartUtc = DateTime.SpecifyKind(a.StartUtc, DateTimeKind.Utc),
            EndUtc = DateTime.SpecifyKind(a.EndUtc, DateTimeKind.Utc),
            MaxDisplays = a.MaxDisplays,
            RepeatIntervalMinutes = a.RepeatIntervalMinutes,
            IsDraft = a.IsDraft,
            // Hidden buttons/options are "removed" from the editor's point of view; hidden questions stay visible
            // with Hidden = true so they can be shown again.
            Buttons = a.Buttons.Where(b => !b.Hidden).OrderBy(b => b.SortOrder)
                .Select(b => new ButtonEdit { Id = b.Id, Label = b.Label }).ToList(),
            Questions = a.Questions.OrderBy(q => q.SortOrder).Select(q => new QuestionEdit
            {
                Id = q.Id,
                Prompt = q.Prompt,
                Kind = q.Kind,
                Required = q.Required,
                Hidden = q.Hidden,
                HasAnswers = answered.Contains(q.Id),
                Options = q.Options.Where(o => !o.Hidden).OrderBy(o => o.SortOrder)
                    .Select(o => new OptionEdit { Id = o.Id, Label = o.Label }).ToList(),
            }).ToList(),
            Targets = a.Targets.Select(t => new TargetEdit
            {
                Kind = t.Kind, DirectoryObjectGuid = t.DirectoryObjectGuid, ComputerId = t.ComputerId, Value = t.Value, DisplayName = t.DisplayName,
            }).ToList(),
        };
    }

    public IReadOnlyList<string> Validate(AnnouncementEditModel model)
    {
        var errors = new List<string>();
        var title = model.Title?.Trim() ?? "";
        if (title.Length == 0) errors.Add("Title is required.");
        else if (title.Length > TitleMax) errors.Add($"Title must be {TitleMax} characters or fewer.");

        if (model.MinDismissSeconds < 0) errors.Add("Minimum time to dismiss cannot be negative.");
        if (model.MaxDisplays < 1) errors.Add("Number of times shown must be at least 1.");
        if (model.RepeatIntervalMinutes < 0) errors.Add("Repeat interval cannot be negative.");
        if (!(ToUtc(model.StartUtc) < ToUtc(model.EndUtc))) errors.Add("End time must be after the start time.");

        var buttons = model.Buttons ?? [];
        if (buttons.Count == 0) errors.Add("Add at least one button.");
        for (var i = 0; i < buttons.Count; i++)
        {
            var label = buttons[i]?.Label?.Trim() ?? "";
            if (label.Length == 0) errors.Add($"Button {i + 1} needs a label.");
            else if (label.Length > ButtonLabelMax) errors.Add($"Button {i + 1}: the label must be {ButtonLabelMax} characters or fewer.");
        }
        if (buttons.Where(b => b?.Id is not null).GroupBy(b => b.Id).Any(g => g.Count() > 1)) errors.Add("A button is listed twice.");

        var questions = model.Questions ?? [];
        for (var i = 0; i < questions.Count; i++)
        {
            var q = questions[i];
            if (q is null) continue;
            var prompt = q.Prompt?.Trim() ?? "";
            var name = prompt.Length == 0 ? $"Question {i + 1}" : $"Question \"{Shorten(prompt)}\"";
            if (prompt.Length == 0) errors.Add($"Question {i + 1} needs a prompt.");
            else if (prompt.Length > PromptMax) errors.Add($"{name}: the prompt must be {PromptMax} characters or fewer.");
            if (!Enum.IsDefined(q.Kind)) errors.Add($"{name}: unknown question type.");
            var options = q.Options ?? [];
            if (!q.Hidden && options.Count(o => !string.IsNullOrWhiteSpace(o?.Label)) == 0)
                errors.Add($"{name} needs at least one option.");
            for (var j = 0; j < options.Count; j++)
            {
                var label = options[j]?.Label?.Trim() ?? "";
                if (label.Length == 0) errors.Add($"{name}: option {j + 1} needs a label.");
                else if (label.Length > OptionLabelMax) errors.Add($"{name}: option {j + 1} must be {OptionLabelMax} characters or fewer.");
            }
            if (options.Where(o => o?.Id is not null).GroupBy(o => o.Id).Any(g => g.Count() > 1)) errors.Add($"{name}: an option is listed twice.");
        }
        if (questions.Where(q => q?.Id is not null).GroupBy(q => q.Id).Any(g => g.Count() > 1)) errors.Add("A question is listed twice.");

        var targets = model.Targets ?? [];
        for (var i = 0; i < targets.Count; i++)
        {
            var t = targets[i];
            if (t is null) continue;
            var error = ValidateTargetShape(t, i + 1);
            if (error is not null) errors.Add(error);
        }
        return errors;
    }

    private string? ValidateTargetShape(TargetEdit t, int n)
    {
        var hasValue = !string.IsNullOrWhiteSpace(t.Value);
        if (hasValue && t.Value!.Trim().Length > TargetValueMax) return $"Target {n}: the name is too long.";
        switch (t.Kind)
        {
            case TargetKind.Computer:
                return t.ComputerId is null && t.DirectoryObjectGuid is null
                    ? $"Target {n}: choose a computer from the enrolled computers{(DirectoryConfigured ? " or the directory" : "")}."
                    : null;
            case TargetKind.User:
                if (DirectoryConfigured)
                    return t.DirectoryObjectGuid is null ? $"Target {n}: choose the user from the directory." : null;
                return t.DirectoryObjectGuid is null && !hasValue ? $"Target {n}: enter a user name." : null;
            case TargetKind.UserGroup:
            case TargetKind.ComputerGroup:
            case TargetKind.OrganizationalUnit:
                if (DirectoryConfigured)
                    return t.DirectoryObjectGuid is null ? $"Target {n}: choose it from the directory." : null;
                // Without a directory these are accepted but never match (as in OpenNotifier).
                return t.DirectoryObjectGuid is null && !hasValue ? $"Target {n}: nothing selected." : null;
            default:
                return $"Target {n}: unknown target type.";
        }
    }

    /// <summary>Targets must exist: directory objects (when the directory is configured) and enrolled computers.</summary>
    private async Task<List<string>> ValidateTargetsExistAsync(AppDbContext db, AnnouncementEditModel model, CancellationToken ct)
    {
        var errors = new List<string>();
        var targets = (model.Targets ?? []).Where(t => t is not null).ToList();

        var computerIds = targets.Where(t => t.Kind == TargetKind.Computer && t.ComputerId is not null).Select(t => t.ComputerId!.Value).Distinct().ToList();
        var knownComputers = computerIds.Count == 0 ? [] :
            (await db.Computers.AsNoTracking().Where(c => computerIds.Contains(c.Id)).Select(c => c.Id).ToListAsync(ct)).ToHashSet();

        Dictionary<Guid, DirectoryObjectKind> knownObjects = [];
        if (DirectoryConfigured)
        {
            var guids = targets.Where(t => t.DirectoryObjectGuid is not null && !(t.Kind == TargetKind.Computer && t.ComputerId is not null))
                .Select(t => t.DirectoryObjectGuid!.Value).Distinct().ToList();
            if (guids.Count > 0)
                knownObjects = await db.DirectoryObjects.AsNoTracking().Where(o => guids.Contains(o.ObjectGuid) && !o.Deleted)
                    .ToDictionaryAsync(o => o.ObjectGuid, o => o.Kind, ct);
        }

        for (var i = 0; i < targets.Count; i++)
        {
            var t = targets[i];
            var label = string.IsNullOrWhiteSpace(t.DisplayName) ? $"Target {i + 1}" : $"\"{t.DisplayName}\"";
            if (t.Kind == TargetKind.Computer && t.ComputerId is { } cid)
            {
                if (!knownComputers.Contains(cid)) errors.Add($"{label}: that computer is not enrolled.");
                continue;
            }
            if (!DirectoryConfigured || t.DirectoryObjectGuid is not { } g) continue;
            if (!knownObjects.TryGetValue(g, out var kind))
            {
                errors.Add($"{label} was not found in the directory. Choose it from the directory list.");
                continue;
            }
            var kindOk = t.Kind switch
            {
                TargetKind.User => kind == DirectoryObjectKind.User,
                TargetKind.Computer => kind == DirectoryObjectKind.Computer,
                TargetKind.UserGroup or TargetKind.ComputerGroup => kind == DirectoryObjectKind.Group,
                TargetKind.OrganizationalUnit => kind is DirectoryObjectKind.OrganizationalUnit or DirectoryObjectKind.Container or DirectoryObjectKind.Domain,
                _ => false,
            };
            if (!kindOk) errors.Add($"{label} is a {kind}, not a valid {t.Kind} target.");
        }
        return errors;
    }

    public async Task<(OperationResult Result, Guid? Id)> SaveAsync(AnnouncementEditModel model, string actor, CancellationToken ct = default)
    {
        var errors = Validate(model).ToList();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (errors.Count == 0) errors.AddRange(await ValidateTargetsExistAsync(db, model, ct));
        if (errors.Count > 0) return (OperationResult.Fail(string.Join("\n", errors)), null);

        var now = time.GetUtcNow().UtcDateTime;
        Announcement a;
        var isNew = model.Id is null;
        if (isNew)
        {
            a = new Announcement { Id = Guid.NewGuid(), CreatedAtUtc = now, CreatedBy = actor };
            db.Announcements.Add(a);
        }
        else
        {
            var found = await db.Announcements.AsSplitQuery()
                .Include(x => x.Buttons)
                .Include(x => x.Questions).ThenInclude(q => q.Options)
                .Include(x => x.Targets)
                .FirstOrDefaultAsync(x => x.Id == model.Id, ct);
            if (found is null) return (OperationResult.Fail("That announcement no longer exists."), null);
            a = found;

            // Plan 8.4: once a window was sent, its promise (anonymous or named) must hold for every answer to it.
            if (a.AnonymousAnswers != model.AnonymousAnswers
                && (await db.Deliveries.AnyAsync(d => d.AnnouncementId == a.Id, ct)
                    || await db.Confirmations.AnyAsync(c => c.AnnouncementId == a.Id, ct)))
                return (OperationResult.Fail("Anonymous answers cannot be switched on or off after the announcement was sent."), null);
        }

        a.Title = model.Title.Trim();
        a.BodyMarkdown = model.BodyMarkdown ?? "";
        a.MinDismissSeconds = model.MinDismissSeconds;
        a.UnlockMode = model.UnlockMode;
        a.AnonymousAnswers = model.AnonymousAnswers;
        a.StartUtc = ToUtc(model.StartUtc);
        a.EndUtc = ToUtc(model.EndUtc);
        a.MaxDisplays = model.MaxDisplays;
        a.RepeatIntervalMinutes = model.RepeatIntervalMinutes;
        a.IsDraft = model.IsDraft;
        a.UpdatedAtUtc = now;
        a.UpdatedBy = actor;

        ApplyButtons(db, a, model.Buttons ?? []);
        var answered = isNew ? new HashSet<Guid>() : await AnsweredQuestionIdsAsync(db, a.Id, ct);
        ApplyQuestions(db, a, model.Questions ?? [], answered);
        ApplyTargets(db, a, model.Targets ?? []);

        await db.SaveChangesAsync(ct);

        await audit.WriteAsync(actor, isNew ? AuditActions.AnnouncementCreated : AuditActions.AnnouncementUpdated,
            "Announcement", a.Id.ToString(), JsonSerializer.Serialize(new { a.Title, a.IsDraft, a.StartUtc, a.EndUtc }), ct);

        // Deliveries: withdraw if it is no longer active (draft, ended, moved to the future), then evaluate now.
        try { await coordinator.WithdrawInactiveAsync(a.Id, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning(ex, "Withdraw after save failed for {Id}", a.Id); }
        coordinator.RequestEvaluation();
        return (OperationResult.Success, a.Id);
    }

    private static void ApplyButtons(AppDbContext db, Announcement a, List<ButtonEdit> edits)
    {
        var kept = new HashSet<Guid>();
        var sort = 0;
        foreach (var edit in edits)
        {
            var existing = edit.Id is { } id ? a.Buttons.FirstOrDefault(b => b.Id == id) : null;
            if (existing is null)
            {
                existing = new AnnouncementButtonEntity { Id = Guid.NewGuid(), AnnouncementId = a.Id };
                db.AnnouncementButtons.Add(existing);
            }
            existing.Label = edit.Label.Trim();
            existing.SortOrder = sort++;
            existing.Hidden = false;
            kept.Add(existing.Id);
        }
        // Removed buttons are hidden, never deleted: an open window can still confirm with them.
        foreach (var b in a.Buttons.Where(b => !kept.Contains(b.Id)))
        {
            b.Hidden = true;
            b.SortOrder = sort++;
        }
    }

    private static void ApplyQuestions(AppDbContext db, Announcement a, List<QuestionEdit> edits, IReadOnlySet<Guid> answered)
    {
        var kept = new HashSet<Guid>();
        var sort = 0;
        foreach (var edit in edits)
        {
            var existing = edit.Id is { } id ? a.Questions.FirstOrDefault(q => q.Id == id) : null;
            if (existing is null)
            {
                existing = new AnnouncementQuestionEntity { Id = Guid.NewGuid(), AnnouncementId = a.Id };
                db.AnnouncementQuestions.Add(existing);
            }
            existing.Prompt = edit.Prompt.Trim();
            existing.Kind = edit.Kind;
            existing.Required = edit.Required;
            existing.Hidden = edit.Hidden;
            existing.SortOrder = sort++;
            kept.Add(existing.Id);

            var keptOptions = new HashSet<Guid>();
            var optionSort = 0;
            foreach (var oe in edit.Options ?? [])
            {
                var option = oe.Id is { } oid ? existing.Options.FirstOrDefault(o => o.Id == oid) : null;
                if (option is null)
                {
                    option = new QuestionOption { Id = Guid.NewGuid(), QuestionId = existing.Id };
                    db.QuestionOptions.Add(option);
                }
                option.Label = oe.Label.Trim();
                option.SortOrder = optionSort++;
                option.Hidden = false;
                keptOptions.Add(option.Id);
            }
            foreach (var o in existing.Options.Where(o => !keptOptions.Contains(o.Id)))
            {
                o.Hidden = true;
                o.SortOrder = optionSort++;
            }
        }

        foreach (var q in a.Questions.Where(q => !kept.Contains(q.Id)).ToList())
        {
            if (answered.Contains(q.Id))
            {
                // A question with answers cannot be deleted, only hidden (plan 8.1).
                q.Hidden = true;
                q.SortOrder = sort++;
            }
            else
            {
                db.AnnouncementQuestions.Remove(q);
            }
        }
    }

    private static void ApplyTargets(AppDbContext db, Announcement a, List<TargetEdit> edits)
    {
        db.AnnouncementTargets.RemoveRange(a.Targets);
        foreach (var t in edits.Where(t => t is not null))
        {
            db.AnnouncementTargets.Add(new AnnouncementTarget
            {
                Id = Guid.NewGuid(),
                AnnouncementId = a.Id,
                Kind = t.Kind,
                DirectoryObjectGuid = t.DirectoryObjectGuid,
                ComputerId = t.Kind == TargetKind.Computer ? t.ComputerId : null,
                Value = string.IsNullOrWhiteSpace(t.Value) ? null : t.Value.Trim(),
                DisplayName = (t.DisplayName ?? "").Trim(),
            });
        }
    }

    /// <summary>Questions with stored answers, plus anonymous answers still in the buffer.</summary>
    private async Task<IReadOnlySet<Guid>> AnsweredQuestionIdsAsync(AppDbContext db, Guid announcementId, CancellationToken ct)
    {
        var stored = await db.Answers.AsNoTracking()
            .Where(x => x.AnswerSet!.AnnouncementId == announcementId)
            .Select(x => x.QuestionId).Distinct().ToListAsync(ct);
        var set = stored.ToHashSet();
        set.UnionWith(anonymousBuffer.QuestionIdsWithPendingAnswers(announcementId));
        return set;
    }

    public async Task<OperationResult> DeleteAsync(Guid id, string actor, CancellationToken ct = default)
    {
        List<(Guid, Guid)> open;
        string title;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var a = await db.Announcements.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (a is null) return OperationResult.Fail("That announcement no longer exists.");
            title = a.Title;
            open = (await db.Deliveries.AsNoTracking()
                    .Where(d => d.AnnouncementId == id && d.ConfirmedAtUtc == null && d.WithdrawnAtUtc == null)
                    .Select(d => new { d.Id, d.ComputerId }).ToListAsync(ct))
                .Select(d => (d.Id, d.ComputerId)).ToList();
            // Cascade: buttons, questions, options, targets, deliveries, confirmations, answer sets.
            db.Announcements.Remove(a);
            await db.SaveChangesAsync(ct);
        }
        anonymousBuffer.Discard(id);
        await audit.WriteAsync(actor, AuditActions.AnnouncementDeleted, "Announcement", id.ToString(),
            JsonSerializer.Serialize(new { Title = title }), ct);
        await coordinator.SendWithdrawAsync(open, ct);
        return OperationResult.Success;
    }

    public string RenderPreviewHtml(string markdown) => MarkdownPreview.ToHtml(markdown);

    private static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    private static string Shorten(string s) => s.Length <= 40 ? s : s[..40] + "…";
}
