using DomainNotice.Server.Abstractions;
using DomainNotice.Server.Data.Entities;
using DomainNotice.Server.Directory;
using DomainNotice.Server.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace DomainNotice.Server.Tests.Directory;

public class DirectorySyncTests(PostgresFixture pg)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Tree(FakeLdapSource Src, LdapDirectoryEntry Domain, LdapDirectoryEntry Staff, LdapDirectoryEntry Pcs,
        LdapDirectoryEntry Alice, LdapDirectoryEntry Bob, LdapDirectoryEntry Pc1, LdapDirectoryEntry Group);

    private static Tree Build()
    {
        var src = new FakeLdapSource();
        var domain = src.AddDomain();
        var staff = src.AddOu("Staff", FakeLdapSource.BaseDn);
        var pcs = src.AddOu("Computers", staff.DistinguishedName);
        var alice = src.AddUser("Alice", staff.DistinguishedName);
        var bob = src.AddUser("Bob", staff.DistinguishedName, disabled: true);
        var pc1 = src.AddComputer("PC-1", pcs.DistinguishedName);
        var outsider = "CN=Foreign,CN=ForeignSecurityPrincipals,DC=other,DC=local";
        var group = src.AddGroup("Sales", staff.DistinguishedName, alice, pc1);
        group = src.Update(group.ObjectGuid, g => FakeLdapSource.Copy(g, members: [.. g.Members, outsider]));
        return new Tree(src, domain, staff, pcs, alice, bob, pc1, group);
    }

    [Fact]
    public async Task Full_sync_inserts_objects_with_helper_columns()
    {
        var ctx = await DirectoryTestContext.CreateAsync(pg);
        var t = Build();
        var run = await ctx.Engine(t.Src).RunAsync(DirectorySyncKind.Full, "tester", Ct);

        Assert.True(run.Success, run.Error);
        Assert.Equal(DirectorySyncKind.Full, run.Kind);
        Assert.Equal("dc1.example.local", run.Host);
        Assert.Equal(t.Src.HighestUsn, run.HighestUsn);
        Assert.Equal(7, run.ObjectsChanged);
        Assert.Equal("tester", run.RequestedBy);
        Assert.NotNull(run.FinishedAtUtc);
        Assert.Equal(t.Src.HighestUsn.ToString(), await ctx.Settings.GetAsync(SettingKeys.DirectoryHighestUsnPrefix + "dc1.example.local", Ct));

        await using var db = ctx.Db.CreateContext();
        var all = await db.DirectoryObjects.ToDictionaryAsync(o => o.ObjectGuid, Ct);
        Assert.Equal(7, all.Count);
        Assert.Null(all[t.Domain.ObjectGuid].ParentGuid); // BaseDn is the root
        Assert.Equal(DirectoryObjectKind.Domain, all[t.Domain.ObjectGuid].Kind);
        Assert.Equal(t.Domain.ObjectGuid, all[t.Staff.ObjectGuid].ParentGuid);
        Assert.Equal(t.Staff.ObjectGuid, all[t.Pcs.ObjectGuid].ParentGuid);
        Assert.Equal("Computers", all[t.Pcs.ObjectGuid].Name);

        var pc = all[t.Pc1.ObjectGuid];
        Assert.Equal(DirectoryObjectKind.Computer, pc.Kind);
        Assert.Equal("PC-1$", pc.SamAccountName);
        Assert.Equal("pc-1$", pc.SamAccountNameLower);
        Assert.Equal("cn=pc-1,ou=computers,ou=staff,dc=example,dc=local", pc.DistinguishedNameLower);
        Assert.Equal("pc-1.example.local", pc.DnsHostName);
        Assert.Equal(t.Pcs.ObjectGuid, pc.ParentGuid);

        var alice = all[t.Alice.ObjectGuid];
        Assert.Equal(DirectoryObjectKind.User, alice.Kind);
        Assert.True(alice.Enabled);
        Assert.Equal("alice", alice.SamAccountNameLower);
        Assert.Equal("ALICE", alice.DisplayName);
        Assert.Equal("Alice@example.local", alice.UserPrincipalName);
        Assert.False(all[t.Bob.ObjectGuid].Enabled); // userAccountControl bit 2

        // Direct members: Alice and PC-1; the foreign member outside BaseDn is skipped.
        var members = await db.DirectoryMemberships.Where(m => m.GroupGuid == t.Group.ObjectGuid).Select(m => m.MemberGuid).ToListAsync(Ct);
        Assert.Equal(new[] { t.Alice.ObjectGuid, t.Pc1.ObjectGuid }.Order(), members.Order());
        Assert.Equal(2, await db.DirectoryTransitiveMemberships.CountAsync(Ct));
    }

    [Fact]
    public async Task Incremental_reads_only_changes_and_first_incremental_runs_full()
    {
        var ctx = await DirectoryTestContext.CreateAsync(pg);
        var t = Build();
        var engine = ctx.Engine(t.Src);

        var first = await engine.RunAsync(DirectorySyncKind.Incremental, null, Ct);
        Assert.True(first.Success, first.Error);
        Assert.Equal(DirectorySyncKind.Full, first.Kind); // never synced → full
        Assert.Equal([null], t.Src.Searches);

        var highest = t.Src.HighestUsn;
        t.Src.Update(t.Alice.ObjectGuid, e => FakeLdapSource.Copy(e, displayName: "Alice Smith", uac: 0x202));
        var second = await engine.RunAsync(DirectorySyncKind.Incremental, null, Ct);
        Assert.True(second.Success, second.Error);
        Assert.Equal(DirectorySyncKind.Incremental, second.Kind);
        Assert.Equal(highest, t.Src.Searches[^1]);
        Assert.Equal(1, second.ObjectsChanged);

        await using var db = ctx.Db.CreateContext();
        var alice = await db.DirectoryObjects.SingleAsync(o => o.ObjectGuid == t.Alice.ObjectGuid, Ct);
        Assert.Equal("Alice Smith", alice.DisplayName);
        Assert.False(alice.Enabled);
        Assert.Equal(t.Src.HighestUsn, alice.UsnChanged);
        Assert.Equal(7, await db.DirectoryObjects.CountAsync(o => !o.Deleted, Ct));
        // The incremental did not touch the group, so its memberships remain.
        Assert.Equal(2, await db.DirectoryMemberships.CountAsync(Ct));

        // Nothing changed → nothing read.
        var third = await engine.RunAsync(DirectorySyncKind.Incremental, null, Ct);
        Assert.True(third.Success);
        Assert.Equal(0, third.ObjectsChanged);
    }

    [Fact]
    public async Task Rename_keeps_guid_and_ou_rename_rewrites_descendants()
    {
        var ctx = await DirectoryTestContext.CreateAsync(pg);
        var t = Build();
        var engine = ctx.Engine(t.Src);
        Assert.True((await engine.RunAsync(DirectorySyncKind.Full, null, Ct)).Success);

        // OU rename: only the OU itself gets a new uSNChanged; its children keep theirs, as in AD.
        const string newStaffDn = "OU=People,DC=example,DC=local";
        // User rename (cn change) in the same run keeps the row.
        t.Src.Update(t.Alice.ObjectGuid, e => FakeLdapSource.Copy(e, dn: "CN=Alice Smith," + newStaffDn, cn: "Alice Smith"));
        t.Src.Update(t.Staff.ObjectGuid, e => FakeLdapSource.Copy(e, dn: newStaffDn, cn: "People"));
        var run = await engine.RunAsync(DirectorySyncKind.Incremental, null, Ct);
        Assert.True(run.Success, run.Error);
        Assert.Equal(DirectorySyncKind.Incremental, run.Kind);

        await using var db = ctx.Db.CreateContext();
        var all = await db.DirectoryObjects.ToDictionaryAsync(o => o.ObjectGuid, Ct);
        Assert.Equal(7, all.Count);
        Assert.Equal("Alice Smith", all[t.Alice.ObjectGuid].Name);
        Assert.Equal("CN=Alice Smith,OU=People,DC=example,DC=local", all[t.Alice.ObjectGuid].DistinguishedName);
        Assert.Equal("People", all[t.Staff.ObjectGuid].Name);
        Assert.Equal("CN=Bob,OU=People,DC=example,DC=local", all[t.Bob.ObjectGuid].DistinguishedName);
        Assert.Equal("cn=pc-1,ou=computers,ou=people,dc=example,dc=local", all[t.Pc1.ObjectGuid].DistinguishedNameLower);
        Assert.Equal("OU=Computers,OU=People,DC=example,DC=local", all[t.Pcs.ObjectGuid].DistinguishedName);
        Assert.Equal(t.Staff.ObjectGuid, all[t.Pcs.ObjectGuid].ParentGuid);
        Assert.All(all.Values, o => Assert.False(o.Deleted));
    }

    [Fact]
    public async Task Full_sync_marks_missing_objects_deleted_and_drops_their_memberships()
    {
        var ctx = await DirectoryTestContext.CreateAsync(pg);
        var t = Build();
        var engine = ctx.Engine(t.Src);
        Assert.True((await engine.RunAsync(DirectorySyncKind.Full, null, Ct)).Success);

        t.Src.Entries.RemoveAll(e => e.ObjectGuid == t.Alice.ObjectGuid);
        // An incremental cannot see deletions.
        Assert.True((await engine.RunAsync(DirectorySyncKind.Incremental, null, Ct)).Success);
        await using (var db = ctx.Db.CreateContext())
            Assert.False((await db.DirectoryObjects.SingleAsync(o => o.ObjectGuid == t.Alice.ObjectGuid, Ct)).Deleted);

        var run = await engine.RunAsync(DirectorySyncKind.Full, null, Ct);
        Assert.True(run.Success, run.Error);
        await using (var db = ctx.Db.CreateContext())
        {
            var alice = await db.DirectoryObjects.SingleAsync(o => o.ObjectGuid == t.Alice.ObjectGuid, Ct);
            Assert.True(alice.Deleted);
            Assert.Equal("Alice", alice.Name); // kept so stored targets still show a name
            Assert.False(await db.DirectoryMemberships.AnyAsync(m => m.MemberGuid == t.Alice.ObjectGuid, Ct));
            Assert.False(await db.DirectoryTransitiveMemberships.AnyAsync(m => m.MemberGuid == t.Alice.ObjectGuid, Ct));
            Assert.Equal(6, await db.DirectoryObjects.CountAsync(o => !o.Deleted, Ct));
        }

        // Coming back un-deletes.
        t.Src.Entries.Add(FakeLdapSource.Copy(t.Alice, usn: ++t.Src.HighestUsn));
        Assert.True((await engine.RunAsync(DirectorySyncKind.Incremental, null, Ct)).Success);
        await using (var db = ctx.Db.CreateContext())
            Assert.False((await db.DirectoryObjects.SingleAsync(o => o.ObjectGuid == t.Alice.ObjectGuid, Ct)).Deleted);
    }

    [Fact]
    public async Task Failed_search_marks_nothing_deleted_and_records_the_error()
    {
        var ctx = await DirectoryTestContext.CreateAsync(pg);
        var t = Build();
        var engine = ctx.Engine(t.Src);
        Assert.True((await engine.RunAsync(DirectorySyncKind.Full, null, Ct)).Success);

        t.Src.FailAfter = 3;
        var run = await engine.RunAsync(DirectorySyncKind.Full, "admin", Ct);
        Assert.False(run.Success);
        Assert.Equal("Simulated page failure.", run.Error);

        await using var db = ctx.Db.CreateContext();
        Assert.Equal(7, await db.DirectoryObjects.CountAsync(o => !o.Deleted, Ct));
        var stored = await db.DirectorySyncRuns.OrderBy(r => r.Id).ToListAsync(Ct);
        Assert.Equal(2, stored.Count);
        Assert.False(stored[1].Success);
        Assert.NotNull(stored[1].FinishedAtUtc);
    }

    [Fact]
    public async Task Nested_and_cyclic_groups_fill_the_transitive_table()
    {
        var ctx = await DirectoryTestContext.CreateAsync(pg);
        var src = new FakeLdapSource();
        src.AddDomain();
        var u1 = src.AddUser("u1", FakeLdapSource.BaseDn);
        var c1 = src.AddComputer("c1", FakeLdapSource.BaseDn);
        var g3 = src.AddGroup("g3", FakeLdapSource.BaseDn, c1);
        var g2 = src.AddGroup("g2", FakeLdapSource.BaseDn, u1, g3);
        g3 = src.Update(g3.ObjectGuid, g => FakeLdapSource.Copy(g, members: [.. g.Members, g2.DistinguishedName])); // cycle g2 ↔ g3
        var g1 = src.AddGroup("g1", FakeLdapSource.BaseDn, g2);
        var self = src.AddGroup("self", FakeLdapSource.BaseDn);
        self = src.Update(self.ObjectGuid, g => FakeLdapSource.Copy(g, members: [g.DistinguishedName, u1.DistinguishedName]));

        var engine = ctx.Engine(src);
        var run = await engine.RunAsync(DirectorySyncKind.Full, null, Ct);
        Assert.True(run.Success, run.Error);

        var name = new Dictionary<Guid, string>
        {
            [u1.ObjectGuid] = "u1", [c1.ObjectGuid] = "c1", [g1.ObjectGuid] = "g1", [g2.ObjectGuid] = "g2",
            [g3.ObjectGuid] = "g3", [self.ObjectGuid] = "self",
        };
        await using (var db = ctx.Db.CreateContext())
        {
            var pairs = (await db.DirectoryTransitiveMemberships.ToListAsync(Ct))
                .Select(m => $"{name[m.GroupGuid]}>{name[m.MemberGuid]}").Order().ToList();
            Assert.Equal(new[]
            {
                "g1>c1", "g1>g2", "g1>g3", "g1>u1",
                "g2>c1", "g2>g3", "g2>u1",
                "g3>c1", "g3>g2", "g3>u1",
                "self>u1",
            }, pairs);
        }

        // Incremental: remove c1 from g3 → c1 disappears from every group's closure.
        src.Update(g3.ObjectGuid, g => FakeLdapSource.Copy(g, members: [g2.DistinguishedName]));
        Assert.True((await engine.RunAsync(DirectorySyncKind.Incremental, null, Ct)).Success);
        await using (var db = ctx.Db.CreateContext())
        {
            Assert.False(await db.DirectoryTransitiveMemberships.AnyAsync(m => m.MemberGuid == c1.ObjectGuid, Ct));
            Assert.Equal(8, await db.DirectoryTransitiveMemberships.CountAsync(Ct));
        }
    }

    [Fact]
    public async Task Dc_switch_forces_full_sync()
    {
        var ctx = await DirectoryTestContext.CreateAsync(pg);
        var t = Build();
        var engine = ctx.Engine(t.Src);
        Assert.True((await engine.RunAsync(DirectorySyncKind.Full, null, Ct)).Success);
        Assert.Equal(DirectorySyncKind.Incremental, (await engine.RunAsync(DirectorySyncKind.Incremental, null, Ct)).Kind);

        t.Src.DnsHostName = "dc2.example.local";
        t.Src.ConfiguredHost = "dc2.example.local";
        var run = await engine.RunAsync(DirectorySyncKind.Incremental, null, Ct);
        Assert.True(run.Success, run.Error);
        Assert.Equal(DirectorySyncKind.Full, run.Kind);
        Assert.Equal("dc2.example.local", run.Host);
        Assert.NotNull(await ctx.Settings.GetAsync(SettingKeys.DirectoryHighestUsnPrefix + "dc2.example.local", Ct));

        // A stored USN above the DC's current one (restored DC) also forces full.
        await ctx.Settings.SetAsync(SettingKeys.DirectoryHighestUsnPrefix + "dc2.example.local", "999999999", "test", Ct);
        Assert.Equal(DirectorySyncKind.Full, (await engine.RunAsync(DirectorySyncKind.Incremental, null, Ct)).Kind);
    }

    [Fact]
    public async Task Connection_failure_is_recorded()
    {
        var ctx = await DirectoryTestContext.CreateAsync(pg);
        var src = new FakeLdapSource { OpenError = new DirectoryConnectException("No domain controller could be reached: x") };
        var run = await ctx.Engine(src).RunAsync(DirectorySyncKind.Full, null, Ct);
        Assert.False(run.Success);
        Assert.StartsWith("No domain controller", run.Error);
    }
}
