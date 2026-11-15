using DomainNotice.Console.Grid;
using DomainNotice.Console.Lists;
using DomainNotice.Server.Data;
using DomainNotice.Server.Data.Entities;
using DomainNotice.Server.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace DomainNotice.Server.Tests.Console;

/// <summary>Plan 8.4 in the Console: anonymous answers have no user/computer/time and are ordered by content only.</summary>
public class AnonymousAnswersListTests(PostgresFixture pg)
{
    private static readonly DateTime Now = new(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly string[] Forbidden = ["user", "computer", "time", "confirmed", "id", "delivery", "confirmation"];

    [Fact]
    public async Task Offers_only_announcement_question_and_option()
    {
        var db = await pg.CreateDatabaseAsync(createSchema: false);
        await using var ctx = db.CreateContext();
        var list = ConsoleLists.AnonymousAnswers;
        var fields = list.GetFields(new GridContext(ctx, new Dictionary<string, string?>(), Now));

        Assert.Equal(["announcement", "question", "option"], fields.Select(f => f.Key));
        Assert.Equal(["announcement", "question", "option"], list.Columns.Select(c => c.Key));
        Assert.All(fields, f => Assert.Equal(FieldKind.Text, f.Kind));
        Assert.False(list.Selectable);

        // The row type itself carries nothing that could identify who, where, when or the storage order.
        var props = typeof(AnonymousAnswerRow).GetProperties().Select(p => p.Name.ToLowerInvariant()).ToList();
        Assert.Equal(["announcementid", "announcementtitle", "question", "option"], props);
        Assert.DoesNotContain(props, p => p is "id" || Forbidden.Where(f => f != "id").Any(p.Contains));
    }

    [Fact]
    public async Task Sql_orders_by_content_and_never_by_id_even_when_asked()
    {
        var db = await pg.CreateDatabaseAsync(createSchema: false);
        await using var ctx = db.CreateContext();
        var gctx = new GridContext(ctx, new Dictionary<string, string?>(), Now);

        foreach (var q in new[]
                 {
                     new GridQuery(),
                     new GridQuery { Sort = "id" },
                     new GridQuery { Sort = "time", Descending = true },
                     new GridQuery { Sort = "option", Descending = true },
                     // User/computer/time filters are not offered, so they are ignored.
                     new GridQuery { Filters = [new("user", FilterOp.Contains, "alice"), new("time", FilterOp.WithinDays, "1")] },
                 })
        {
            var sql = (await ConsoleLists.AnonymousAnswers.BuildPageQueryAsync(gctx, q)).ToQueryString();
            var orderBy = sql[sql.LastIndexOf("ORDER BY", StringComparison.Ordinal)..];
            Assert.Contains("\"Title\"", orderBy);
            Assert.Contains("\"QuestionPrompt\"", orderBy);
            Assert.Contains("\"OptionLabel\"", orderBy);
            Assert.DoesNotContain("\"Id\"", orderBy);
            Assert.DoesNotContain("ctid", sql);
            Assert.DoesNotContain("\"UserName\"", sql);
            Assert.DoesNotContain("\"ConfirmedAtUtc\"", sql);
            Assert.DoesNotContain("alice", sql);
        }
    }

    [Fact]
    public async Task Lists_only_anonymous_sets_sorted_by_content_and_named_list_only_named()
    {
        var db = await pg.CreateDatabaseAsync();
        var announcementId = Guid.NewGuid();
        var questionId = Guid.NewGuid();
        await using (var ctx = db.CreateContext())
        {
            var a = new Announcement { Id = announcementId, Title = "Survey", StartUtc = Now, EndUtc = Now.AddDays(1), AnonymousAnswers = true };
            ctx.Announcements.Add(a);
            // Inserted deliberately out of content order.
            foreach (var label in new[] { "Zebra", "Apple", "Mango" })
                ctx.AnswerSets.Add(AnonymousSet(announcementId, questionId, label));

            var confirmation = new Confirmation
            {
                Id = Guid.NewGuid(), AnnouncementId = announcementId, ComputerId = Guid.NewGuid(), ComputerName = "PC-1",
                UserName = @"CORP\bob", UserKey = @"corp\bob", ButtonId = Guid.NewGuid(), ButtonLabel = "OK", ConfirmedAtUtc = Now,
            };
            ctx.Confirmations.Add(confirmation);
            var named = AnonymousSet(announcementId, questionId, "Named choice");
            named.ConfirmationId = confirmation.Id;
            ctx.AnswerSets.Add(named);
            await ctx.SaveChangesAsync(Ct);
        }

        await using (var ctx = db.CreateContext())
        {
            var gctx = new GridContext(ctx, new Dictionary<string, string?> { ["announcement"] = announcementId.ToString() }, Now);
            var anon = await ConsoleLists.AnonymousAnswers.QueryAsync(gctx, new GridQuery(), Ct);
            Assert.Equal(["Apple", "Mango", "Zebra"], anon.Rows.Select(r => r.Option));

            var desc = await ConsoleLists.AnonymousAnswers.QueryAsync(gctx, new GridQuery { Sort = "option", Descending = true }, Ct);
            Assert.Equal(["Zebra", "Mango", "Apple"], desc.Rows.Select(r => r.Option));

            var filtered = await ConsoleLists.AnonymousAnswers.QueryAsync(gctx,
                new GridQuery { Filters = [new("option", FilterOp.Contains, "an")] }, Ct);
            Assert.Equal(["Mango"], filtered.Rows.Select(r => r.Option));

            var namedRows = await ConsoleLists.NamedAnswers.QueryAsync(gctx, new GridQuery(), Ct);
            var row = Assert.Single(namedRows.Rows);
            Assert.Equal("Named choice", row.Option);
            Assert.Equal(@"CORP\bob", row.UserName);

            var csv = System.Text.Encoding.UTF8.GetString(await ConsoleLists.AnonymousAnswers.ExportCsvAsync(gctx, new GridQuery(), Ct));
            Assert.StartsWith("﻿Announcement,Question,Option\r\n", csv);
            Assert.DoesNotContain("bob", csv);
            Assert.DoesNotContain("Named choice", csv);
        }
    }

    private static AnswerSet AnonymousSet(Guid announcementId, Guid questionId, string option)
    {
        var answerId = Guid.NewGuid();
        return new AnswerSet
        {
            Id = Guid.NewGuid(), AnnouncementId = announcementId,
            Answers =
            [
                new Answer
                {
                    Id = answerId, QuestionId = questionId, QuestionPrompt = "Favourite fruit?",
                    Selections = [new AnswerSelection { Id = Guid.NewGuid(), OptionId = Guid.NewGuid(), OptionLabel = option }],
                },
            ],
        };
    }
}
