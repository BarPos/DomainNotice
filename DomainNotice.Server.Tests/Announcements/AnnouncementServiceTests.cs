using DomainNotice.Server.Abstractions;
using DomainNotice.Server.Announcements;
using DomainNotice.Server.Data.Entities;
using DomainNotice.Server.Tests.Infrastructure;
using DomainNotice.Shared;
using DomainNotice.Shared.Protocol;
using Microsoft.EntityFrameworkCore;

namespace DomainNotice.Server.Tests.Announcements;

public class AnnouncementServiceTests(PostgresFixture pg)
{
    [Fact]
    public async Task New_announcement_defaults()
    {
        await using var h = await AnnouncementHarness.CreateAsync(pg);
        var m = (await h.Service.GetForEditAsync(null, h.Ct))!;
        Assert.Null(m.Id);
        Assert.Equal(h.Now, m.StartUtc);
        Assert.Equal(h.Now.AddDays(7), m.EndUtc);
        Assert.Equal("OK", Assert.Single(m.Buttons).Label);
        Assert.Equal(10, m.MinDismissSeconds);
        Assert.Equal(UnlockMode.AfterTimeAndRequired, m.UnlockMode);
        Assert.Equal(1, m.MaxDisplays);
    }

    [Fact]
    public async Task Validation_reports_errors()
    {
        await using var h = await AnnouncementHarness.CreateAsync(pg);
        var m = new AnnouncementEditModel
        {
            Title = new string('x', 201),
            StartUtc = h.Now, EndUtc = h.Now,
            MaxDisplays = 0, RepeatIntervalMinutes = -1,
            Buttons = [],
            Questions = [new QuestionEdit { Prompt = "" , Options = [] }],
            Targets = [new TargetEdit { Kind = TargetKind.Computer }],
        };
        var errors = h.Service.Validate(m);
        Assert.Contains(errors, e => e.Contains("Title"));
        Assert.Contains(errors, e => e.Contains("End time"));
        Assert.Contains(errors, e => e.Contains("at least 1"));
        Assert.Contains(errors, e => e.Contains("Repeat interval"));
        Assert.Contains(errors, e => e.Contains("at least one button"));
        Assert.Contains(errors, e => e.Contains("needs a prompt"));
        Assert.Contains(errors, e => e.Contains("at least one option"));
        Assert.Contains(errors, e => e.Contains("choose a computer"));

        var (result, id) = await h.Service.SaveAsync(m, "tester", h.Ct);
        Assert.False(result.Ok);
        Assert.Null(id);
        Assert.Empty(h.Service.Validate(AnnouncementHarness.Model()));
    }

    [Fact]
    public async Task Targets_must_exist()
    {
        await using var h = await AnnouncementHarness.CreateAsync(pg, directory: true);
        var (r1, _) = await h.Service.SaveAsync(AnnouncementHarness.Model(targets:
            new TargetEdit { Kind = TargetKind.UserGroup, DirectoryObjectGuid = Guid.NewGuid(), DisplayName = "Ghosts" }), "t", h.Ct);
        Assert.False(r1.Ok);
        Assert.Contains("not found in the directory", r1.Error);

        var (r2, _) = await h.Service.SaveAsync(AnnouncementHarness.Model(targets:
            new TargetEdit { Kind = TargetKind.Computer, ComputerId = Guid.NewGuid() }), "t", h.Ct);
        Assert.False(r2.Ok);
        Assert.Contains("not enrolled", r2.Error);

        var user = await h.AddDirectoryObjectAsync(DirectoryObjectKind.User, "CN=Alice,OU=Staff,DC=corp,DC=example", "alice");
        var (r3, _) = await h.Service.SaveAsync(AnnouncementHarness.Model(targets:
            new TargetEdit { Kind = TargetKind.UserGroup, DirectoryObjectGuid = user }), "t", h.Ct);
        Assert.False(r3.Ok); // a user is not a group

        var (r4, _) = await h.Service.SaveAsync(AnnouncementHarness.Model(targets:
            new TargetEdit { Kind = TargetKind.User, Value = "alice" }), "t", h.Ct);
        Assert.False(r4.Ok); // with a directory, users must be picked from it

        var pc = await h.AddComputerAsync("PC1");
        await h.SaveAsync(AnnouncementHarness.Model(targets: [
            new TargetEdit { Kind = TargetKind.User, DirectoryObjectGuid = user },
            new TargetEdit { Kind = TargetKind.Computer, ComputerId = pc }]));
    }

    [Fact]
    public async Task Save_audits_create_update_delete()
    {
        await using var h = await AnnouncementHarness.CreateAsync(pg);
        var id = await h.SaveAsync(AnnouncementHarness.Model());
        var m = (await h.Service.GetForEditAsync(id, h.Ct))!;
        m.Title = "Changed";
        await h.SaveAsync(m);
        Assert.True((await h.Service.DeleteAsync(id, "tester", h.Ct)).Ok);

        await using var ctx = h.Db.CreateContext();
        var actions = await ctx.AuditLog.OrderBy(a => a.Id).Select(a => a.Action).ToListAsync(h.Ct);
        Assert.Equal([AuditActions.AnnouncementCreated, AuditActions.AnnouncementUpdated, AuditActions.AnnouncementDeleted], actions);
        Assert.False(await ctx.Announcements.AnyAsync(h.Ct));
    }

    [Fact]
    public async Task Edit_keeps_ids_and_hides_removed_buttons_and_options()
    {
        await using var h = await AnnouncementHarness.CreateAsync(pg);
        var model = AnnouncementHarness.Model();
        model.Buttons = [new ButtonEdit { Label = "Yes" }, new ButtonEdit { Label = "No" }];
        model.Questions = [new QuestionEdit
        {
            Prompt = "Colour?", Kind = QuestionKind.Single,
            Options = [new OptionEdit { Label = "Red" }, new OptionEdit { Label = "Blue" }],
        }];
        var id = await h.SaveAsync(model);
        var before = await h.LoadAnnouncementAsync(id);
        var yes = before.Buttons.Single(b => b.Label == "Yes").Id;
        var no = before.Buttons.Single(b => b.Label == "No").Id;
        var q = before.Questions.Single();
        var red = q.Options.Single(o => o.Label == "Red").Id;
        var blue = q.Options.Single(o => o.Label == "Blue").Id;

        var edit = (await h.Service.GetForEditAsync(id, h.Ct))!;
        edit.Buttons = [new ButtonEdit { Id = yes, Label = "Yes!" }, new ButtonEdit { Label = "Maybe" }];
        edit.Questions[0].Prompt = "Favourite colour?";
        edit.Questions[0].Options = [new OptionEdit { Id = blue, Label = "Blue" }, new OptionEdit { Label = "Green" }];
        await h.SaveAsync(edit);

        var after = await h.LoadAnnouncementAsync(id);
        Assert.Equal("Yes!", after.Buttons.Single(b => b.Id == yes).Label);
        Assert.False(after.Buttons.Single(b => b.Id == yes).Hidden);
        Assert.True(after.Buttons.Single(b => b.Id == no).Hidden);
        Assert.Contains(after.Buttons, b => b.Label == "Maybe" && !b.Hidden);
        var q2 = after.Questions.Single();
        Assert.Equal(q.Id, q2.Id);
        Assert.Equal("Favourite colour?", q2.Prompt);
        Assert.True(q2.Options.Single(o => o.Id == red).Hidden);
        Assert.False(q2.Options.Single(o => o.Id == blue).Hidden);
        Assert.Contains(q2.Options, o => o.Label == "Green" && !o.Hidden);

        // The editor no longer sees hidden buttons/options; the Show payload does not carry them either.
        var reload = (await h.Service.GetForEditAsync(id, h.Ct))!;
        Assert.DoesNotContain(reload.Buttons, b => b.Id == no);
        var payload = DeliveryCoordinator.ToPayload(after);
        Assert.Equal(["Yes!", "Maybe"], payload.Buttons.Select(b => b.Label));
        Assert.Equal(["Blue", "Green"], payload.Questions.Single().Options.Select(o => o.Label));
    }

    [Fact]
    public async Task Question_with_answers_is_hidden_not_deleted()
    {
        await using var h = await AnnouncementHarness.CreateAsync(pg);
        var model = AnnouncementHarness.Model();
        model.Questions =
        [
            new QuestionEdit { Prompt = "Answered", Options = [new OptionEdit { Label = "A" }] },
            new QuestionEdit { Prompt = "Unanswered", Options = [new OptionEdit { Label = "B" }] },
        ];
        var id = await h.SaveAsync(model);
        var a = await h.LoadAnnouncementAsync(id);
        var answered = a.Questions.Single(q => q.Prompt == "Answered");
        await using (var ctx = h.Db.CreateContext())
        {
            var set = new AnswerSet { Id = Guid.NewGuid(), AnnouncementId = id };
            set.Answers.Add(new Answer { Id = Guid.NewGuid(), QuestionId = answered.Id, QuestionPrompt = "Answered" });
            ctx.AnswerSets.Add(set);
            await ctx.SaveChangesAsync(h.Ct);
        }

        var edit = (await h.Service.GetForEditAsync(id, h.Ct))!;
        Assert.True(edit.Questions.Single(q => q.Id == answered.Id).HasAnswers);
        Assert.False(edit.Questions.Single(q => q.Prompt == "Unanswered").HasAnswers);
        edit.Questions = [];
        await h.SaveAsync(edit);

        var after = await h.LoadAnnouncementAsync(id);
        var kept = Assert.Single(after.Questions);
        Assert.Equal(answered.Id, kept.Id);
        Assert.True(kept.Hidden);

        // Hidden questions stay in the editor (Hidden = true) and can be shown again.
        var again = (await h.Service.GetForEditAsync(id, h.Ct))!;
        Assert.True(Assert.Single(again.Questions).Hidden);
        again.Questions[0].Hidden = false;
        await h.SaveAsync(again);
        Assert.False((await h.LoadAnnouncementAsync(id)).Questions.Single().Hidden);
    }

    [Fact]
    public async Task Buffered_anonymous_answers_count_as_answers()
    {
        await using var h = await AnnouncementHarness.CreateAsync(pg);
        var model = AnnouncementHarness.Model();
        model.AnonymousAnswers = true;
        model.Questions = [new QuestionEdit { Prompt = "Q", Options = [new OptionEdit { Label = "A" }] }];
        var id = await h.SaveAsync(model);
        var q = (await h.LoadAnnouncementAsync(id)).Questions.Single();
        h.Buffer.Add(new PendingAnonymousSet(id, [new PendingAnswer(q.Id, "Q", [new PendingSelection(q.Options[0].Id, "A", 0)])]));

        var edit = (await h.Service.GetForEditAsync(id, h.Ct))!;
        Assert.True(edit.Questions.Single().HasAnswers);
        edit.Questions = [];
        await h.SaveAsync(edit);
        Assert.True((await h.LoadAnnouncementAsync(id)).Questions.Single().Hidden);
    }

    [Fact]
    public async Task Anonymous_flag_cannot_change_after_confirmations()
    {
        await using var h = await AnnouncementHarness.CreateAsync(pg);
        var id = await h.SaveAsync(AnnouncementHarness.Model());
        await using (var ctx = h.Db.CreateContext())
        {
            ctx.Confirmations.Add(new Confirmation { Id = Guid.NewGuid(), AnnouncementId = id, ComputerId = Guid.NewGuid(), ConfirmedAtUtc = h.Now });
            await ctx.SaveChangesAsync(h.Ct);
        }
        var edit = (await h.Service.GetForEditAsync(id, h.Ct))!;
        edit.AnonymousAnswers = true;
        var (r, _) = await h.Service.SaveAsync(edit, "t", h.Ct);
        Assert.False(r.Ok);
    }

    [Fact]
    public async Task Anonymous_flag_cannot_change_once_a_window_was_sent()
    {
        await using var h = await AnnouncementHarness.CreateAsync(pg);
        var pc = await h.AddComputerAsync("PC1");
        var m = AnnouncementHarness.Model();
        m.AnonymousAnswers = true;
        var id = await h.SaveAsync(m);
        var conn = await h.OnlineAsync(pc, @"CORP\alice");
        Assert.Single(conn.SentOf<Show>());

        var edit = (await h.Service.GetForEditAsync(id, h.Ct))!;
        edit.AnonymousAnswers = false;
        var (r, _) = await h.Service.SaveAsync(edit, "t", h.Ct);
        Assert.False(r.Ok);
        Assert.True((await h.LoadAnnouncementAsync(id)).AnonymousAnswers);
    }

    [Fact]
    public async Task Status_follows_draft_and_time()
    {
        await using var h = await AnnouncementHarness.CreateAsync(pg);
        var m = AnnouncementHarness.Model(start: h.Now.AddHours(1), end: h.Now.AddHours(2));
        m.IsDraft = true;
        var id = await h.SaveAsync(m);
        var a = await h.LoadAnnouncementAsync(id);
        Assert.Equal(AnnouncementStatus.Draft, a.StatusAt(h.Now));
        a.IsDraft = false;
        Assert.Equal(AnnouncementStatus.Scheduled, a.StatusAt(h.Now));
        Assert.Equal(AnnouncementStatus.Active, a.StatusAt(h.Now.AddMinutes(90)));
        Assert.Equal(AnnouncementStatus.Ended, a.StatusAt(h.Now.AddHours(3)));
    }

    [Fact]
    public async Task Delete_cascades_and_withdraws_open_deliveries()
    {
        await using var h = await AnnouncementHarness.CreateAsync(pg);
        var pc = await h.AddComputerAsync("PC1");
        var id = await h.SaveAsync(AnnouncementHarness.Model());
        var conn = await h.OnlineAsync(pc, @"CORP\alice");
        var show = Assert.Single(conn.SentOf<Show>());

        Assert.True((await h.Service.DeleteAsync(id, "tester", h.Ct)).Ok);
        Assert.Equal(show.DeliveryId, Assert.Single(conn.SentOf<Withdraw>()).DeliveryId);
        Assert.Empty(await h.DeliveriesAsync());
    }

    [Theory]
    [InlineData("[x](javascript:alert(1))")]
    [InlineData("[x](JavaScript:alert(1))")]
    [InlineData("[x](data:text/html;base64,PHNjcmlwdD4=)")]
    [InlineData("![x](data:image/png;base64,AAAA)")]
    [InlineData("[x](/relative)")]
    [InlineData("<javascript:alert(1)>")]
    public void Preview_drops_non_http_links(string markdown)
    {
        var html = MarkdownPreview.ToHtml(markdown);
        Assert.DoesNotContain("href", html);
        Assert.DoesNotContain("src", html);
    }

    [Fact]
    public void Preview_keeps_http_links_and_escapes_html()
    {
        var html = MarkdownPreview.ToHtml("**Hi** [site](https://example.com/a) ![i](http://example.com/i.png) <script>alert(1)</script>");
        Assert.Contains("<strong>Hi</strong>", html);
        Assert.Contains("href=\"https://example.com/a\"", html);
        Assert.Contains("src=\"http://example.com/i.png\"", html);
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("site", MarkdownPreview.ToHtml("[site](javascript:x)"));
    }
}
