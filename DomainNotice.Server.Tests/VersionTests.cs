using System.Xml.Linq;
using DomainNotice.Shared;

namespace DomainNotice.Server.Tests;

public class VersionTests
{
    [Fact]
    public void DirectoryBuildProps_matches_AppVersion()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Directory.Build.props.copy");
        var version = XDocument.Load(path).Descendants("Version").First().Value;
        Assert.Equal(AppVersion.Current, version);
    }

    [Theory]
    [InlineData("1.0.0", true)]
    [InlineData("0.9.0", false)] // below MinimumAgent
    [InlineData("1.0.1", false)] // newer than the server
    [InlineData("2.0.0", false)]
    [InlineData("garbage", false)]
    [InlineData(null, false)]
    public void Agent_support_rules(string? agent, bool supported) =>
        Assert.Equal(supported, AppVersion.IsAgentSupported(agent));

    [Theory]
    [InlineData("1.2.3", true)]
    [InlineData("1.2", false)]
    [InlineData("1.2.3-beta", false)]
    [InlineData("../1.2.3", false)]
    public void Plain_triplet(string value, bool ok) => Assert.Equal(ok, AppVersion.IsPlainTriplet(value));
}
