using DomainNotice.Server.Abstractions;
using DomainNotice.Server.Announcements;
using DomainNotice.Server.Data.Entities;
using DomainNotice.Server.Tests.Infrastructure;
using DomainNotice.Shared;
using DomainNotice.Shared.Protocol;
using Microsoft.EntityFrameworkCore;

namespace DomainNotice.Server.Tests.Announcements;

public class DeliveryFlowTests(PostgresFixture pg)
{
    private const string Alice = @"CORP\alice";

    [Fact]
    public async Task Online_show_shown_confirm_then_next_is_pushed()
    {
        await using var h = await AnnouncementHarness.CreateAsync(pg);
        var pc = await h.AddComputerAsync("PC-ONE");
        var first = await h.SaveAsync(AnnouncementHarness.Model("First", start: h.Now.AddMinutes(-10)));
        var second = await h.SaveAsync(AnnouncementHarness.Model("Second", start: h.Now.AddMinutes(-5)));

        var conn = await h.OnlineAsync(pc, Alice);
        var show = Assert.Single(conn.SentOf<Show>()); // one open delivery per (computer, user)
        Assert.Equal(first, show.Announcement.Id);
        Assert.Equal(Alice, show.SessionUser);
        var delivery = Assert.Single(await h.DeliveriesAsync());
        Assert.Equal(show.DeliveryId, delivery.Id);
        Assert.Equal("corp\\alice", delivery.UserKey);
        Assert.Null(delivery.ShownAtUtc);

        await h.ShownAsync(conn, show.DeliveryId);
        var shownAt = (await h.DeliveriesAsync()).Single().ShownAtUtc;
        Assert.Equal(h.Now, shownAt);
        h.Time.Advance(TimeSpan.FromSeconds(5));
        await h.ShownAsync(conn, show.DeliveryId); // set once
        Assert.Equal(shownAt, (await h.DeliveriesAsync()).Single().ShownAtUtc);

        h.Time.Advance(TimeSpan.FromSeconds(6));
        var (result, _) = await h.ConfirmAsync(conn, show.DeliveryId, show.Announcement.Buttons[0].Id);
        Assert.True(result.Accepted, result.Error);
        Assert.Equal(show.DeliveryId, result.DeliveryId);

        await using (var ctx = h.Db.CreateContext())
        {
            var c = await ctx.Confirmations.SingleAsync(h.Ct);
            Assert.Equal(pc, c.ComputerId);
            Assert.Equal("PC-ONE", c.ComputerName);
            Assert.Equal(Alice, c.UserName);
            Assert.Equal("OK", c.ButtonLabel);
            Assert.Equal(show.DeliveryId, c.DeliveryId);
            Assert.NotNull((await ctx.Deliveries.SingleAsync(d => d.Id == show.DeliveryId, h.Ct)).ConfirmedAtUtc);
        }

        var shows = conn.SentOf<Show>().ToList();
        Assert.Equal(2, shows.Count);
        Assert.Equal(second, shows[1].Announcement.Id);
        Assert.NotEqual(show.DeliveryId, shows[1].DeliveryId);

        // Nothing more after both are done.
        await h.ShownAsync(conn, shows[1].DeliveryId);
        h.Time.Advance(TimeSpan.FromSeconds(10));
        Assert.True((await h.ConfirmAsync(conn, shows[1].DeliveryId, shows[1].Announcement.Buttons[0].Id)).Result.Accepted);
        await h.Coordinator.EvaluateAllOnlineAsync(h.Ct);
        Assert.Equal(2, conn.SentOf<Show>().Count());
    }

    [Fact]
    public async Task Reconnect_resends_same_delivery_id()
    {
        await using var h = await AnnouncementHarness.CreateAsync(pg);
        var pc = await h.AddComputerAsync("PC1");
        await h.SaveAsync(AnnouncementHarness.Model());
        var conn = await h.OnlineAsync(pc, Alice);
        var first = Assert.Single(conn.SentOf<Show>());

        // Scheduler ticks do not re-send an open delivery.
        await h.Coordinator.EvaluateAllOnlineAsync(h.Ct);
        Assert.Single(conn.SentOf<Show>());

        h.Hub.Disconnect(pc);
        var again = await h.OnlineAsync(pc, Alice);
        Assert.Equal(first.DeliveryId, Assert.Single(again.SentOf<Show>()).DeliveryId);
        Assert.Single(await h.DeliveriesAsync());
    }

    [Fact]
    public async Task Logon_resends_open_delivery_and_new_user_gets_own()
    {
        await using var h = await AnnouncementHarness.CreateAsync(pg);
        var pc = await h.AddComputerAsync("PC1");
        await h.SaveAsync(AnnouncementHarness.Model());
        var conn = await h.OnlineAsync(pc, Alice);
        var first = Assert.Single(conn.SentOf<Show>());

        // Alice logs off and on again; Bob logs on.
        conn.SessionList.Clear();
        conn.WithSession(Alice, id: 5).WithSession(@"CORP\bob", id: 6);
        var current = conn.Sessions;
        await h.Observer.OnSessionsChangedAsync(conn, new SessionsChange(current, current, []), h.Ct);
        var shows = conn.SentOf<Show>().ToList();
        Assert.Equal(3, shows.Count);
        Assert.Equal(2, shows.Count(s => s.DeliveryId == first.DeliveryId));
        Assert.Contains(shows, s => s.SessionUser == @"CORP\bob" && s.DeliveryId != first.DeliveryId);
    }

    [Fact]
    public async Task Disconnected_sessions_are_skipped_locked_are_served()
    {
        await using var h = await AnnouncementHarness.CreateAsync(pg);
        var pc = await h.AddComputerAsync("PC1");
        await h.SaveAsync(AnnouncementHarness.Model());
        var conn = h.Hub.Connect(pc).WithSession(Alice, SessionState.Disconnected).WithSession(@"CORP\bob", SessionState.Locked);
        await h.Observer.OnAgentOnlineAsync(conn, h.Ct);
        Assert.Equal(@"CORP\bob", Assert.Single(conn.SentOf<Show>()).SessionUser);
    }

    [Fact]
    public async Task Display_limit_counts_shown_deliveries_across_computers()
    {
        await using var h = await AnnouncementHarness.CreateAsync(pg);
        var pc1 = await h.AddComputerAsync("PC1");
        var pc2 = await h.AddComputerAsync("PC2");
        var m = AnnouncementHarness.Model();
        m.MaxDisplays = 2;
        m.RepeatIntervalMinutes = 60;
        await h.SaveAsync(m);

        var c1 = await h.OnlineAsync(pc1, Alice);
        var s1 = Assert.Single(c1.SentOf<Show>());
        await h.ShownAsync(c1, s1.DeliveryId);

        // Unconfirmed shown window on PC1: PC2 gets nothing for this announcement.
        var c2 = await h.OnlineAsync(pc2, Alice);
        Assert.Empty(c2.SentOf<Show>());

        h.Time.Advance(TimeSpan.FromSeconds(20));
        Assert.True((await h.ConfirmAsync(c1, s1.DeliveryId, s1.Announcement.Buttons[0].Id)).Result.Accepted);

        // Repeat interval runs from the last shown time, for the user across computers.
        await h.Coordinator.EvaluateAllOnlineAsync(h.Ct);
        Assert.Empty(c2.SentOf<Show>());
        h.Time.Advance(TimeSpan.FromMinutes(60));
        await h.Coordinator.EvaluateAllOnlineAsync(h.Ct);
        // Nothing shown is open now, so both computers where Alice is logged on get the second display.
        var onPc1 = c1.SentOf<Show>().Last();
        var onPc2 = Assert.Single(c2.SentOf<Show>());
        Assert.NotEqual(s1.DeliveryId, onPc1.DeliveryId);
        await h.ShownAsync(c2, onPc2.DeliveryId);
        h.Time.Advance(TimeSpan.FromSeconds(20));
        Assert.True((await h.ConfirmAsync(c2, onPc2.DeliveryId, onPc2.Announcement.Buttons[0].Id)).Result.Accepted);
        // Confirming on PC2 withdraws the window still open on PC1.
        Assert.Equal(onPc1.DeliveryId, Assert.Single(c1.SentOf<Withdraw>()).DeliveryId);

        // Two shown = limit reached, on every computer.
        h.Time.Advance(TimeSpan.FromHours(12));
        await h.Coordinator.EvaluateAllOnlineAsync(h.Ct);
        Assert.Equal(2, c1.SentOf<Show>().Count());
        Assert.Single(c2.SentOf<Show>());
    }

    [Fact]
    public async Task Unshown_withdrawn_delivery_does_not_count()
    {
        await using var h = await AnnouncementHarness.CreateAsync(pg);
        var pc1 = await h.AddComputerAsync("PC1");
        var pc2 = await h.AddComputerAsync("PC2");
        var id = await h.SaveAsync(AnnouncementHarness.Model());
        await using (var ctx = h.Db.CreateContext())
        {
            ctx.Deliveries.Add(new Delivery
            {
                Id = Guid.NewGuid(), AnnouncementId = id, ComputerId = pc2, UserName = Alice, UserKey = UserKeys.Normalize(Alice),
                SentAtUtc = h.Now.AddHours(-1), WithdrawnAtUtc = h.Now.AddMinutes(-30),
            });
            await ctx.SaveChangesAsync(h.Ct);
        }
        var c1 = await h.OnlineAsync(pc1, Alice);
        Assert.Single(c1.SentOf<Show>());
    }

    [Fact]
    public async Task Start_time_fires_on_scheduler_tick_and_end_withdraws()
    {
        await using var h = await AnnouncementHarness.CreateAsync(pg);
        var pc = await h.AddComputerAsync("PC1");
        var id = await h.SaveAsync(AnnouncementHarness.Model(start: h.Now.AddMinutes(5), end: h.Now.AddMinutes(30)));
        var conn = await h.OnlineAsync(pc, Alice);
        Assert.Empty(conn.SentOf<Show>());

        h.Time.Advance(TimeSpan.FromMinutes(5));
        await h.Coordinator.EvaluateAllOnlineAsync(h.Ct);
        var show = Assert.Single(conn.SentOf<Show>());

        h.Time.Advance(TimeSpan.FromMinutes(26));
        await h.Coordinator.EvaluateAllOnlineAsync(h.Ct);
        Assert.Equal(show.DeliveryId, Assert.Single(conn.SentOf<Withdraw>()).DeliveryId);
        Assert.NotNull(Assert.Single(await h.DeliveriesAsync()).WithdrawnAtUtc);
    }

    [Fact]
    public async Task Becoming_a_draft_withdraws_immediately_and_offline_rows_are_marked()
    {
        await using var h = await AnnouncementHarness.CreateAsync(pg);
        var pc = await h.AddComputerAsync("PC1");
        var offlinePc = await h.AddComputerAsync("PC2");
        var id = await h.SaveAsync(AnnouncementHarness.Model());
        var conn = await h.OnlineAsync(pc, Alice);
        var off = await h.OnlineAsync(offlinePc, @"CORP\bob");
        h.Hub.Disconnect(offlinePc);
        var show = Assert.Single(conn.SentOf<Show>());

        var edit = (await h.Service.GetForEditAsync(id, h.Ct))!;
        edit.IsDraft = true;
        await h.SaveAsync(edit);
        Assert.Equal(show.DeliveryId, Assert.Single(conn.SentOf<Withdraw>()).DeliveryId);
        Assert.Empty(off.SentOf<Withdraw>());
        Assert.All(await h.DeliveriesAsync(), d => Assert.NotNull(d.WithdrawnAtUtc));
    }

    [Fact]
    public async Task Awaiting_approval_policy_is_read_from_settings()
    {
        await using var h = await AnnouncementHarness.CreateAsync(pg);
        var pc = await h.AddComputerAsync("PC1", AdLinkState.AwaitingApproval);
        await h.SaveAsync(AnnouncementHarness.Model());
        await h.Settings.SetAsync(SettingKeys.AwaitingApprovalReceives, nameof(AwaitingApprovalPolicy.Nothing), "t", h.Ct);
        var conn = await h.OnlineAsync(pc, Alice);
        Assert.Empty(conn.SentOf<Show>());

        await h.Settings.SetAsync(SettingKeys.AwaitingApprovalReceives, null, "t", h.Ct); // default EveryoneAndUser
        await h.Observer.OnComputerIdentityChangedAsync(pc, h.Ct);
        Assert.Single(conn.SentOf<Show>());
    }

    [Fact]
    public async Task Directory_targets_resolve_through_synced_tables()
    {
        await using var h = await AnnouncementHarness.CreateAsync(pg, directory: true);
        var ou = await h.AddDirectoryObjectAsync(DirectoryObjectKind.OrganizationalUnit, "OU=Sales,DC=corp,DC=example");
        var alice = await h.AddDirectoryObjectAsync(DirectoryObjectKind.User, "CN=Alice,OU=Staff,OU=Sales,DC=corp,DC=example", "alice", "alice@corp.example");
        var bob = await h.AddDirectoryObjectAsync(DirectoryObjectKind.User, "CN=Bob,OU=IT,DC=corp,DC=example", "bob");
        var carol = await h.AddDirectoryObjectAsync(DirectoryObjectKind.User, "CN=Carol,OU=IT,DC=corp,DC=example", "carol");
        var outer = await h.AddDirectoryObjectAsync(DirectoryObjectKind.Group, "CN=All,DC=corp,DC=example", "all");
        // bob is in a group nested in "All"; the sync writes the transitive pair (All, bob).
        await h.AddTransitiveMembershipAsync(outer, bob);
        var pcAd = await h.AddDirectoryObjectAsync(DirectoryObjectKind.Computer, "CN=PC1,OU=Workstations,DC=corp,DC=example", "pc1$");
        var pcGroup = await h.AddDirectoryObjectAsync(DirectoryObjectKind.Group, "CN=Laptops,DC=corp,DC=example", "laptops");
        await h.AddTransitiveMembershipAsync(pcGroup, pcAd);
        var linkedPc = await h.AddComputerAsync("PC1", AdLinkState.Proven, pcAd);
        var unlinkedPc = await h.AddComputerAsync("PC2", AdLinkState.None);

        var ouId = await h.SaveAsync(AnnouncementHarness.Model("OU", start: h.Now.AddMinutes(-30), targets: new TargetEdit { Kind = TargetKind.OrganizationalUnit, DirectoryObjectGuid = ou }));
        var groupId = await h.SaveAsync(AnnouncementHarness.Model("Group", start: h.Now.AddMinutes(-20), targets: new TargetEdit { Kind = TargetKind.UserGroup, DirectoryObjectGuid = outer }));
        var pcGroupId = await h.SaveAsync(AnnouncementHarness.Model("PcGroup", start: h.Now.AddMinutes(-10), targets: new TargetEdit { Kind = TargetKind.ComputerGroup, DirectoryObjectGuid = pcGroup }));

        // In order OU, Group, PcGroup. Everyone on the linked PC matches PcGroup; Alice (by UPN) is in the OU
        // subtree, Bob in the nested group, Carol only in neither.
        var c1 = await h.OnlineAsync(linkedPc, "alice@corp.example", @"CORP\bob", @"CORP\carol");
        var shows = c1.SentOf<Show>().ToList();
        Assert.Equal(ouId, shows.Single(s => s.SessionUser == "alice@corp.example").Announcement.Id);
        Assert.Equal(groupId, shows.Single(s => s.SessionUser == @"CORP\bob").Announcement.Id);
        Assert.Equal(pcGroupId, shows.Single(s => s.SessionUser == @"CORP\carol").Announcement.Id);

        // Unlinked computer: none of these targets apply.
        var c2 = await h.OnlineAsync(unlinkedPc, @"CORP\bob", @"CORP\carol");
        Assert.Empty(c2.SentOf<Show>());
        _ = alice; _ = carol;
    }
    [Fact]
    public async Task Registration_shares_one_instance_per_area_service()
    {
        await using var h = await AnnouncementHarness.CreateAsync(pg);
        Assert.Same(h.Coordinator, h.Observer);
        Assert.Contains(Shown.Type, h.Handler.MessageTypes);
        Assert.Contains(Confirm.Type, h.Handler.MessageTypes);
        Assert.Same(h.Export, h.Results);
    }

    [Fact]
    public async Task Concurrent_evaluations_create_one_delivery()
    {
        await using var h = await AnnouncementHarness.CreateAsync(pg);
        var pc = await h.AddComputerAsync("PC1");
        await h.SaveAsync(AnnouncementHarness.Model());
        var conn = h.Hub.Connect(pc).WithSession(Alice);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => h.Coordinator.EvaluateComputerAsync(pc, conn.Sessions, false, null, h.Ct)));
        Assert.Single(await h.DeliveriesAsync());
        Assert.Single(conn.SentOf<Show>());
    }

    [Fact]
    public async Task Saving_wakes_the_scheduler()
    {
        await using var h = await AnnouncementHarness.CreateAsync(pg);
        var pc = await h.AddComputerAsync("PC1");
        var conn = h.Hub.Connect(pc).WithSession(Alice);
        var scheduler = h.HostedServices.OfType<DeliveryScheduler>().Single();
        await scheduler.StartAsync(h.Ct);
        try
        {
            await h.SaveAsync(AnnouncementHarness.Model());
            for (var i = 0; i < 100 && !conn.SentOf<Show>().Any(); i++) await Task.Delay(50, h.Ct);
            Assert.Single(conn.SentOf<Show>());
        }
        finally
        {
            await scheduler.StopAsync(h.Ct);
        }
    }
}
