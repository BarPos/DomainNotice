using DomainNotice.Server.Abstractions;
using DomainNotice.Server.Data;
using DomainNotice.Server.Data.Entities;
using DomainNotice.Shared;
using DomainNotice.Shared.Protocol;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DomainNotice.Server.Announcements;

/// <summary>Reasons sent back in ConfirmResult.Error.</summary>
public static class ConfirmErrors
{
    public const string UnknownDelivery = "This announcement is no longer available.";
    public const string Withdrawn = "This announcement was withdrawn.";
    public const string NotShown = "The window was not reported as shown.";
    public const string NotActive = "This announcement is not active.";
    public const string UnknownButton = "That button is not part of this announcement.";
    public const string InvalidAnswer = "An answer does not belong to this announcement.";
    public const string TooManyOptions = "Only one option may be chosen for a single-choice question.";
    public const string RequiredMissing = "A required question was not answered.";
    public const string TooEarly = "The announcement cannot be dismissed yet.";
    public const string DisplayLimit = "This announcement was already confirmed as often as allowed.";
    public const string ServerError = "The server could not store the confirmation. Please try again.";
}

/// <summary>
/// Handles Shown and Confirm (plan 8.3, P1). The delivery must belong to the connection's computer: the computer is
/// identified only by its TLS key (plan 3.4), never by the payload.
/// </summary>
internal sealed class ConfirmationHandler(
    IDbContextFactory<AppDbContext> dbFactory,
    DeliveryCoordinator coordinator,
    AnonymousAnswerBuffer anonymousBuffer,
    TimeProvider time,
    ILogger<ConfirmationHandler> logger) : IAgentMessageHandler
{
    /// <summary>Clock tolerance between agent and server for the minimum display time.</summary>
    public static readonly TimeSpan ClockTolerance = TimeSpan.FromSeconds(2);

    public IReadOnlyCollection<string> MessageTypes { get; } = [Shown.Type, Confirm.Type];

    public Task HandleAsync(IAgentConnection connection, Envelope envelope, IAgentMessage message, CancellationToken ct) =>
        message switch
        {
            Shown shown => HandleShownAsync(connection, shown, ct),
            Confirm confirm => HandleConfirmAsync(connection, envelope, confirm, ct),
            _ => Task.CompletedTask,
        };

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    internal async Task HandleShownAsync(IAgentConnection connection, Shown shown, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = Now;
        var updated = await db.Deliveries
            .Where(d => d.Id == shown.DeliveryId && d.ComputerId == connection.ComputerId && d.ShownAtUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.ShownAtUtc, now), ct);
        if (updated == 0)
            logger.LogDebug("Shown {DeliveryId} from {ComputerId} ignored (unknown, other computer, or already shown)",
                shown.DeliveryId, connection.ComputerId);
    }

    internal async Task HandleConfirmAsync(IAgentConnection connection, Envelope envelope, Confirm confirm, CancellationToken ct)
    {
        var computerId = connection.ComputerId;
        using (await coordinator.LockComputerAsync(computerId, ct))
        {
            bool accepted;
            string? error;
            Delivery? delivery;
            try
            {
                (accepted, error, delivery) = await ConfirmLockedAsync(computerId, confirm, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Confirm {DeliveryId} from {ComputerId} failed", confirm.DeliveryId, computerId);
                (accepted, error, delivery) = (false, ConfirmErrors.ServerError, null);
            }
            await connection.SendAsync(new ConfirmResult { DeliveryId = confirm.DeliveryId, Accepted = accepted, Error = error },
                envelope.Id, ct);
            if (!accepted || delivery is null) return;

            // The user confirmed: other open windows of this announcement for the same user are withdrawn (they would
            // otherwise bypass the repeat interval or the display limit).
            await using (var db = await dbFactory.CreateDbContextAsync(ct))
            {
                var others = await db.Deliveries.AsNoTracking()
                    .Where(d => d.AnnouncementId == delivery.AnnouncementId && d.UserKey == delivery.UserKey && d.Id != delivery.Id
                                && d.ConfirmedAtUtc == null && d.WithdrawnAtUtc == null)
                    .Select(d => new { d.Id, d.ComputerId }).ToListAsync(ct);
                if (others.Count > 0)
                    await coordinator.WithdrawAsync(db, others.Select(o => (o.Id, o.ComputerId)).ToList(), ct);
            }

            // Push the next due announcement.
            await coordinator.EvaluateLockedAsync(computerId, connection.Sessions, false, null, ct);
        }
    }

    /// <summary>Caller holds the computer lock. Runs the P1 checks and stores the confirmation.</summary>
    private async Task<(bool Accepted, string? Error, Delivery? Delivery)> ConfirmLockedAsync(Guid computerId, Confirm confirm, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var delivery = await db.Deliveries.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == confirm.DeliveryId && d.ComputerId == computerId, ct);
        if (delivery is null)
        {
            logger.LogWarning("Confirm for delivery {DeliveryId} refused: not a delivery of computer {ComputerId}", confirm.DeliveryId, computerId);
            return Reject(ConfirmErrors.UnknownDelivery);
        }

        using var userLock = await coordinator.LockUserAsync(delivery.UserKey, ct);
        delivery = await db.Deliveries.AsNoTracking().FirstAsync(d => d.Id == delivery.Id, ct);
        if (delivery.ConfirmedAtUtc is not null) return (true, null, null); // duplicate (lost ConfirmResult): idempotent
        if (delivery.WithdrawnAtUtc is not null) return Reject(ConfirmErrors.Withdrawn);
        if (delivery.ShownAtUtc is not { } shownAt) return Reject(ConfirmErrors.NotShown);

        var announcement = await db.Announcements.AsNoTracking().AsSplitQuery()
            .Include(a => a.Buttons)
            .Include(a => a.Questions).ThenInclude(q => q.Options)
            .FirstOrDefaultAsync(a => a.Id == delivery.AnnouncementId, ct);
        if (announcement is null) return Reject(ConfirmErrors.UnknownDelivery);

        var now = Now;
        if (announcement.StatusAt(now) != AnnouncementStatus.Active) return Reject(ConfirmErrors.NotActive);

        // Hidden buttons and options are still accepted: ids are stable across edits (plan 8.1).
        var button = announcement.Buttons.FirstOrDefault(b => b.Id == confirm.ButtonId);
        if (button is null) return Reject(ConfirmErrors.UnknownButton);

        var (answersOk, answerError, answers) = BuildAnswers(announcement, confirm.Answers ?? []);
        if (!answersOk) return Reject(answerError!);

        var requiredSatisfied = DismissRules.RequiredSatisfied(announcement.Questions.Where(q => !q.Hidden)
            .Select(q => new RequiredQuestion(q.Required, answers.FirstOrDefault(a => a.QuestionId == q.Id)?.Selections.Count ?? 0)));
        if (announcement.UnlockMode != UnlockMode.AfterTime && !requiredSatisfied) return Reject(ConfirmErrors.RequiredMissing);
        var elapsed = now - shownAt + ClockTolerance;
        if (!DismissRules.CanSubmit(announcement.UnlockMode, announcement.MinDismissSeconds, elapsed, requiredSatisfied))
            return Reject(ConfirmErrors.TooEarly);

        // Display limit (P1/P2, per user across computers, D8): other shown deliveries that were already confirmed.
        var confirmedElsewhere = await db.Deliveries.CountAsync(d => d.AnnouncementId == announcement.Id && d.UserKey == delivery.UserKey
            && d.Id != delivery.Id && d.ShownAtUtc != null && d.ConfirmedAtUtc != null, ct);
        if (confirmedElsewhere >= announcement.MaxDisplays) return Reject(ConfirmErrors.DisplayLimit);

        var computerName = await db.Computers.AsNoTracking().Where(c => c.Id == computerId).Select(c => c.Name).FirstOrDefaultAsync(ct) ?? "";

        var confirmation = new Confirmation
        {
            Id = Guid.NewGuid(),
            AnnouncementId = announcement.Id,
            DeliveryId = delivery.Id,
            ComputerId = computerId,              // from the connection (plan 3.4)
            ComputerName = computerName,
            UserName = delivery.UserName,         // from the delivery (plan 8.3)
            UserKey = delivery.UserKey,
            ButtonId = button.Id,
            ButtonLabel = button.Label,
            ConfirmedAtUtc = now,
        };

        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            var marked = await db.Deliveries
                .Where(d => d.Id == delivery.Id && d.ConfirmedAtUtc == null && d.WithdrawnAtUtc == null)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.ConfirmedAtUtc, now), ct);
            if (marked == 0)
            {
                await tx.RollbackAsync(ct);
                return Reject(ConfirmErrors.Withdrawn);
            }
            db.Confirmations.Add(confirmation);
            if (!announcement.AnonymousAnswers && answers.Count > 0)
            {
                var set = new AnswerSet { Id = Guid.NewGuid(), AnnouncementId = announcement.Id, ConfirmationId = confirmation.Id };
                foreach (var a in answers)
                {
                    var answer = new Answer { Id = Guid.NewGuid(), AnswerSetId = set.Id, QuestionId = a.QuestionId, QuestionPrompt = a.QuestionPrompt };
                    foreach (var s in a.Selections)
                        answer.Selections.Add(new AnswerSelection
                        {
                            Id = Guid.NewGuid(), AnswerId = answer.Id, OptionId = s.OptionId, OptionLabel = s.OptionLabel, SortOrder = s.SortOrder,
                        });
                    set.Answers.Add(answer);
                }
                db.AnswerSets.Add(set);
            }
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        // Anonymous answers leave this transaction entirely (plan 8.4): buffered, written later in shuffled batches.
        if (announcement.AnonymousAnswers && answers.Count > 0)
            anonymousBuffer.Add(new PendingAnonymousSet(announcement.Id, answers));

        logger.LogInformation("Confirmation of {AnnouncementId} by {User} on {ComputerId}", announcement.Id, delivery.UserName, computerId);
        return (true, null, delivery);

        static (bool, string?, Delivery?) Reject(string error) => (false, error, null);
    }

    /// <summary>
    /// Checks the answers: each option must belong to its question (no cross-question options), at most one option for
    /// Single, no duplicate questions. Answers to questions that no longer exist (deleted after the window opened) are
    /// ignored. Selections are snapshotted with the current labels.
    /// </summary>
    internal static (bool Ok, string? Error, List<PendingAnswer> Answers) BuildAnswers(Announcement announcement, IReadOnlyList<AnswerItem> items)
    {
        var result = new List<PendingAnswer>();
        var seen = new HashSet<Guid>();
        foreach (var item in items)
        {
            if (item is null) continue;
            var question = announcement.Questions.FirstOrDefault(q => q.Id == item.QuestionId);
            if (question is null) continue;
            if (!seen.Add(question.Id)) return (false, ConfirmErrors.InvalidAnswer, result);

            var optionIds = (item.OptionIds ?? []).Distinct().ToList();
            var options = new List<QuestionOption>();
            foreach (var id in optionIds)
            {
                var option = question.Options.FirstOrDefault(o => o.Id == id);
                if (option is null) return (false, ConfirmErrors.InvalidAnswer, result);
                options.Add(option);
            }
            if (question.Kind == QuestionKind.Single && options.Count > 1) return (false, ConfirmErrors.TooManyOptions, result);
            if (options.Count == 0) continue;

            result.Add(new PendingAnswer(question.Id, question.Prompt,
                options.OrderBy(o => o.SortOrder).Select((o, i) => new PendingSelection(o.Id, o.Label, i)).ToList()));
        }
        return (true, null, result);
    }
}
