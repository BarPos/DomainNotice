using DomainNotice.Agent.Logic;
using DomainNotice.Shared;
using DomainNotice.Shared.LocalIpc;
using DomainNotice.Shared.Protocol;

namespace DomainNotice.Agent.Tests;

internal sealed class FakeEndpoint(int sessionId, string user) : ISessionEndpoint
{
    public int SessionId { get; } = sessionId;
    public string UserName { get; } = user;
    public List<ILocalPayload> Sent { get; } = [];

    public Task SendAsync<T>(T message, CancellationToken ct) where T : ILocalMessage
    {
        lock (Sent) Sent.Add(message);
        return Task.CompletedTask;
    }

    public List<T> Of<T>() => Sent.OfType<T>().ToList();
}

internal sealed class FakeServer : IServerSender
{
    public List<(Guid Id, object Message)> Sent { get; } = [];
    public bool Fail { get; set; }

    public Task<Guid?> TrySendAsync<T>(T message, CancellationToken ct) where T : IAgentMessage
    {
        if (Fail) return Task.FromResult<Guid?>(null);
        var id = Guid.NewGuid();
        Sent.Add((id, message));
        return Task.FromResult<Guid?>(id);
    }

    public List<T> Of<T>() => Sent.Select(s => s.Message).OfType<T>().ToList();
}

public sealed class RouterTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly Guid ButtonOk = Guid.NewGuid();
    private static readonly Guid Question = Guid.NewGuid();
    private static readonly Guid OptionA = Guid.NewGuid();
    private static readonly Guid OptionB = Guid.NewGuid();

    private static Show MakeShow(string user, Guid? id = null) => new()
    {
        DeliveryId = id ?? Guid.NewGuid(),
        SessionUser = user,
        Announcement = new AnnouncementPayload
        {
            Id = Guid.NewGuid(),
            Title = "Maintenance",
            BodyMarkdown = "**Tonight**",
            Buttons = [new AnnouncementButton { Id = ButtonOk, Label = "OK" }],
            Questions =
            [
                new AnnouncementQuestion
                {
                    Id = Question, Prompt = "Pick", Kind = QuestionKind.Single,
                    Options = [new AnnouncementOption { Id = OptionA, Label = "A" }, new AnnouncementOption { Id = OptionB, Label = "B" }],
                },
            ],
        },
    };

    private static ConnectionStatus Up => new() { Connected = true, ServerName = "srv" };
    private static ConnectionStatus Down => new() { Connected = false, ServerName = "srv" };

    private static async Task<(AnnouncementRouter Router, FakeServer Server)> CreateConnected()
    {
        var router = new AnnouncementRouter(TimeProvider.System);
        var server = new FakeServer();
        await router.SetLinkAsync(server, Up, Ct);
        return (router, server);
    }

    [Fact]
    public async Task Show_goes_only_to_sessions_of_the_matching_user_case_insensitive()
    {
        var (router, _) = await CreateConnected();
        var alice = new FakeEndpoint(1, @"CORP\alice");
        var bob = new FakeEndpoint(2, @"CORP\bob");
        await router.OnSessionConnectedAsync(alice, Ct);
        await router.OnSessionConnectedAsync(bob, Ct);

        var show = MakeShow(@"corp\ALICE");
        await router.OnServerShowAsync(show, Ct);

        Assert.Single(alice.Of<ShowAnnouncement>(), s => s.DeliveryId == show.DeliveryId);
        Assert.Empty(bob.Of<ShowAnnouncement>());
        Assert.Equal(1, router.PendingCount);
    }

    [Fact]
    public async Task Pending_delivery_is_resent_when_the_session_reconnects()
    {
        var (router, _) = await CreateConnected();
        var show = MakeShow(@"CORP\alice");
        await router.OnServerShowAsync(show, Ct); // nobody connected yet

        var first = new FakeEndpoint(1, @"CORP\alice");
        await router.OnSessionConnectedAsync(first, Ct);
        Assert.IsType<ConnectionStatus>(first.Sent[0]);
        Assert.Single(first.Of<ShowAnnouncement>());

        router.OnSessionDisconnected(first); // e.g. killed in Task Manager
        var restarted = new FakeEndpoint(1, @"CORP\alice");
        await router.OnSessionConnectedAsync(restarted, Ct);
        await router.OnSessionMessageAsync(restarted, new RequestPending(), Ct);
        Assert.Equal(2, restarted.Of<ShowAnnouncement>().Count);
        Assert.Equal(2, restarted.Of<ConnectionStatus>().Count);
    }

    [Fact]
    public async Task Shown_is_forwarded_once_and_only_for_own_deliveries()
    {
        var (router, server) = await CreateConnected();
        var alice = new FakeEndpoint(1, @"CORP\alice");
        var bob = new FakeEndpoint(2, @"CORP\bob");
        await router.OnSessionConnectedAsync(alice, Ct);
        await router.OnSessionConnectedAsync(bob, Ct);
        var show = MakeShow(@"CORP\alice");
        await router.OnServerShowAsync(show, Ct);

        await router.OnSessionMessageAsync(bob, new ShownReport { DeliveryId = show.DeliveryId }, Ct);
        Assert.Empty(server.Of<Shown>());

        await router.OnSessionMessageAsync(alice, new ShownReport { DeliveryId = show.DeliveryId }, Ct);
        await router.OnSessionMessageAsync(alice, new ShownReport { DeliveryId = show.DeliveryId }, Ct);
        Assert.Single(server.Of<Shown>(), s => s.DeliveryId == show.DeliveryId);
    }

    [Fact]
    public async Task Shown_while_offline_is_sent_when_the_link_comes_back()
    {
        var router = new AnnouncementRouter(TimeProvider.System);
        var server = new FakeServer();
        await router.SetLinkAsync(server, Up, Ct);
        var alice = new FakeEndpoint(1, @"CORP\alice");
        await router.OnSessionConnectedAsync(alice, Ct);
        var show = MakeShow(@"CORP\alice");
        await router.OnServerShowAsync(show, Ct);

        await router.SetLinkAsync(null, Down, Ct);
        await router.OnSessionMessageAsync(alice, new ShownReport { DeliveryId = show.DeliveryId }, Ct);
        Assert.Empty(server.Of<Shown>());

        await router.SetLinkAsync(server, Up, Ct);
        Assert.Single(server.Of<Shown>());
        Assert.Contains(alice.Of<ConnectionStatus>(), s => !s.Connected);
    }

    [Fact]
    public async Task Confirm_is_forwarded_with_filtered_answers_and_acceptance_closes_all_windows_of_the_user()
    {
        var (router, server) = await CreateConnected();
        var console = new FakeEndpoint(1, @"CORP\alice");
        var rdp = new FakeEndpoint(3, @"CORP\Alice");
        await router.OnSessionConnectedAsync(console, Ct);
        await router.OnSessionConnectedAsync(rdp, Ct);
        var show = MakeShow(@"CORP\alice");
        await router.OnServerShowAsync(show, Ct);

        await router.OnSessionMessageAsync(console, new ConfirmRequest
        {
            DeliveryId = show.DeliveryId,
            ButtonId = ButtonOk,
            Answers =
            [
                new AnswerItem { QuestionId = Question, OptionIds = [OptionA, OptionB, Guid.NewGuid()] },
                new AnswerItem { QuestionId = Guid.NewGuid(), OptionIds = [OptionA] },
            ],
        }, Ct);

        var (envelopeId, message) = Assert.Single(server.Sent, s => s.Message is Confirm);
        var confirm = (Confirm)message;
        Assert.Equal(ButtonOk, confirm.ButtonId);
        var answer = Assert.Single(confirm.Answers);
        Assert.Equal([OptionA], answer.OptionIds); // single-choice: one option, unknown ids dropped

        await router.OnServerConfirmResultAsync(new ConfirmResult { DeliveryId = show.DeliveryId, Accepted = true }, envelopeId, Ct);
        Assert.Single(console.Of<ConfirmReply>(), r => r.Accepted);
        Assert.Single(rdp.Of<ConfirmReply>(), r => r.Accepted);
        Assert.Equal(0, router.PendingCount);
    }

    [Fact]
    public async Task Rejected_confirm_keeps_the_delivery_pending_and_answers_only_the_sender()
    {
        var (router, server) = await CreateConnected();
        var console = new FakeEndpoint(1, @"CORP\alice");
        var rdp = new FakeEndpoint(3, @"CORP\alice");
        await router.OnSessionConnectedAsync(console, Ct);
        await router.OnSessionConnectedAsync(rdp, Ct);
        var show = MakeShow(@"CORP\alice");
        await router.OnServerShowAsync(show, Ct);
        await router.OnSessionMessageAsync(console, new ConfirmRequest { DeliveryId = show.DeliveryId, ButtonId = ButtonOk }, Ct);
        var envelopeId = server.Sent.Single(s => s.Message is Confirm).Id;

        await router.OnServerConfirmResultAsync(new ConfirmResult { DeliveryId = show.DeliveryId, Accepted = false, Error = "too early" }, envelopeId, Ct);

        var reply = Assert.Single(console.Of<ConfirmReply>());
        Assert.False(reply.Accepted);
        Assert.Equal("too early", reply.Error);
        Assert.Empty(rdp.Of<ConfirmReply>());
        Assert.Equal(1, router.PendingCount);
    }

    [Fact]
    public async Task Confirm_while_offline_is_rejected_locally()
    {
        var router = new AnnouncementRouter(TimeProvider.System);
        var alice = new FakeEndpoint(1, @"CORP\alice");
        await router.OnSessionConnectedAsync(alice, Ct);
        var server = new FakeServer();
        await router.SetLinkAsync(server, Up, Ct);
        var show = MakeShow(@"CORP\alice");
        await router.OnServerShowAsync(show, Ct);
        await router.SetLinkAsync(null, Down, Ct);

        await router.OnSessionMessageAsync(alice, new ConfirmRequest { DeliveryId = show.DeliveryId, ButtonId = ButtonOk }, Ct);

        var reply = Assert.Single(alice.Of<ConfirmReply>());
        Assert.False(reply.Accepted);
        Assert.Equal(AnnouncementRouter.NotConnectedError, reply.Error);
        Assert.Empty(server.Of<Confirm>());
    }

    [Fact]
    public async Task Link_loss_rejects_confirms_in_flight()
    {
        var (router, _) = await CreateConnected();
        var alice = new FakeEndpoint(1, @"CORP\alice");
        await router.OnSessionConnectedAsync(alice, Ct);
        var show = MakeShow(@"CORP\alice");
        await router.OnServerShowAsync(show, Ct);
        await router.OnSessionMessageAsync(alice, new ConfirmRequest { DeliveryId = show.DeliveryId, ButtonId = ButtonOk }, Ct);

        await router.SetLinkAsync(null, Down, Ct);

        var reply = Assert.Single(alice.Of<ConfirmReply>());
        Assert.False(reply.Accepted);
        Assert.Equal(AnnouncementRouter.LinkLostError, reply.Error);
        Assert.Equal(1, router.PendingCount);
    }

    [Fact]
    public async Task Confirm_for_someone_elses_or_unknown_delivery_is_not_forwarded_and_closes_the_window()
    {
        var (router, server) = await CreateConnected();
        var alice = new FakeEndpoint(1, @"CORP\alice");
        var bob = new FakeEndpoint(2, @"CORP\bob");
        await router.OnSessionConnectedAsync(alice, Ct);
        await router.OnSessionConnectedAsync(bob, Ct);
        var show = MakeShow(@"CORP\alice");
        await router.OnServerShowAsync(show, Ct);

        await router.OnSessionMessageAsync(bob, new ConfirmRequest { DeliveryId = show.DeliveryId, ButtonId = ButtonOk }, Ct);
        await router.OnSessionMessageAsync(alice, new ConfirmRequest { DeliveryId = Guid.NewGuid(), ButtonId = ButtonOk }, Ct);

        Assert.Empty(server.Of<Confirm>());
        Assert.Single(bob.Of<WithdrawAnnouncement>());
        Assert.Single(alice.Of<WithdrawAnnouncement>());
    }

    [Fact]
    public async Task Confirm_with_unknown_button_is_rejected()
    {
        var (router, server) = await CreateConnected();
        var alice = new FakeEndpoint(1, @"CORP\alice");
        await router.OnSessionConnectedAsync(alice, Ct);
        var show = MakeShow(@"CORP\alice");
        await router.OnServerShowAsync(show, Ct);

        await router.OnSessionMessageAsync(alice, new ConfirmRequest { DeliveryId = show.DeliveryId, ButtonId = Guid.NewGuid() }, Ct);

        Assert.Empty(server.Of<Confirm>());
        Assert.Equal(AnnouncementRouter.UnknownButtonError, Assert.Single(alice.Of<ConfirmReply>()).Error);
    }

    [Fact]
    public async Task Withdraw_removes_pending_and_closes_the_window()
    {
        var (router, _) = await CreateConnected();
        var alice = new FakeEndpoint(1, @"CORP\alice");
        await router.OnSessionConnectedAsync(alice, Ct);
        var show = MakeShow(@"CORP\alice");
        await router.OnServerShowAsync(show, Ct);

        await router.OnServerWithdrawAsync(new Withdraw { DeliveryId = show.DeliveryId }, Ct);

        Assert.Single(alice.Of<WithdrawAnnouncement>(), w => w.DeliveryId == show.DeliveryId);
        Assert.Equal(0, router.PendingCount);
    }

    [Fact]
    public async Task Repeated_show_is_deduplicated_in_the_store()
    {
        var (router, _) = await CreateConnected();
        var id = Guid.NewGuid();
        await router.OnServerShowAsync(MakeShow(@"CORP\alice", id), Ct);
        await router.OnServerShowAsync(MakeShow(@"CORP\alice", id), Ct);
        Assert.Equal(1, router.PendingCount);
    }

    [Fact]
    public async Task Logoff_of_the_last_session_drops_the_users_pending_items()
    {
        var (router, _) = await CreateConnected();
        var alice = new FakeEndpoint(1, @"CORP\alice");
        await router.OnSessionConnectedAsync(alice, Ct);
        await router.OnServerShowAsync(MakeShow(@"CORP\alice"), Ct);
        await router.OnServerShowAsync(MakeShow(@"CORP\bob"), Ct);

        Assert.Equal(0, router.OnUserLoggedOff(@"CORP\alice")); // still connected
        router.OnSessionDisconnected(alice);
        Assert.Equal(1, router.OnUserLoggedOff(@"corp\alice"));
        Assert.Equal(1, router.PendingCount);
    }

    [Fact]
    public async Task Show_without_user_is_ignored()
    {
        var (router, _) = await CreateConnected();
        await router.OnServerShowAsync(MakeShow(" "), Ct);
        Assert.Equal(0, router.PendingCount);
    }
}
