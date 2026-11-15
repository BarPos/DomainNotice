using DomainNotice.Server.Abstractions;
using DomainNotice.Server.AgentLink;
using DomainNotice.Server.Data.Entities;
using DomainNotice.Server.Tests.Infrastructure;
using DomainNotice.Shared.Protocol;
using Microsoft.EntityFrameworkCore;

namespace DomainNotice.Server.Tests.AgentLink;

public class ComputerAdminTests(PostgresFixture pg)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Revoke_disconnects_refuses_and_is_audited()
    {
        var db = await pg.CreateDatabaseAsync();
        var observer = new RecordingObserver();
        await using var server = await AgentLinkTestHelpers.StartAsync(db, observer);
        var cert = TestAgentClient.CreateCertificate();
        var (agent, id) = await server.ConnectEnrolledAsync("PC-1", [TestAgentClient.Session(1, @"CORP\alice")], certificate: cert);
        await using var _ = agent;
        var admin = server.Get<IComputerAdminService>();

        Assert.True((await admin.RevokeAsync(id, "admin", Ct)).Ok);

        Assert.Equal(RefusedReasons.Revoked, (await agent.WaitForRefusedAsync())?.Reason);
        await TestAgentServer.WaitUntilAsync(() => observer.Offline.Contains(id));
        await using var again = await server.ConnectAsync(cert);
        Assert.Equal(RefusedReasons.Revoked, (await again.WaitForRefusedAsync())?.Reason);
        // Enrolling again with the same key does not bring it back.
        await using var enrolAgain = await server.ConnectAsync(cert);
        await enrolAgain.SendAsync(new Enroll { ComputerName = "PC-1" }, ct: Ct);
        Assert.Equal(RefusedReasons.Revoked, (await enrolAgain.WaitForRefusedAsync())?.Reason);

        await using var ctx = db.CreateContext();
        var c = await ctx.Computers.SingleAsync(Ct);
        Assert.NotNull(c.RevokedAtUtc);
        Assert.Equal("admin", c.RevokedBy);
        Assert.False(c.Online);
        Assert.Single(await ctx.AuditLog.Where(a => a.Action == AuditActions.ComputerRevoked && a.TargetId == id.ToString()).ToListAsync(Ct));
        Assert.False((await admin.RevokeAsync(id, "admin", Ct)).Ok);
    }

    [Fact]
    public async Task Revoke_between_connect_and_hello_refuses_the_hello()
    {
        var db = await pg.CreateDatabaseAsync();
        await using var server = await AgentLinkTestHelpers.StartAsync(db);
        var cert = TestAgentClient.CreateCertificate();
        Guid id;
        await using (var first = await server.ConnectAsync(cert))
            id = (await first.EnrollAsync()).ComputerId!.Value;

        await using var agent = await server.ConnectAsync(cert); // accepted as a known key, Hello not sent yet
        await agent.PingAsync();
        Assert.True((await server.Get<IComputerAdminService>().RevokeAsync(id, "admin", Ct)).Ok);
        await agent.SendAsync(TestAgentClient.CreateHello(), ct: Ct);

        Assert.Equal(RefusedReasons.Revoked, (await agent.WaitForRefusedAsync())?.Reason);
        Assert.False(server.Get<IAgentHub>().IsOnline(id));
        Assert.False((await AgentLinkTestHelpers.GetComputerAsync(db, id)).Online);
    }

    [Fact]
    public async Task Delete_disconnects_and_frees_the_key()
    {
        var db = await pg.CreateDatabaseAsync();
        await using var server = await AgentLinkTestHelpers.StartAsync(db);
        var cert = TestAgentClient.CreateCertificate();
        var (agent, id) = await server.ConnectEnrolledAsync("PC-1", [TestAgentClient.Session(1, @"CORP\alice")], certificate: cert);
        await using var _ = agent;

        Assert.True((await server.Get<IComputerAdminService>().DeleteAsync(id, "admin", Ct)).Ok);

        Assert.True(await agent.WaitForCloseAsync());
        await using (var ctx = db.CreateContext())
        {
            Assert.Equal(0, await ctx.Computers.CountAsync(Ct));
            Assert.Equal(0, await ctx.UserLogonHistory.CountAsync(Ct));
            Assert.Contains(await ctx.AuditLog.Select(a => a.Action).ToListAsync(Ct), a => a == AuditActions.ComputerDeleted);
        }
        await using var again = await server.ConnectAsync(cert);
        var result = await again.EnrollAsync();
        Assert.True(result.Accepted);
        Assert.NotEqual(id, result.ComputerId);
    }

    [Fact]
    public async Task Name_fallback_links_when_exactly_one_ad_computer_and_no_holder()
    {
        var db = await pg.CreateDatabaseAsync();
        var observer = new RecordingObserver();
        var adGuid = await AgentLinkTestHelpers.AddAdComputerAsync(db, "PC-1");
        await AgentLinkTestHelpers.AddAdComputerAsync(db, "PC-TWIN");
        await AgentLinkTestHelpers.AddAdComputerAsync(db, "PC-TWIN");
        await using var server = await AgentLinkTestHelpers.StartAsync(db, observer);

        var (a, idA) = await server.ConnectEnrolledAsync("pc-1");
        var (b, idB) = await server.ConnectEnrolledAsync("PC-TWIN");
        var (c, idC) = await server.ConnectEnrolledAsync("PC-NOT-IN-AD");
        await using var _a = a;
        await using var _b = b;
        await using var _c = c;
        await TestAgentServer.WaitUntilAsync(() => observer.Online.Count == 3);

        var linked = await AgentLinkTestHelpers.GetComputerAsync(db, idA);
        Assert.Equal(AdLinkState.MatchedByName, linked.AdLinkState);
        Assert.Equal(adGuid, linked.DirectoryObjectGuid);
        Assert.Contains(idA, observer.IdentityChanged);
        Assert.Equal(AdLinkState.None, (await AgentLinkTestHelpers.GetComputerAsync(db, idB)).AdLinkState);
        Assert.Null((await AgentLinkTestHelpers.GetComputerAsync(db, idB)).DirectoryObjectGuid);
        Assert.Equal(AdLinkState.None, (await AgentLinkTestHelpers.GetComputerAsync(db, idC)).AdLinkState);

        // Rename to a name that is not in AD drops the name link.
        await a.SendStatusAsync("PC-RENAMED");
        await TestAgentServer.WaitUntilAsync(async () => (await AgentLinkTestHelpers.GetComputerAsync(db, idA)).AdLinkState == AdLinkState.None);
        Assert.Null((await AgentLinkTestHelpers.GetComputerAsync(db, idA)).DirectoryObjectGuid);
    }

    [Fact]
    public async Task Late_directory_sync_is_picked_up_by_the_periodic_job()
    {
        var db = await pg.CreateDatabaseAsync();
        await using var server = await AgentLinkTestHelpers.StartAsync(db);
        var (a, id) = await server.ConnectEnrolledAsync("PC-LATE");
        await using var _ = a;
        await a.PingAsync();
        Assert.Equal(AdLinkState.None, (await AgentLinkTestHelpers.GetComputerAsync(db, id)).AdLinkState);

        var guid = await AgentLinkTestHelpers.AddAdComputerAsync(db, "PC-LATE");
        Assert.Equal(1, await server.Get<AdLinkService>().EvaluatePendingAsync(Ct));

        var c = await AgentLinkTestHelpers.GetComputerAsync(db, id);
        Assert.Equal(AdLinkState.MatchedByName, c.AdLinkState);
        Assert.Equal(guid, c.DirectoryObjectGuid);
    }

    private async Task<(TestAgentServer Server, RecordingObserver Observer, Guid AdGuid, TestAgentClient Old, Guid OldId, TestAgentClient New, Guid NewId)>
        ReimagedAsync(TestDatabase db)
    {
        var observer = new RecordingObserver();
        var adGuid = await AgentLinkTestHelpers.AddAdComputerAsync(db, "PC-1");
        var server = await AgentLinkTestHelpers.StartAsync(db, observer);
        var (old, oldId) = await server.ConnectEnrolledAsync("PC-1");
        await TestAgentServer.WaitUntilAsync(() => observer.Online.Contains(oldId));
        var (@new, newId) = await server.ConnectEnrolledAsync("PC-1");
        await TestAgentServer.WaitUntilAsync(() => observer.Online.Contains(newId));
        return (server, observer, adGuid, old, oldId, @new, newId);
    }

    [Fact]
    public async Task Same_name_on_a_new_key_waits_for_approval_and_keeps_old_link()
    {
        var db = await pg.CreateDatabaseAsync();
        var (server, observer, adGuid, old, oldId, @new, newId) = await ReimagedAsync(db);
        await using var _s = server;
        await using var _o = old;
        await using var _n = @new;

        var waiting = await AgentLinkTestHelpers.GetComputerAsync(db, newId);
        Assert.Equal(AdLinkState.AwaitingApproval, waiting.AdLinkState);
        Assert.Equal(oldId, waiting.ConflictsWithComputerId);
        Assert.Null(waiting.DirectoryObjectGuid);
        var holder = await AgentLinkTestHelpers.GetComputerAsync(db, oldId);
        Assert.Equal(AdLinkState.MatchedByName, holder.AdLinkState);
        Assert.Equal(adGuid, holder.DirectoryObjectGuid);
        Assert.Contains(newId, observer.IdentityChanged);

        // Nothing is released automatically.
        await server.Get<AdLinkService>().EvaluatePendingAsync(Ct);
        Assert.Equal(AdLinkState.AwaitingApproval, (await AgentLinkTestHelpers.GetComputerAsync(db, newId)).AdLinkState);
    }

    [Fact]
    public async Task Approve_replace_moves_the_link_and_refuses_the_old_key()
    {
        var db = await pg.CreateDatabaseAsync();
        var (server, observer, adGuid, old, oldId, @new, newId) = await ReimagedAsync(db);
        await using var _s = server;
        await using var _o = old;
        await using var _n = @new;
        var admin = server.Get<IComputerAdminService>();

        Assert.True((await admin.ApproveReplaceAsync(newId, "admin", Ct)).Ok);

        Assert.Equal(RefusedReasons.Replaced, (await old.WaitForRefusedAsync())?.Reason);
        var replacement = await AgentLinkTestHelpers.GetComputerAsync(db, newId);
        Assert.Equal(AdLinkState.MatchedByName, replacement.AdLinkState);
        Assert.Equal(adGuid, replacement.DirectoryObjectGuid);
        Assert.Null(replacement.ConflictsWithComputerId);
        var replaced = await AgentLinkTestHelpers.GetComputerAsync(db, oldId);
        Assert.Equal(newId, replaced.ReplacedByComputerId);
        Assert.Null(replaced.DirectoryObjectGuid);
        Assert.Equal([newId, oldId], await admin.GetHistoryChainAsync(newId, Ct));
        Assert.Contains(oldId, observer.IdentityChanged);

        await using var ctx = db.CreateContext();
        Assert.Contains(await ctx.AuditLog.Select(a => a.Action).ToListAsync(Ct), a => a == AuditActions.ComputerApprovedReplace);
        Assert.False((await admin.ApproveReplaceAsync(newId, "admin", Ct)).Ok); // not awaiting any more
    }

    [Fact]
    public async Task Keep_separate_takes_it_off_the_list_for_good()
    {
        var db = await pg.CreateDatabaseAsync();
        var (server, _, adGuid, old, oldId, @new, newId) = await ReimagedAsync(db);
        await using var _s = server;
        await using var _o = old;
        await using var _n = @new;
        var admin = server.Get<IComputerAdminService>();

        Assert.True((await admin.KeepSeparateAsync(newId, "admin", Ct)).Ok);
        await server.Get<AdLinkService>().EvaluatePendingAsync(Ct);
        await @new.SendStatusAsync("PC-1");
        await @new.PingAsync();

        var kept = await AgentLinkTestHelpers.GetComputerAsync(db, newId);
        Assert.Equal(AdLinkState.KeptSeparate, kept.AdLinkState);
        Assert.Null(kept.DirectoryObjectGuid);
        Assert.Null(kept.ConflictsWithComputerId);
        Assert.Equal(adGuid, (await AgentLinkTestHelpers.GetComputerAsync(db, oldId)).DirectoryObjectGuid);
        Assert.False(old.IsClosed);
        await using var ctx = db.CreateContext();
        Assert.Contains(await ctx.AuditLog.Select(a => a.Action).ToListAsync(Ct), a => a == AuditActions.ComputerKeptSeparate);
    }

    [Fact]
    public async Task Revoking_the_holder_lets_the_waiting_computer_link()
    {
        var db = await pg.CreateDatabaseAsync();
        var (server, _, adGuid, old, oldId, @new, newId) = await ReimagedAsync(db);
        await using var _s = server;
        await using var _o = old;
        await using var _n = @new;

        Assert.True((await server.Get<IComputerAdminService>().RevokeAsync(oldId, "admin", Ct)).Ok);

        var linked = await AgentLinkTestHelpers.GetComputerAsync(db, newId);
        Assert.Equal(AdLinkState.MatchedByName, linked.AdLinkState);
        Assert.Equal(adGuid, linked.DirectoryObjectGuid);
    }
}
