using DomainNotice.Server.Abstractions;
using DomainNotice.Server.Announcements;
using DomainNotice.Server.Data.Entities;
using DomainNotice.Server.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace DomainNotice.Server.Tests.Announcements;

/// <summary>Which reported user names get a synced domain user's groups and OU (only one domain is supported).</summary>
public class TargetingUserMatchTests(PostgresFixture pg)
{
    private static async Task<(AnnouncementHarness H, Guid Ceo, Guid Board)> SetupAsync(PostgresFixture pg, string? netBios)
    {
        var h = await AnnouncementHarness.CreateAsync(pg, directory: true);
        if (netBios is not null) await h.Settings.SetAsync(SettingKeys.DirectoryNetBiosName, netBios, "test", h.Ct);
        var ceo = await h.AddDirectoryObjectAsync(DirectoryObjectKind.User, "CN=CEO,OU=Board,DC=corp,DC=example", "ceo", "ceo@corp.example");
        var board = await h.AddDirectoryObjectAsync(DirectoryObjectKind.Group, "CN=Board,DC=corp,DC=example", "board");
        await h.AddTransitiveMembershipAsync(board, ceo);
        return (h, ceo, board);
    }

    private static async Task<UserFacts> LoadAsync(AnnouncementHarness h, string user)
    {
        var loader = h.Services.GetRequiredService<TargetingDataLoader>();
        await using var db = h.Db.CreateContext();
        return await loader.LoadUserAsync(db, user, h.Ct);
    }

    [Theory]
    [InlineData(@"CORP\ceo", true)]
    [InlineData(@"corp\CEO", true)]        // case-insensitive
    [InlineData("ceo@corp.example", true)] // UPN
    [InlineData(@"PC01\ceo", false)]       // a local Windows account with the same name
    [InlineData(@"OTHER\ceo", false)]      // another domain
    [InlineData("ceo", false)]             // no domain at all
    [InlineData("ceo@other.example", false)]
    [InlineData(@"\ceo", false)]
    public async Task Only_the_configured_domain_gets_directory_facts(string reported, bool matches)
    {
        var (h, ceo, board) = await SetupAsync(pg, "CORP");
        await using (h)
        {
            var facts = await LoadAsync(h, reported);
            Assert.Equal(reported, facts.UserName);
            Assert.Equal(matches, facts.ObjectGuids.Contains(ceo));
            Assert.Equal(matches, facts.GroupGuids.Contains(board));
            Assert.Equal(matches, facts.DistinguishedNamesLower.Count > 0);
        }
    }

    [Fact]
    public async Task Unknown_NetBios_name_fails_closed_for_domain_names()
    {
        var (h, ceo, _) = await SetupAsync(pg, netBios: null);
        await using (h)
        {
            Assert.Empty((await LoadAsync(h, @"CORP\ceo")).ObjectGuids);
            Assert.Contains(ceo, (await LoadAsync(h, "ceo@corp.example")).ObjectGuids); // UPN does not need it
        }
    }
}
