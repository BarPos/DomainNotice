using System.Net;
using System.Text;
using DomainNotice.Server.Options;
using DomainNotice.Server.Updates;
using Microsoft.Extensions.Logging.Abstractions;

namespace DomainNotice.Server.Tests.Updates;

public class GitHubReleaseSourceTests
{
    private static string ReleaseJson(string tag, bool prerelease, string published, bool draft = false) => $$"""
        {
          "tag_name": "{{tag}}", "name": "DomainNotice {{tag}}", "body": "Notes for {{tag}}",
          "published_at": "{{published}}", "prerelease": {{(prerelease ? "true" : "false")}}, "draft": {{(draft ? "true" : "false")}},
          "html_url": "https://github.com/example/domainnotice/releases/tag/{{tag}}",
          "assets": [
            { "name": "SHA256SUMS.txt", "size": 300,
              "url": "https://api.github.com/repos/example/domainnotice/releases/assets/11",
              "browser_download_url": "https://github.com/example/domainnotice/releases/download/{{tag}}/SHA256SUMS.txt" },
            { "name": "DomainNotice.Server-{{tag.TrimStart('v')}}-linux-x64.tar.gz", "size": 12345,
              "url": "https://api.github.com/repos/example/domainnotice/releases/assets/12",
              "browser_download_url": "https://github.com/example/domainnotice/releases/download/{{tag}}/DomainNotice.Server-{{tag.TrimStart('v')}}-linux-x64.tar.gz" },
            { "name": "evil.bin", "size": 1, "url": "http://insecure.example/evil.bin", "browser_download_url": "http://insecure.example/evil.bin" }
          ]
        }
        """;

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static (GitHubReleaseSource Source, RecordingHandler Handler, RecordingHttpClientFactory Factory) Create(UpdatesOptions options)
    {
        var handler = new RecordingHandler();
        var factory = new RecordingHttpClientFactory(handler);
        var source = new GitHubReleaseSource(factory, Microsoft.Extensions.Options.Options.Create(options), NullLogger<GitHubReleaseSource>.Instance);
        return (source, handler, factory);
    }

    [Fact]
    public async Task Stable_channel_reads_releases_latest_and_parses_assets()
    {
        var (source, handler, _) = Create(new UpdatesOptions { GitHubRepository = "example/domainnotice" });
        handler.Responder = _ => Json(ReleaseJson("v1.2.0", false, "2026-10-01T12:00:00Z"));

        var release = await source.GetLatestAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(release);
        Assert.Equal("1.2.0", release.Info.Version);
        Assert.Equal("v1.2.0", release.Info.TagName);
        Assert.Equal("Notes for v1.2.0", release.Info.NotesMarkdown);
        Assert.Equal(new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc), release.Info.PublishedAtUtc);
        Assert.False(release.Info.Prerelease);
        var tar = release.FindAsset("DomainNotice.Server-1.2.0-linux-x64.tar.gz");
        Assert.NotNull(tar);
        Assert.Equal(12345, tar.Size);
        Assert.StartsWith("https://github.com/", tar.DownloadUrl);
        Assert.Null(release.FindAsset("evil.bin")); // non-HTTPS URLs are dropped

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://api.github.com/repos/example/domainnotice/releases/latest", request.RequestUri!.ToString());
        Assert.Equal(GitHubReleaseSource.UserAgent, request.Headers.UserAgent.ToString());
        Assert.StartsWith("DomainNotice-Server/", GitHubReleaseSource.UserAgent);
        Assert.Null(request.Headers.Authorization);
    }

    [Fact]
    public async Task Prerelease_channel_takes_newest_non_draft_release()
    {
        var (source, handler, _) = Create(new UpdatesOptions { GitHubRepository = "example/domainnotice", Channel = "prerelease" });
        handler.Responder = _ => Json("[" + string.Join(",",
            ReleaseJson("v1.3.0", true, "2026-11-10T00:00:00Z", draft: true),
            ReleaseJson("v1.2.0", false, "2026-10-01T00:00:00Z"),
            ReleaseJson("v1.2.1", true, "2026-11-01T00:00:00Z")) + "]");

        var release = await source.GetLatestAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(release);
        Assert.Equal("1.2.1", release.Info.Version);
        Assert.True(release.Info.Prerelease);
        Assert.Equal("/repos/example/domainnotice/releases", Assert.Single(handler.Requests).RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task By_version_reads_the_tag_and_sends_token()
    {
        var (source, handler, _) = Create(new UpdatesOptions { GitHubRepository = "example/domainnotice", GitHubToken = "secret-token" });
        handler.Responder = _ => Json(ReleaseJson("v1.0.0", false, "2026-01-01T00:00:00Z"));

        var release = await source.GetByVersionAsync("1.0.0", TestContext.Current.CancellationToken);

        Assert.NotNull(release);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://api.github.com/repos/example/domainnotice/releases/tags/v1.0.0", request.RequestUri!.ToString());
        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        Assert.Equal("secret-token", request.Headers.Authorization?.Parameter);
        // With a token, assets are fetched via the API URL (works for private repositories).
        Assert.StartsWith("https://api.github.com/", release.FindAsset("SHA256SUMS.txt")!.DownloadUrl);
    }

    [Fact]
    public async Task Missing_release_is_null_and_errors_throw()
    {
        var (source, handler, _) = Create(new UpdatesOptions { GitHubRepository = "example/domainnotice" });
        handler.Responder = _ => new HttpResponseMessage(HttpStatusCode.NotFound);
        Assert.Null(await source.GetByVersionAsync("9.9.9", TestContext.Current.CancellationToken));

        handler.Responder = _ => new HttpResponseMessage(HttpStatusCode.Forbidden);
        await Assert.ThrowsAsync<ReleaseSourceException>(() => source.GetLatestAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-repo")]
    [InlineData("a/b/c")]
    public async Task Blank_or_invalid_repository_makes_no_network_calls(string repo)
    {
        var (source, handler, factory) = Create(new UpdatesOptions { GitHubRepository = repo });
        handler.FailOnRequest = true;

        Assert.False(source.IsConfigured);
        Assert.Null(await source.GetLatestAsync(TestContext.Current.CancellationToken));
        Assert.Null(await source.GetByVersionAsync("1.0.0", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            source.OpenAssetAsync(new ReleaseAsset("x", 1, "https://github.com/x"), TestContext.Current.CancellationToken));

        Assert.Empty(handler.Requests);
        Assert.Equal(0, factory.Created);
    }

    [Fact]
    public void Sums_file_parses_both_sha256sum_formats()
    {
        var a = new string('a', 64);
        var b = new string('B', 64);
        var sums = Sha256Sums.Parse($"{a}  DomainNotice.Server-1.0.0-linux-x64.tar.gz\r\n{b} *install.sh\nbad line\n");
        Assert.Equal(a, sums["DomainNotice.Server-1.0.0-linux-x64.tar.gz"]);
        Assert.Equal(new string('b', 64), sums["install.sh"]);
        Assert.Equal(2, sums.Count);
    }

    [Fact]
    public void Asset_names_match_plan_11_4()
    {
        Assert.Equal("DomainNotice.Server-1.2.3-linux-x64.tar.gz", ReleaseAssetNames.ServerTarball("1.2.3", "x64"));
        Assert.Equal("DomainNotice.Server-1.2.3-linux-arm64.tar.gz", ReleaseAssetNames.ServerTarball("1.2.3", "arm64"));
        Assert.Equal("DomainNotice.Agent-1.2.3-x64.msi", ReleaseAssetNames.AgentMsi("1.2.3"));
        Assert.Equal("SHA256SUMS.txt", ReleaseAssetNames.Sums);
        Assert.Equal("install.sh", ReleaseAssetNames.InstallScript);
        Assert.Equal("v1.2.3", ReleaseAssetNames.Tag("1.2.3"));
    }
}
