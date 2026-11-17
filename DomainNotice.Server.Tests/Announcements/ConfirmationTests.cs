using System.Text;
using DomainNotice.Server.Abstractions;
using DomainNotice.Server.Announcements;
using DomainNotice.Server.Data.Entities;
using DomainNotice.Server.Tests.Infrastructure;
using DomainNotice.Shared;
using DomainNotice.Shared.Protocol;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DomainNotice.Server.Tests.Announcements;

public class ConfirmationTests(PostgresFixture pg)
{
    private const string Alice = @"CORP\alice";

    private sealed record Setup(AnnouncementHarness H, FakeConnection Conn, Show Show, Guid AnnouncementId)
    {
        public AnnouncementQuestion Q(int i) => Show.Announcement.Questions[i];
        public Guid Button => Show.Announcement.Buttons[0].Id;
    }

    /// <summary>One announcement with a required single-choice question and an optional multiple-choice one, shown to Alice.</summary>
    private async Task<Setup> ShownAsync(AnnouncementHarness h, bool anonymous = false, bool shown = true,
        UnlockMode mode = UnlockMode.AfterTimeAndRequired)
    {
        var pc = await h.AddComputerAsync("PC-ONE");
        var m = AnnouncementHarness.Model("Survey");
        m.AnonymousAnswers = anonymous;
        m.UnlockMode = mode;
        m.Questions =
        [
            new QuestionEdit { Prompt = "Pick one", Kind = QuestionKind.Single, Required = true,
                Options = [new OptionEdit { Label = "A" }, new OptionEdit { Label = "B" }] },
            new QuestionEdit { Prompt = "Pick many, \"quoted\"", Kind = QuestionKind.Multiple,
                Options = [new OptionEdit { Label = "X" }, new OptionEdit { Label = "Y, with comma" }] },
        ];
        var id = await h.SaveAsync(m);
        var conn = await h.OnlineAsync(pc, Alice);
        var show = Assert.Single(conn.SentOf<Show>());
        if (shown) await h.ShownAsync(conn, show.DeliveryId);
        return new Setup(h, conn, show, id);
    }

    private static AnswerItem Answer(AnnouncementQuestion q, params int[] options) =>
        new() { QuestionId = q.Id, OptionIds = options.Select(i => q.Options[i].Id).ToList() };

    private static async Task<int> CountAsync(AnnouncementHarness h, Func<Data.AppDbContext, Task<int>> f)
    {
        await using var ctx = h.Db.CreateContext();
        return await f(ctx);
    }

    [Fact]
    public async Task Too_early_is_rejected_with_clock_tolerance()
    {
        await using var h = await AnnouncementHarness.CreateAsync(pg);
        var s = await ShownAsync(h);
        h.Time.Advance(TimeSpan.FromSeconds(3));
        var (r, _) = await h.ConfirmAsync(s.Conn, s.Show.DeliveryId, s.Button, Answer(s.Q(0), 0));
        Assert.False(r.Accepted);
        Assert.Equal(ConfirmErrors.TooEarly, r.Error);

        h.Time.Advance(TimeSpan.FromSeconds(5)); // 8 s + 2 s tolerance = 10 s
        Assert.True((await h.ConfirmAsync(s.Conn, s.Show.DeliveryId, s.Button, Answer(s.Q(0), 0))).Result.Accepted);
    }

    [Fact]
    public async Task Confirm_without_shown_is_rejected()
    {
        await using var h = await AnnouncementHarness.CreateAsync(pg);
        var s = await ShownAsync(h, shown: false);
        h.Time.Advance(TimeSpan.FromMinutes(1));
        var (r, _) = await h.ConfirmAsync(s.Conn, s.Show.DeliveryId, s.Button, Answer(s.Q(0), 0));
        Assert.Equal(ConfirmErrors.NotShown, r.Error);
    }

    [Fact]
    public async Task Shown_and_confirm_from_another_computer_are_rejected()
    {
        await using var h = await AnnouncementHarness.CreateAsync(pg);
        var s = await ShownAsync(h, shown: false);
        var other = h.Hub.Connect(await h.AddComputerAsync("PC-TWO")).WithSession(Alice);

        // Shown from the wrong computer is ignored (identity = connection key, never the payload).
        await h.ShownAsync(other, s.Show.DeliveryId);
        Assert.Null((await h.DeliveriesAsync()).Single().ShownAtUtc);
        await h.ShownAsync(s.Conn, s.Show.DeliveryId);
        h.Time.Advance(TimeSpan.FromMinutes(1));

        var (r, _) = await h.ConfirmAsync(other, s.Show.DeliveryId, s.Button, Answer(s.Q(0), 0));
        Assert.False(r.Accepted);
        Assert.Equal(ConfirmErrors.UnknownDelivery, r.Error);
        Assert.Equal(0, await CountAsync(h, c => c.Confirmations.CountAsync(h.Ct)));
        Assert.Null((await h.DeliveriesAsync()).Single().ConfirmedAtUtc);
    }

    [Fact]
    public async Task Missing_required_answer_is_rejected()
    {
        await using var h = await AnnouncementHarness.CreateAsync(pg);
        var s = await ShownAsync(h);
        h.Time.Advance(TimeSpan.FromMinutes(1));
        var (r, _) = await h.ConfirmAsync(s.Conn, s.Show.DeliveryId, s.Button, Answer(s.Q(1), 0));
        Assert.Equal(ConfirmErrors.RequiredMissing, r.Error);
    }

    [Fact]
    public async Task After_time_mode_does_not_require_answers()
    {
        await using var h = await AnnouncementHarness.CreateAsync(pg);
        var s = await ShownAsync(h, mode: UnlockMode.AfterTime);
        h.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.True((await h.ConfirmAsync(s.Conn, s.Show.DeliveryId, s.Button)).Result.Accepted);
    }

    [Fact]
    public async Task Foreign_option_and_too_many_single_options_are_rejected()
    {
        await using var h = await AnnouncementHarness.CreateAsync(pg);
        var s = await ShownAsync(h);
        h.Time.Advance(TimeSpan.FromMinutes(1));

        var crossQuestion = new AnswerItem { QuestionId = s.Q(0).Id, OptionIds = [s.Q(1).Options[0].Id] };
        Assert.Equal(ConfirmErrors.InvalidAnswer, (await h.ConfirmAsync(s.Conn, s.Show.DeliveryId, s.Button, crossQuestion)).Result.Error);

        var unknown = new AnswerItem { QuestionId = s.Q(0).Id, OptionIds = [Guid.NewGuid()] };
        Assert.Equal(ConfirmErrors.InvalidAnswer, (await h.ConfirmAsync(s.Conn, s.Show.DeliveryId, s.Button, unknown)).Result.Error);

        Assert.Equal(ConfirmErrors.TooManyOptions,
            (await h.ConfirmAsync(s.Conn, s.Show.DeliveryId, s.Button, Answer(s.Q(0), 0, 1))).Result.Error);

        Assert.Equal(ConfirmErrors.UnknownButton,
            (await h.ConfirmAsync(s.Conn, s.Show.DeliveryId, Guid.NewGuid(), Answer(s.Q(0), 0))).Result.Error);
        Assert.Equal(0, await CountAsync(h, c => c.Confirmations.CountAsync(h.Ct)));
    }

    [Fact]
    public async Task Ended_announcement_is_rejected()
    {
        await using var h = await AnnouncementHarness.CreateAsync(pg);
        var s = await ShownAsync(h);
        h.Time.Advance(TimeSpan.FromDays(2));
        Assert.Equal(ConfirmErrors.NotActive,
            (await h.ConfirmAsync(s.Conn, s.Show.DeliveryId, s.Button, Answer(s.Q(0), 0))).Result.Error);
    }

    [Fact]
    public async Task Display_limit_rejects_when_already_confirmed_elsewhere()
    {
        await using var h = await AnnouncementHarness.CreateAsync(pg);
        var s = await ShownAsync(h);
        await using (var ctx = h.Db.CreateContext())
        {
            var pc2 = await h.AddComputerAsync("PC-TWO");
            ctx.Deliveries.Add(new Delivery
            {
                Id = Guid.NewGuid(), AnnouncementId = s.AnnouncementId, ComputerId = pc2, UserName = Alice, UserKey = UserKeys.Normalize(Alice),
                SentAtUtc = h.Now.AddHours(-2), ShownAtUtc = h.Now.AddHours(-2), ConfirmedAtUtc = h.Now.AddHours(-2),
            });
            await ctx.SaveChangesAsync(h.Ct);
        }
        h.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(ConfirmErrors.DisplayLimit,
            (await h.ConfirmAsync(s.Conn, s.Show.DeliveryId, s.Button, Answer(s.Q(0), 0))).Result.Error);
    }

    [Fact]
    public async Task Hidden_button_of_open_window_is_still_accepted_and_duplicate_confirm_is_idempotent()
    {
        await using var h = await AnnouncementHarness.CreateAsync(pg);
        var s = await ShownAsync(h);
        var edit = (await h.Service.GetForEditAsync(s.AnnouncementId, h.Ct))!;
        edit.Buttons = [new ButtonEdit { Label = "New button" }];
        await h.SaveAsync(edit);

        h.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.True((await h.ConfirmAsync(s.Conn, s.Show.DeliveryId, s.Button, Answer(s.Q(0), 1))).Result.Accepted);
        Assert.True((await h.ConfirmAsync(s.Conn, s.Show.DeliveryId, s.Button, Answer(s.Q(0), 1))).Result.Accepted);
        await using var ctx = h.Db.CreateContext();
        Assert.Equal("OK", (await ctx.Confirmations.SingleAsync(h.Ct)).ButtonLabel);
    }

    [Fact]
    public async Task Named_answers_are_linked_to_the_confirmation()
    {
        await using var h = await AnnouncementHarness.CreateAsync(pg);
        var s = await ShownAsync(h);
        h.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.True((await h.ConfirmAsync(s.Conn, s.Show.DeliveryId, s.Button, Answer(s.Q(0), 1), Answer(s.Q(1), 0, 1))).Result.Accepted);

        await using var ctx = h.Db.CreateContext();
        var confirmation = await ctx.Confirmations.SingleAsync(h.Ct);
        var set = await ctx.AnswerSets.Include(x => x.Answers).ThenInclude(a => a.Selections).SingleAsync(h.Ct);
        Assert.Equal(confirmation.Id, set.ConfirmationId);
        Assert.Equal(2, set.Answers.Count);
        Assert.Equal(["B"], set.Answers.Single(a => a.QuestionId == s.Q(0).Id).Selections.Select(x => x.OptionLabel));
        Assert.Equal("Pick one", set.Answers.Single(a => a.QuestionId == s.Q(0).Id).QuestionPrompt);
    }

    [Fact]
    public async Task Anonymous_answers_are_unlinked_buffered_and_written_in_another_transaction()
    {
        await using var h = await AnnouncementHarness.CreateAsync(pg);
        var s = await ShownAsync(h, anonymous: true);
        h.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.True((await h.ConfirmAsync(s.Conn, s.Show.DeliveryId, s.Button, Answer(s.Q(0), 0), Answer(s.Q(1), 1))).Result.Accepted);

        // The confirmation is stored at once; the answers are not written until the flush.
        Assert.Equal(1, await CountAsync(h, c => c.Confirmations.CountAsync(h.Ct)));
        Assert.Equal(0, await CountAsync(h, c => c.AnswerSets.CountAsync(h.Ct)));
        Assert.Equal(1, h.Buffer.Count);

        Assert.Equal(1, await h.Buffer.FlushAsync(h.Ct));
        Assert.Equal(0, h.Buffer.Count);

        await using (var ctx = h.Db.CreateContext())
        {
            var set = await ctx.AnswerSets.Include(x => x.Answers).ThenInclude(a => a.Selections).SingleAsync(h.Ct);
            Assert.Null(set.ConfirmationId);
            Assert.Equal(s.AnnouncementId, set.AnnouncementId);
            Assert.Equal(4, set.Id.Version);
            Assert.All(set.Answers, a => Assert.Equal(4, a.Id.Version));
            Assert.All(set.Answers.SelectMany(a => a.Selections), x => Assert.Equal(4, x.Id.Version));
            Assert.Equal(2, set.Answers.Count);
        }

        // Different transactions: xmin of the confirmation differs from every answer row's xmin.
        await using var conn = new NpgsqlConnection(h.Db.ConnectionString);
        await conn.OpenAsync(h.Ct);
        async Task<List<string>> Xmins(string table)
        {
            await using var cmd = new NpgsqlCommand($"SELECT xmin::text FROM \"{table}\"", conn);
            var list = new List<string>();
            await using var reader = await cmd.ExecuteReaderAsync(h.Ct);
            while (await reader.ReadAsync(h.Ct)) list.Add(reader.GetString(0));
            return list;
        }
        var confirmationXmin = Assert.Single(await Xmins("Confirmations"));
        foreach (var table in new[] { "AnswerSets", "Answers", "AnswerSelections" })
        {
            var xmins = await Xmins(table);
            Assert.NotEmpty(xmins);
            Assert.DoesNotContain(confirmationXmin, xmins);
        }
    }

    [Fact]
    public async Task Flush_drops_sets_of_deleted_announcements()
    {
        await using var h = await AnnouncementHarness.CreateAsync(pg);
        var s = await ShownAsync(h, anonymous: true);
        h.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.True((await h.ConfirmAsync(s.Conn, s.Show.DeliveryId, s.Button, Answer(s.Q(0), 0))).Result.Accepted);
        h.Buffer.Add(new PendingAnonymousSet(Guid.NewGuid(), [new PendingAnswer(Guid.NewGuid(), "x", [])]));
        Assert.Equal(1, await h.Buffer.FlushAsync(h.Ct));
    }

    [Fact]
    public async Task Hosted_flusher_writes_on_stop()
    {
        await using var h = await AnnouncementHarness.CreateAsync(pg);
        var s = await ShownAsync(h, anonymous: true);
        h.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.True((await h.ConfirmAsync(s.Conn, s.Show.DeliveryId, s.Button, Answer(s.Q(0), 0))).Result.Accepted);
        var flusher = h.HostedServices.OfType<AnonymousAnswerFlusher>().Single();
        await flusher.StartAsync(h.Ct);
        await flusher.StopAsync(h.Ct);
        Assert.Equal(1, await CountAsync(h, c => c.AnswerSets.CountAsync(h.Ct)));
    }

    // ---------------------------------------------------------------- CSV

    private static string[] Lines(byte[] csv)
    {
        Assert.Equal<byte>([0xEF, 0xBB, 0xBF], csv[..3]);
        return Encoding.UTF8.GetString(csv, 3, csv.Length - 3).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
    }

    [Fact]
    public async Task Csv_named_has_user_computer_time_columns()
    {
        await using var h = await AnnouncementHarness.CreateAsync(pg);
        var s = await ShownAsync(h);
        h.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.True((await h.ConfirmAsync(s.Conn, s.Show.DeliveryId, s.Button, Answer(s.Q(0), 0), Answer(s.Q(1), 0, 1))).Result.Accepted);

        var confirmations = Lines(await h.Export.ExportConfirmationsCsvAsync(s.AnnouncementId, h.Ct));
        Assert.Equal("Announcement,User,Computer,Time,Button", confirmations[0]);
        Assert.StartsWith(@"Survey,CORP\alice,PC-ONE,", confirmations[1]);
        Assert.EndsWith(",OK", confirmations[1]);

        var answers = Lines(await h.Export.ExportAnswersCsvAsync(s.AnnouncementId, h.Ct));
        Assert.Equal("User,Computer,Time,Question,Selected options", answers[0]);
        Assert.Equal(3, answers.Length);
        Assert.StartsWith(@"CORP\alice,PC-ONE,", answers[1]);
        Assert.EndsWith(",Pick one,A", answers[1]);
        Assert.EndsWith(",\"Pick many, \"\"quoted\"\"\",\"X; Y, with comma\"", answers[2]);
    }

    [Fact]
    public async Task Csv_anonymous_has_only_question_and_options_sorted_by_content()
    {
        await using var h = await AnnouncementHarness.CreateAsync(pg);
        var s = await ShownAsync(h, anonymous: true);
        h.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.True((await h.ConfirmAsync(s.Conn, s.Show.DeliveryId, s.Button, Answer(s.Q(0), 1), Answer(s.Q(1), 0))).Result.Accepted);
        // A second user, so there is something to sort.
        s.Conn.WithSession(@"CORP\bob");
        await h.Coordinator.EvaluateAllOnlineAsync(h.Ct);
        var bobShow = s.Conn.SentOf<Show>().Single(x => x.SessionUser == @"CORP\bob");
        await h.ShownAsync(s.Conn, bobShow.DeliveryId);
        h.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.True((await h.ConfirmAsync(s.Conn, bobShow.DeliveryId, s.Button, Answer(s.Q(0), 0))).Result.Accepted);
        await h.Buffer.FlushAsync(h.Ct);

        var lines = Lines(await h.Export.ExportAnswersCsvAsync(s.AnnouncementId, h.Ct));
        Assert.Equal("Question,Selected options", lines[0]);
        Assert.Equal(["\"Pick many, \"\"quoted\"\"\",X", "Pick one,A", "Pick one,B"], lines[1..]);
        Assert.All(lines, l => Assert.DoesNotContain("alice", l));

        // Summary for the Console: counts only.
        var summary = (await h.Results.GetSummaryAsync(s.AnnouncementId, h.Ct))!;
        Assert.Equal(2, summary.Confirmations);
        Assert.Equal(2, summary.Questions[0].AnswerCount);
        Assert.Equal([1, 1], summary.Questions[0].Options.Select(o => o.Count));
        Assert.Equal(1, summary.Questions[1].Options[0].Count);
    }

    [Fact]
    public async Task Csv_uses_semicolon_when_the_language_is_polish()
    {
        await using var h = await AnnouncementHarness.CreateAsync(pg);
        var s = await ShownAsync(h);
        h.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.True((await h.ConfirmAsync(s.Conn, s.Show.DeliveryId, s.Button, Answer(s.Q(0), 0), Answer(s.Q(1), 0, 1))).Result.Accepted);
        var language = new FixedLanguage(AppLanguages.Polish);
        var export = new ResultsExport(h.Db, language);

        var confirmations = Lines(await export.ExportConfirmationsCsvAsync(s.AnnouncementId, h.Ct));
        Assert.Equal("Announcement;User;Computer;Time;Button", confirmations[0]);
        Assert.StartsWith(@"Survey;CORP\alice;PC-ONE;", confirmations[1]);
        Assert.EndsWith(";OK", confirmations[1]);
        var answers = Lines(await export.ExportAnswersCsvAsync(s.AnnouncementId, h.Ct));
        Assert.Equal("User;Computer;Time;Question;Selected options", answers[0]);
        // Joined options contain "; " and are quoted, so they stay one cell.
        Assert.EndsWith(";\"Pick many, \"\"quoted\"\"\";\"X; Y, with comma\"", answers[2]);

        // Read per export: switching back to English gives commas again.
        language.Current = AppLanguages.English;
        Assert.Equal("Announcement,User,Computer,Time,Button", Lines(await export.ExportConfirmationsCsvAsync(s.AnnouncementId, h.Ct))[0]);
    }

    [Fact]
    public void Csv_field_keeps_a_hostile_value_in_one_cell_with_either_separator()
    {
        Assert.Equal("\"x;=cmd|' /C calc'!A0;\"", ResultsExport.CsvField("x;=cmd|' /C calc'!A0;"));
        Assert.Equal("\"'=HYPERLINK(\"\"http://x\"\";\"\"y\"\")\"", ResultsExport.CsvField("=HYPERLINK(\"http://x\";\"y\")"));
        Assert.Equal("'+1", ResultsExport.CsvField("+1"));
        Assert.Equal("'@x", ResultsExport.CsvField("@x"));
        Assert.Equal("\"'\rx\"", ResultsExport.CsvField("\rx"));
        Assert.Equal("\" padded \"", ResultsExport.CsvField(" padded "));
    }

    private sealed class FixedLanguage(string current) : IAppLanguage
    {
        public string Current { get; set; } = current;
        public Task<OperationResult> SetAsync(string language, string actor, CancellationToken ct = default) => throw new NotSupportedException();
        public event Action<string>? Changed { add { } remove { } }
    }

    [Fact]
    public void Csv_field_escaping()
    {
        Assert.Equal("plain", ResultsExport.CsvField("plain"));
        Assert.Equal("\"a,b\"", ResultsExport.CsvField("a,b"));
        Assert.Equal("\"say \"\"hi\"\"\"", ResultsExport.CsvField("say \"hi\""));
        Assert.Equal("\"line\nbreak\"", ResultsExport.CsvField("line\nbreak"));
        Assert.Equal("'=1+1", ResultsExport.CsvField("=1+1"));
        Assert.Equal("", ResultsExport.CsvField(null));
    }
}
