using DomainNotice.Server.Abstractions;

namespace DomainNotice.Server.Updates;

/// <summary>One downloadable file of a release.</summary>
public sealed record ReleaseAsset(string Name, long Size, string DownloadUrl);

/// <summary>A release with its assets.</summary>
public sealed record Release(ReleaseInfo Info, IReadOnlyList<ReleaseAsset> Assets)
{
    public ReleaseAsset? FindAsset(string name) => Assets.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.Ordinal));
}

/// <summary>
/// Where releases come from (plan 11.4). The production source is GitHub; tests use a fake.
/// When <see cref="IsConfigured"/> is false, implementations make no network calls at all.
/// </summary>
public interface IReleaseSource
{
    bool IsConfigured { get; }

    /// <summary>The newest release of the configured channel, or null when there is none (or not configured).</summary>
    Task<Release?> GetLatestAsync(CancellationToken ct = default);

    /// <summary>The release tagged <c>v&lt;version&gt;</c>, or null when it does not exist (or not configured).</summary>
    Task<Release?> GetByVersionAsync(string version, CancellationToken ct = default);

    /// <summary>Opens a download stream for an asset of a release returned by this source.</summary>
    Task<Stream> OpenAssetAsync(ReleaseAsset asset, CancellationToken ct = default);
}

public sealed class ReleaseSourceException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Release asset names, fixed by plan 11.4.</summary>
public static class ReleaseAssetNames
{
    public const string Sums = "SHA256SUMS.txt";
    public const string InstallScript = "install.sh";

    /// <summary><c>DomainNotice.Server-&lt;version&gt;-linux-&lt;arch&gt;.tar.gz</c>, arch <c>x64</c> or <c>arm64</c>.</summary>
    public static string ServerTarball(string version, string arch) => $"DomainNotice.Server-{version}-linux-{arch}.tar.gz";

    public static string AgentMsi(string version) => $"DomainNotice.Agent-{version}-x64.msi";

    public static string Tag(string version) => "v" + version;
}
