using System.Text.RegularExpressions;
using DomainNotice.Agent.Logic;
using DomainNotice.Agent.Session;
using DomainNotice.Shared;
using DomainNotice.Shared.LocalIpc;

namespace DomainNotice.Agent.Tests;

public sealed class LanguageTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Router_sends_the_language_with_every_status()
    {
        var router = new AnnouncementRouter(TimeProvider.System);
        router.InitializeLanguage("pl");
        var alice = new FakeEndpoint(1, @"CORP\alice");
        await router.OnSessionConnectedAsync(alice, Ct);
        Assert.Equal("pl", alice.Of<ConnectionStatus>().Last().Language);

        await router.SetLinkAsync(new FakeServer(), new ConnectionStatus { Connected = true, ServerName = "srv" }, Ct);
        Assert.Equal("pl", alice.Of<ConnectionStatus>().Last().Language); // kept when the link status changes

        await router.SetLanguageAsync("en", Ct);
        var last = alice.Of<ConnectionStatus>().Last();
        Assert.Equal("en", last.Language);
        Assert.True(last.Connected); // the rest of the status is unchanged
    }

    [Fact]
    public void Agent_texts_switch_language_and_fall_back_to_English()
    {
        try
        {
            AgentTexts.SetLanguage("pl");
            Assert.Equal("Twoje odpowiedzi w tym ogłoszeniu są anonimowe.", AgentTexts.Get("Your answers in this announcement are anonymous."));
            Assert.Equal("Możesz kontynuować za 5 s.", AgentTexts.F("You can continue in {0}s.", 5));
            Assert.Equal("Some text without translation", AgentTexts.Get("Some text without translation"));
            AgentTexts.SetLanguage("xx"); // unknown → English
            Assert.Equal("You can continue.", AgentTexts.Get("You can continue."));
        }
        finally
        {
            AgentTexts.SetLanguage(AppLanguages.Default);
        }
    }

    [Fact]
    public void Polish_agent_texts_keep_the_placeholders()
    {
        static string Set(string s) => string.Join(",", Regex.Matches(s, @"\{\d+\}").Select(m => m.Value).Distinct().OrderBy(x => x));
        AgentTexts.SetLanguage("pl");
        try
        {
            foreach (var key in AgentTexts.PolishKeys)
                Assert.True(Set(key) == Set(AgentTexts.Get(key)), key);
        }
        finally
        {
            AgentTexts.SetLanguage(AppLanguages.Default);
        }
    }
}
