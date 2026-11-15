using DomainNotice.Server.Abstractions;
using DomainNotice.Server.Tests.Infrastructure;
using DomainNotice.Shared;
using DomainNotice.Shared.Protocol;

namespace DomainNotice.Server.Tests.Integration;

/// <summary>One language for the installation: stored, pushed to agents on connect and on change, and sent with announcements.</summary>
public class LanguageTests(PostgresFixture pg)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Language_reaches_agents_on_connect_on_change_and_with_announcements()
    {
        var db = await pg.CreateDatabaseAsync();
        await using var server = await TestAgentServer.StartAsync(db, fullCore: true);
        var language = server.Get<IAppLanguage>();
        Assert.Equal(AppLanguages.English, language.Current);
        Assert.False((await language.SetAsync("de", "tester", Ct)).Ok);
        Assert.True((await language.SetAsync("pl", "tester", Ct)).Ok);
        Assert.Equal("pl", language.Current);

        var (agent, _) = await server.ConnectEnrolledAsync("PC-LANG", [TestAgentClient.Session(1, @"CORP\ola")]);
        await using var _a = agent;
        Assert.Equal("pl", (await agent.ReceiveAsync<AgentConfig>(TimeSpan.FromSeconds(10), Ct)).Language);

        var now = DateTime.UtcNow;
        var (saved, _) = await server.Get<IAnnouncementService>().SaveAsync(new AnnouncementEditModel
        {
            Title = "Lang", DisplayTitle = "Język", BodyMarkdown = "x", StartUtc = now.AddMinutes(-1), EndUtc = now.AddDays(1),
            Buttons = [new ButtonEdit { Label = "OK" }],
        }, "tester", Ct);
        Assert.True(saved.Ok, saved.Error);
        var show = await agent.ReceiveAsync<Show>(TimeSpan.FromSeconds(20), Ct);
        Assert.Equal("pl", show.Announcement.Language);

        Assert.True((await language.SetAsync("en", "tester", Ct)).Ok);
        Assert.Equal("en", (await agent.ReceiveAsync<AgentConfig>(TimeSpan.FromSeconds(10), Ct)).Language);
    }
}
