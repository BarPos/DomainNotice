using DomainNotice.Console.Grid;
using DomainNotice.Console.Lists;
using DomainNotice.Server.Data;
using DomainNotice.Server.Data.Entities;
using DomainNotice.Server.Tests.Infrastructure;
using DomainNotice.Shared;
using Microsoft.EntityFrameworkCore;

namespace DomainNotice.Server.Tests.Console;

/// <summary>Grid filter → EF Core translation, executed against PostgreSQL 16.</summary>
public class GridTranslationTests(PostgresFixture pg)
{
    private static readonly DateTime Now = new(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Computer Pc(string name, int lastSeenDaysAgo, bool online = false, AdLinkState link = AdLinkState.None,
        string? remoteIp = null, string[]? localIps = null) => new()
    {
        Id = Guid.NewGuid(), Name = name, AgentKeyFingerprint = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
        EnrolledAtUtc = Now.AddDays(-100), LastSeenAtUtc = Now.AddDays(-lastSeenDaysAgo), Online = online, AdLinkState = link,
        AgentVersion = "1.0.0", OsVersion = "Windows 11", RemoteIp = remoteIp, LocalIps = [.. localIps ?? []],
    };

    private async Task<TestDatabase> SeedComputersAsync()
    {
        var db = await pg.CreateDatabaseAsync();
        await using var ctx = db.CreateContext();
        var a = Pc("PC-12", 1, online: true, link: AdLinkState.Proven, remoteIp: "203.0.113.5", localIps: ["10.0.0.12", "fe80::12"]);
        var b = Pc("pc-120", 10, link: AdLinkState.MatchedByName, remoteIp: "203.0.113.6", localIps: ["10.0.0.120"]);
        var c = Pc("LAPTOP-7", 30, link: AdLinkState.AwaitingApproval, remoteIp: "198.51.100.7", localIps: ["192.168.1.7"]);
        var d = Pc("100%_done", 2);
        ctx.Computers.AddRange(a, b, c, d);
        ctx.ComputerSessions.Add(new ComputerSession
        {
            ComputerId = a.Id, SessionId = 1, UserName = @"CORP\Alice", UserKey = @"corp\alice", LogonAtUtc = Now, LastSeenUtc = Now,
        });
        await ctx.SaveChangesAsync(Ct);
        return db;
    }

    private static async Task<List<string>> NamesAsync(AppDbContext ctx, GridQuery q, Dictionary<string, string?>? scope = null)
    {
        var page = await ConsoleLists.Computers.QueryAsync(new GridContext(ctx, scope ?? [], Now), q, Ct);
        return page.Rows.Select(r => r.Name).ToList();
    }

    [Fact]
    public async Task Text_contains_is_case_insensitive_and_treats_wildcards_literally()
    {
        var db = await SeedComputersAsync();
        await using var ctx = db.CreateContext();

        var q = new GridQuery { Filters = [new("name", FilterOp.Contains, "pc-12")] };
        Assert.Equal(["PC-12", "pc-120"], await NamesAsync(ctx, q));

        Assert.Equal(["100%_done"], await NamesAsync(ctx, new GridQuery { Filters = [new("name", FilterOp.Contains, "%_")] }));
        Assert.Empty(await NamesAsync(ctx, new GridQuery { Filters = [new("name", FilterOp.Contains, "_x%")] }));

        Assert.Equal(["PC-12"], await NamesAsync(ctx, new GridQuery { Filters = [new("name", FilterOp.Equals, "pc-12")] }));
    }

    [Fact]
    public async Task Ip_and_user_filters_look_into_collections()
    {
        var db = await SeedComputersAsync();
        await using var ctx = db.CreateContext();
        Assert.Equal(["PC-12"], await NamesAsync(ctx, new GridQuery { Filters = [new("ip", FilterOp.Contains, "FE80::12")] }));
        Assert.Equal(["LAPTOP-7"], await NamesAsync(ctx, new GridQuery { Filters = [new("ip", FilterOp.Contains, "198.51")] }));
        Assert.Equal(["PC-12"], await NamesAsync(ctx, new GridQuery { Filters = [new("user", FilterOp.Contains, "ALICE")] }));
        // Free text searches name, IPs, users, OS.
        Assert.Equal(["PC-12"], await NamesAsync(ctx, new GridQuery { Search = "alice" }));
    }

    [Fact]
    public async Task Date_older_than_and_within_use_the_query_clock()
    {
        var db = await SeedComputersAsync();
        await using var ctx = db.CreateContext();
        Assert.Equal(["LAPTOP-7", "pc-120"],
            await NamesAsync(ctx, new GridQuery { Filters = [new("lastSeen", FilterOp.OlderThanDays, "7")] }));
        Assert.Equal(["100%_done", "PC-12"],
            await NamesAsync(ctx, new GridQuery { Filters = [new("lastSeen", FilterOp.WithinDays, "7")] }));

        var range = FilterClause.FormatRange(Now.AddDays(-11), Now.AddDays(-9));
        Assert.Equal(["pc-120"], await NamesAsync(ctx, new GridQuery { Filters = [new("lastSeen", FilterOp.Between, range)] }));
    }

    [Fact]
    public async Task Enum_and_bool_filters()
    {
        var db = await SeedComputersAsync();
        await using var ctx = db.CreateContext();
        Assert.Equal(["PC-12", "pc-120"], await NamesAsync(ctx,
            new GridQuery { Filters = [new("adLink", FilterOp.Equals, "Proven|MatchedByName")] }));
        Assert.Equal(["LAPTOP-7"], await NamesAsync(ctx, new GridQuery { Filters = [new("adLink", FilterOp.Equals, "awaitingapproval")] }));
        // An invalid enum value is ignored rather than matching nothing or throwing.
        Assert.Equal(4, (await NamesAsync(ctx, new GridQuery { Filters = [new("adLink", FilterOp.Equals, "Nope")] })).Count);

        Assert.Equal(["PC-12"], await NamesAsync(ctx, new GridQuery { Filters = [new("online", FilterOp.Equals, "true")] }));
        Assert.Equal(3, (await NamesAsync(ctx, new GridQuery { Filters = [new("online", FilterOp.Equals, "false")] })).Count);
        // Several chips are AND-ed.
        Assert.Equal(["pc-120"], await NamesAsync(ctx, new GridQuery
        {
            Filters = [new("online", FilterOp.Equals, "false"), new("name", FilterOp.Contains, "pc")],
        }));
    }

    [Fact]
    public async Task Sorting_and_paging()
    {
        var db = await SeedComputersAsync();
        await using var ctx = db.CreateContext();
        var q = new GridQuery { Sort = "lastSeen", Descending = true, PageSize = 2, Page = 1 };
        var page1 = await ConsoleLists.Computers.QueryAsync(new GridContext(ctx, new Dictionary<string, string?>(), Now), q, Ct);
        Assert.Equal(4, page1.Total);
        Assert.Equal(["PC-12", "100%_done"], page1.Rows.Select(r => r.Name));

        q.Page = 2;
        Assert.Equal(["pc-120", "LAPTOP-7"], await NamesAsync(ctx, q));

        // Unknown sort field falls back to the default (name).
        Assert.Equal("100%_done", (await NamesAsync(ctx, new GridQuery { Sort = "nope" }))[0]);
    }

    [Fact]
    public async Task Tree_nodes_scope_the_computer_list()
    {
        var db = await SeedComputersAsync();
        await using var ctx = db.CreateContext();
        Assert.Equal(["LAPTOP-7"], await NamesAsync(ctx, new GridQuery(), new() { ["node"] = "awaiting" }));
        Assert.Equal(["LAPTOP-7", "pc-120"], await NamesAsync(ctx, new GridQuery(), new() { ["node"] = "offline7" }));
    }

    [Fact]
    public async Task Ou_node_matches_the_subtree()
    {
        var db = await SeedComputersAsync();
        await using var ctx = db.CreateContext();
        var ou = new DirectoryObject
        {
            ObjectGuid = Guid.NewGuid(), Kind = DirectoryObjectKind.OrganizationalUnit, Name = "Sales",
            DistinguishedName = "OU=Sales,DC=corp,DC=local", DistinguishedNameLower = "ou=sales,dc=corp,dc=local",
        };
        var pcObject = new DirectoryObject
        {
            ObjectGuid = Guid.NewGuid(), Kind = DirectoryObjectKind.Computer, Name = "PC-12",
            DistinguishedName = "CN=PC-12,OU=Branch,OU=Sales,DC=corp,DC=local",
            DistinguishedNameLower = "cn=pc-12,ou=branch,ou=sales,dc=corp,dc=local",
        };
        ctx.DirectoryObjects.AddRange(ou, pcObject);
        var pc = await ctx.Computers.SingleAsync(c => c.Name == "PC-12", Ct);
        pc.DirectoryObjectGuid = pcObject.ObjectGuid;
        await ctx.SaveChangesAsync(Ct);

        Assert.Equal(["PC-12"], await NamesAsync(ctx, new GridQuery(), new() { ["node"] = "ou:" + ou.ObjectGuid }));
        var rows = await ConsoleLists.Computers.QueryAsync(new GridContext(ctx, new Dictionary<string, string?>(), Now),
            new GridQuery { Filters = [new("ou", FilterOp.Contains, "branch")] }, Ct);
        Assert.Equal("OU=Branch,OU=Sales,DC=corp,DC=local", Assert.Single(rows.Rows).Ou);
        Assert.Equal(3, (await NamesAsync(ctx, new GridQuery(), new() { ["node"] = "notindir" })).Count);
    }

    /// <summary>Every field of every list, filtered and sorted, translates and runs on PostgreSQL.</summary>
    [Fact]
    public async Task Every_field_of_every_list_translates()
    {
        var db = await SeedComputersAsync();
        await using var ctx = db.CreateContext();
        var gctx = new GridContext(ctx, new Dictionary<string, string?>(), Now);
        foreach (var list in ConsoleLists.All.Values)
        {
            foreach (var field in list.GetFields(gctx))
            {
                foreach (var op in field.Ops)
                {
                    var value = op switch
                    {
                        FilterOp.Between => FilterClause.FormatRange(Now.AddDays(-5), Now),
                        FilterOp.OlderThanDays or FilterOp.WithinDays or FilterOp.AtLeast or FilterOp.AtMost => "1",
                        _ when field.Options.Count > 0 => field.Options[0].Value,
                        _ => "x",
                    };
                    var q = new GridQuery
                    {
                        Filters = [new FilterClause(field.Key, op, value)], Search = "a",
                        Sort = field.Sortable ? field.Key : null, Descending = true,
                    };
                    var csv = await list.ExportCsvAsync(gctx, q, Ct);
                    Assert.True(csv.Length >= 3, $"{list.Name}.{field.Key} {op}");
                }
            }
        }
    }
}
