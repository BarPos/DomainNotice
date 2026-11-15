using DomainNotice.Server.Data;
using DomainNotice.Server.Data.Entities;
using DomainNotice.Server.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DomainNotice.Server.Announcements;

/// <summary>One anonymous answer set waiting to be written. Holds no time, user, computer, delivery or confirmation.</summary>
internal sealed record PendingAnonymousSet(Guid AnnouncementId, IReadOnlyList<PendingAnswer> Answers);

internal sealed record PendingAnswer(Guid QuestionId, string QuestionPrompt, IReadOnlyList<PendingSelection> Selections);

internal sealed record PendingSelection(Guid OptionId, string OptionLabel, int SortOrder);

/// <summary>
/// Plan 8.4: anonymous answers are held in memory and written in shuffled batches, in their own transaction (never
/// the confirmation's, so the rows do not share its xmin), every AnonymousAnswers:FlushMinutes and at shutdown.
/// Keys are explicit random v4 Guids generated at flush time. A crash loses at most one window of anonymous answers,
/// never confirmations.
/// </summary>
internal sealed class AnonymousAnswerBuffer(IDbContextFactory<AppDbContext> dbFactory, ILogger<AnonymousAnswerBuffer> logger)
{
    private readonly Lock _gate = new();
    private List<PendingAnonymousSet> _pending = [];
    private readonly SemaphoreSlim _flushLock = new(1, 1);

    public int Count { get { lock (_gate) return _pending.Count; } }

    public void Add(PendingAnonymousSet set)
    {
        if (set.Answers.Count == 0) return;
        lock (_gate) _pending.Add(set);
    }

    /// <summary>Drops buffered sets of a deleted announcement (they would fail the foreign key at flush).</summary>
    public void Discard(Guid announcementId)
    {
        lock (_gate) _pending.RemoveAll(s => s.AnnouncementId == announcementId);
    }

    /// <summary>Question ids that have buffered (not yet written) answers. Used for the hide-not-delete rule.</summary>
    public IReadOnlySet<Guid> QuestionIdsWithPendingAnswers(Guid announcementId)
    {
        lock (_gate)
            return _pending.Where(s => s.AnnouncementId == announcementId)
                .SelectMany(s => s.Answers).Select(a => a.QuestionId).ToHashSet();
    }

    /// <summary>Writes all buffered sets in one shuffled batch in its own transaction. Returns the number written.</summary>
    public async Task<int> FlushAsync(CancellationToken ct = default)
    {
        await _flushLock.WaitAsync(ct);
        try
        {
            List<PendingAnonymousSet> batch;
            lock (_gate)
            {
                if (_pending.Count == 0) return 0;
                batch = _pending;
                _pending = [];
            }

            try
            {
                await using var db = await dbFactory.CreateDbContextAsync(ct);
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                var announcementIds = batch.Select(s => s.AnnouncementId).Distinct().ToList();
                var existing = (await db.Announcements.Where(a => announcementIds.Contains(a.Id)).Select(a => a.Id).ToListAsync(ct)).ToHashSet();
                var sets = batch.Where(s => existing.Contains(s.AnnouncementId)).ToArray();
                Random.Shared.Shuffle(sets);

                foreach (var pending in sets)
                {
                    var set = new AnswerSet { Id = Guid.NewGuid(), AnnouncementId = pending.AnnouncementId, ConfirmationId = null };
                    foreach (var pa in pending.Answers)
                    {
                        var answer = new Answer { Id = Guid.NewGuid(), AnswerSetId = set.Id, QuestionId = pa.QuestionId, QuestionPrompt = pa.QuestionPrompt };
                        foreach (var ps in pa.Selections)
                            answer.Selections.Add(new AnswerSelection
                            {
                                Id = Guid.NewGuid(), AnswerId = answer.Id, OptionId = ps.OptionId, OptionLabel = ps.OptionLabel, SortOrder = ps.SortOrder,
                            });
                        set.Answers.Add(answer);
                    }
                    db.AnswerSets.Add(set);
                }
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                if (sets.Length != batch.Count)
                    logger.LogInformation("Dropped {Count} anonymous answer sets of deleted announcements", batch.Count - sets.Length);
                return sets.Length;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Writing {Count} anonymous answer sets failed; they stay buffered", batch.Count);
                lock (_gate) _pending.InsertRange(0, batch);
                throw;
            }
        }
        finally
        {
            _flushLock.Release();
        }
    }
}

/// <summary>Flushes the anonymous answer buffer every AnonymousAnswers:FlushMinutes and on shutdown.</summary>
internal sealed class AnonymousAnswerFlusher : BackgroundService
{
    private readonly AnonymousAnswerBuffer buffer;
    private readonly IOptions<AnonymousAnswersOptions> options;
    private readonly TimeProvider time;
    private readonly ILogger<AnonymousAnswerFlusher> logger;

    public AnonymousAnswerFlusher(AnonymousAnswerBuffer buffer, IOptions<AnonymousAnswersOptions> options, TimeProvider time,
        ILogger<AnonymousAnswerFlusher> logger, IHostApplicationLifetime? lifetime = null)
    {
        this.buffer = buffer;
        this.options = options;
        this.time = time;
        this.logger = logger;
        // Hosted services stop in reverse registration order, so the agent link (registered earlier) may still accept
        // Confirms after StopAsync below. One last flush once everything has stopped catches those.
        lifetime?.ApplicationStopped.Register(FinalFlush);
    }

    private void FinalFlush()
    {
        try
        {
            var written = buffer.FlushAsync(CancellationToken.None).GetAwaiter().GetResult();
            if (written > 0) logger.LogInformation("Wrote {Count} anonymous answer sets after shutdown", written);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Anonymous answers could not be written after shutdown and are lost");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var minutes = Math.Max(1, options.Value.FlushMinutes);
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(minutes), time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try { await buffer.FlushAsync(stoppingToken); }
                catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning(ex, "Anonymous answer flush failed"); }
            }
        }
        catch (OperationCanceledException) { }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        try
        {
            var written = await buffer.FlushAsync(CancellationToken.None);
            if (written > 0) logger.LogInformation("Wrote {Count} anonymous answer sets at shutdown", written);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Anonymous answers could not be written at shutdown and are lost");
        }
    }
}
