using DomainNotice.Server.Abstractions;
using DomainNotice.Server.Data.Entities;
using DomainNotice.Server.Tests.Infrastructure;
using DomainNotice.Shared;
using DomainNotice.Shared.Protocol;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DomainNotice.Server.Tests.Integration;

/// <summary>
/// Whole-server loopback: AddDomainNoticeCore (every area), the real TLS listener, and a fake agent speaking the
/// Shared protocol. Covers enrol → Hello → Show → Shown → Confirm → ConfirmResult (plan 15, step 2 + 4).
/// </summary>
public class EndToEndTests(PostgresFixture pg)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AnnouncementEditModel Announcement(string title, bool anonymous = false, bool withQuestion = false)
    {
        var now = DateTime.UtcNow;
        var model = new AnnouncementEditModel
        {
            Title = title,
            BodyMarkdown = "**Hello** world",
            MinDismissSeconds = 0,
            UnlockMode = UnlockMode.AfterTime,
            AnonymousAnswers = anonymous,
            StartUtc = now.AddMinutes(-1),
            EndUtc = now.AddDays(1),
            MaxDisplays = 1,
            Buttons = [new ButtonEdit { Label = "OK" }],
        };
        if (withQuestion)
            model.Questions.Add(new QuestionEdit
            {
                Prompt = "Coffee?", Kind = QuestionKind.Single, Required = true,
                Options = [new OptionEdit { Label = "Yes" }, new OptionEdit { Label = "No" }],
            });
        return model;
    }

    [Fact]
    public async Task Enrol_hello_show_shown_confirm_round_trip()
    {
        var db = await pg.CreateDatabaseAsync();
        await using var server = await TestAgentServer.StartAsync(db, fullCore: true);

        var (saved, id) = await server.Get<IAnnouncementService>().SaveAsync(Announcement("Maintenance tonight"), "tester", Ct);
        Assert.True(saved.Ok, saved.Error);

        var (agent, computerId) = await server.ConnectEnrolledAsync("PC-E2E", [TestAgentClient.Session(1, @"CORP\alice")]);
        await using var _ = agent;

        var show = await agent.ReceiveAsync<Show>(TimeSpan.FromSeconds(20), Ct);
        Assert.Equal(id, show.Announcement.Id);
        Assert.Equal(@"CORP\alice", show.SessionUser, ignoreCase: true);
        Assert.Equal("Maintenance tonight", show.Announcement.Title);

        await agent.SendAsync(new Shown { DeliveryId = show.DeliveryId }, ct: Ct);
        await TestAgentServer.WaitUntilAsync(async () =>
        {
            await using var ctx = db.CreateContext();
            return await ctx.Deliveries.AnyAsync(d => d.Id == show.DeliveryId && d.ShownAtUtc != null, Ct);
        });

        var confirmId = await agent.SendAsync(new Confirm
        {
            DeliveryId = show.DeliveryId,
            ButtonId = show.Announcement.Buttons[0].Id,
        }, ct: Ct);
        var (env, result) = await agent.ReceiveEnvelopeAsync<ConfirmResult>(TimeSpan.FromSeconds(10), Ct);
        Assert.True(result.Accepted, result.Error);
        Assert.Equal(confirmId, env.ReplyTo);

        await using (var ctx = db.CreateContext())
        {
            var confirmation = await ctx.Confirmations.SingleAsync(Ct);
            Assert.Equal(computerId, confirmation.ComputerId); // from the TLS key, not the payload
            Assert.Equal(@"corp\alice", confirmation.UserKey);
            Assert.Equal("OK", confirmation.ButtonLabel);
            var computer = await ctx.Computers.SingleAsync(Ct);
            Assert.True(computer.Online);
            Assert.Equal("PC-E2E", computer.Name);
        }

        // Shown once, confirmed once: nothing further is pushed for MaxDisplays = 1.
        await Task.Delay(500, Ct);
        Assert.DoesNotContain(agent.Pending, f => f.Message is Show);
    }

    [Fact]
    public async Task Anonymous_answers_are_unlinked_and_written_only_on_flush()
    {
        var db = await pg.CreateDatabaseAsync();
        await using var server = await TestAgentServer.StartAsync(db, fullCore: true);
        var (saved, _) = await server.Get<IAnnouncementService>().SaveAsync(Announcement("Survey", anonymous: true, withQuestion: true), "tester", Ct);
        Assert.True(saved.Ok, saved.Error);

        var (agent, _) = await server.ConnectEnrolledAsync("PC-ANON", [TestAgentClient.Session(3, @"CORP\bob")]);
        await using var __ = agent;
        var show = await agent.ReceiveAsync<Show>(TimeSpan.FromSeconds(20), Ct);
        await agent.SendAsync(new Shown { DeliveryId = show.DeliveryId }, ct: Ct);
        var question = show.Announcement.Questions.Single();
        await TestAgentServer.WaitUntilAsync(async () =>
        {
            await using var ctx = db.CreateContext();
            return await ctx.Deliveries.AnyAsync(d => d.ShownAtUtc != null, Ct);
        });
        await agent.SendAsync(new Confirm
        {
            DeliveryId = show.DeliveryId,
            ButtonId = show.Announcement.Buttons[0].Id,
            Answers = [new AnswerItem { QuestionId = question.Id, OptionIds = [question.Options[0].Id] }],
        }, ct: Ct);
        Assert.True((await agent.ReceiveAsync<ConfirmResult>(TimeSpan.FromSeconds(10), Ct)).Accepted);

        await using (var ctx = db.CreateContext())
        {
            Assert.Equal(1, await ctx.Confirmations.CountAsync(Ct));
            Assert.Equal(0, await ctx.AnswerSets.CountAsync(Ct)); // held in memory until the batch flush
        }

        // Stopping the host flushes the buffer (plan 8.4: "and at shutdown").
        await server.Host.StopAsync(Ct);
        await using (var ctx = db.CreateContext())
        {
            var set = await ctx.AnswerSets.Include(s => s.Answers).ThenInclude(a => a.Selections).SingleAsync(Ct);
            Assert.Null(set.ConfirmationId);
            Assert.Equal(4, set.Id.Version);
            Assert.Equal("Yes", set.Answers.Single().Selections.Single().OptionLabel);
        }
    }

    [Fact]
    public async Task Payload_names_cannot_confirm_for_another_computer()
    {
        var db = await pg.CreateDatabaseAsync();
        await using var server = await TestAgentServer.StartAsync(db, fullCore: true);
        await server.Get<IAnnouncementService>().SaveAsync(Announcement("Only mine"), "tester", Ct);

        var (a, _) = await server.ConnectEnrolledAsync("PC-A", [TestAgentClient.Session(1, @"CORP\carol")]);
        await using var _a = a;
        var showA = await a.ReceiveAsync<Show>(TimeSpan.FromSeconds(20), Ct);

        // A second machine that claims the same name tries to confirm A's delivery.
        var (b, _) = await server.ConnectEnrolledAsync("PC-A", []);
        await using var _b = b;
        await b.SendAsync(new Shown { DeliveryId = showA.DeliveryId }, ct: Ct);
        await b.SendAsync(new Confirm { DeliveryId = showA.DeliveryId, ButtonId = showA.Announcement.Buttons[0].Id }, ct: Ct);
        var rejected = await b.ReceiveAsync<ConfirmResult>(TimeSpan.FromSeconds(10), Ct);
        Assert.False(rejected.Accepted);

        await using var ctx = db.CreateContext();
        Assert.Equal(0, await ctx.Confirmations.CountAsync(Ct));
        Assert.Null((await ctx.Deliveries.SingleAsync(d => d.Id == showA.DeliveryId, Ct)).ShownAtUtc);
    }
}
