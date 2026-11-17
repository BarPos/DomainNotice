using System.Security.Cryptography;
using DomainNotice.Agent.Logic;
using DomainNotice.Shared.Protocol;

namespace DomainNotice.Agent.Tests;

// Links here are symbolic links (what macOS/Linux offer); on Windows the same checks see junctions too, because both
// carry FileAttributes.ReparsePoint.

public sealed class FolderGuardTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dn-agent-guard-" + Guid.NewGuid().ToString("N"));

    public FolderGuardTests() => System.IO.Directory.CreateDirectory(_dir);

    public void Dispose() => FolderGuard.DeleteTree(_dir);

    private string Sub(string name) => Path.Combine(_dir, name);

    [Fact]
    public void Sees_links_to_folders_and_files_but_not_real_ones()
    {
        var target = System.IO.Directory.CreateDirectory(Sub("target")).FullName;
        File.WriteAllText(Sub("file.txt"), "x");
        System.IO.Directory.CreateSymbolicLink(Sub("dirlink"), target);
        File.CreateSymbolicLink(Sub("filelink"), Sub("file.txt"));

        Assert.False(FolderGuard.IsLink(target));
        Assert.False(FolderGuard.IsLink(Sub("file.txt")));
        Assert.False(FolderGuard.IsLink(Sub("missing")));
        Assert.True(FolderGuard.IsLink(Sub("dirlink")));
        Assert.True(FolderGuard.IsLink(Sub("filelink")));
    }

    [Fact]
    public void CheckDirectories_refuses_links_files_and_missing_folders()
    {
        var real = System.IO.Directory.CreateDirectory(Sub("real")).FullName;
        System.IO.Directory.CreateSymbolicLink(Sub("link"), real);
        File.WriteAllText(Sub("file"), "x");

        Assert.Null(FolderGuard.CheckDirectories(_dir, real));
        Assert.Contains("junction or link", FolderGuard.CheckDirectories(_dir, Sub("link")));
        Assert.Contains("not a folder", FolderGuard.CheckDirectories(Sub("file")));
        Assert.Contains("does not exist", FolderGuard.CheckDirectories(Sub("missing")));
    }

    [Fact]
    public void DeleteTree_removes_links_but_never_what_they_point_to()
    {
        var outside = Path.Combine(Path.GetTempPath(), "dn-agent-outside-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(outside);
        try
        {
            File.WriteAllText(Path.Combine(outside, "precious.txt"), "keep me");
            var tree = System.IO.Directory.CreateDirectory(Sub("tree")).FullName;
            System.IO.Directory.CreateDirectory(Path.Combine(tree, "a", "b"));
            File.WriteAllText(Path.Combine(tree, "a", "b", "f.txt"), "x");
            var readOnly = Path.Combine(tree, "a", "ro.txt");
            File.WriteAllText(readOnly, "x");
            File.SetAttributes(readOnly, FileAttributes.ReadOnly);
            System.IO.Directory.CreateSymbolicLink(Path.Combine(tree, "a", "junction"), outside);
            File.CreateSymbolicLink(Path.Combine(tree, "filelink"), Path.Combine(outside, "precious.txt"));

            FolderGuard.DeleteTree(tree);
            FolderGuard.DeleteTree(tree); // missing is fine

            Assert.False(System.IO.Directory.Exists(tree));
            Assert.Equal("keep me", File.ReadAllText(Path.Combine(outside, "precious.txt")));

            // A link given directly: only the link goes.
            System.IO.Directory.CreateSymbolicLink(Sub("toplink"), outside);
            FolderGuard.DeleteTree(Sub("toplink"));
            Assert.False(FolderGuard.IsLink(Sub("toplink")));
            Assert.True(File.Exists(Path.Combine(outside, "precious.txt")));
        }
        finally
        {
            System.IO.Directory.Delete(outside, true);
        }
    }

    [Theory]
    [InlineData("S-1-5-18", new[] { "S-1-5-18", "S-1-5-32-544" }, true)]
    [InlineData("S-1-5-32-544", new[] { "S-1-5-18" }, true)]
    [InlineData("S-1-5-21-1-2-3-1001", new[] { "S-1-5-18", "S-1-5-32-544" }, false)] // pre-created by a user
    [InlineData("S-1-5-18", new[] { "S-1-5-18", "S-1-5-32-545" }, false)] // Users still have access
    [InlineData("S-1-5-18", new[] { "S-1-1-0" }, false)] // Everyone
    [InlineData("S-1-5-18", new[] { "S-1-3-0" }, false)] // CREATOR OWNER
    [InlineData(null, new string[0], false)]
    public void Only_system_and_administrators_are_trusted(string? owner, string[] allowed, bool expected) =>
        Assert.Equal(expected, FolderTrust.IsTrusted(owner, allowed));
}

public sealed class UpdateFolderLinkTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dn-agent-links-" + Guid.NewGuid().ToString("N"));
    private readonly string _outside = Path.Combine(Path.GetTempPath(), "dn-agent-victim-" + Guid.NewGuid().ToString("N"));

    public UpdateFolderLinkTests()
    {
        System.IO.Directory.CreateDirectory(_root);
        System.IO.Directory.CreateDirectory(_outside);
    }

    public void Dispose()
    {
        FolderGuard.DeleteTree(_root);
        FolderGuard.DeleteTree(_outside);
    }

    private string Updates => Path.Combine(_root, "updates");

    private static (UpdateAgent Command, byte[] Data) MakePackage(int size = 1000)
    {
        var data = new byte[size];
        new Random(3).NextBytes(data);
        return (new UpdateAgent { TaskId = Guid.NewGuid(), Version = "1.2.0", Size = size, Sha256 = Convert.ToHexStringLower(SHA256.HashData(data)) }, data);
    }

    [Fact]
    public void Download_refuses_an_updates_folder_that_is_a_link()
    {
        System.IO.Directory.CreateSymbolicLink(Updates, _outside);
        var (cmd, _) = MakePackage();

        Assert.Throws<IOException>(() => PackageDownload.OpenOrResume(Updates, cmd));
        Assert.Empty(System.IO.Directory.EnumerateFileSystemEntries(_outside));
    }

    [Fact]
    public void A_planted_part_link_is_removed_not_written_through()
    {
        System.IO.Directory.CreateDirectory(Updates);
        var victim = Path.Combine(_outside, "victim.bin");
        File.WriteAllText(victim, "original");
        var (cmd, data) = MakePackage();
        File.CreateSymbolicLink(Path.Combine(Updates, cmd.TaskId.ToString("D") + ".part"), victim);
        File.CreateSymbolicLink(Path.Combine(Updates, cmd.TaskId.ToString("D") + ".msi"), victim);

        using var download = PackageDownload.OpenOrResume(Updates, cmd);
        Assert.Equal(0, download.Offset);
        Assert.Equal(ChunkResult.Complete, download.Append(new PackageChunk(cmd.TaskId, 0, data)));
        var verify = download.Verify();
        Assert.True(verify.Ok, verify.Error);
        verify.Package!.Dispose();

        Assert.Equal("original", File.ReadAllText(victim));
        Assert.False(FolderGuard.IsLink(download.PackagePath));
    }

    [Fact]
    public void Verify_fails_when_the_folder_was_swapped_for_a_link()
    {
        System.IO.Directory.CreateDirectory(Updates);
        var (cmd, data) = MakePackage();
        using var download = PackageDownload.OpenOrResume(Updates, cmd);
        download.Append(new PackageChunk(cmd.TaskId, 0, data));
        download.Dispose();

        // Swap: move the real folder away and put a link to an attacker-controlled copy in its place.
        var moved = Path.Combine(_root, "moved");
        System.IO.Directory.Move(Updates, moved);
        File.Copy(Path.Combine(moved, cmd.TaskId.ToString("D") + ".part"), Path.Combine(_outside, cmd.TaskId.ToString("D") + ".part"));
        System.IO.Directory.CreateSymbolicLink(Updates, _outside);

        var verify = download.Verify();
        Assert.False(verify.Ok);
        Assert.Null(verify.Package);
        Assert.Contains("junction or link", verify.Error);
    }

    [Fact]
    public void Verify_fails_when_the_part_file_is_a_link()
    {
        System.IO.Directory.CreateDirectory(Updates);
        var (cmd, data) = MakePackage();
        using var download = PackageDownload.OpenOrResume(Updates, cmd);
        download.Append(new PackageChunk(cmd.TaskId, 0, data));
        download.Dispose();

        var copy = Path.Combine(_outside, "copy.part");
        File.Copy(download.PartPath, copy);
        File.Delete(download.PartPath);
        File.CreateSymbolicLink(download.PartPath, copy);

        var verify = download.Verify();
        Assert.False(verify.Ok);
        Assert.Contains("is a link", verify.Error);
        Assert.True(File.Exists(copy)); // the link went, not its target
    }

    [Fact]
    public void CleanFolder_does_not_delete_through_a_link()
    {
        File.WriteAllText(Path.Combine(_outside, Guid.NewGuid() + ".msi"), "x");
        System.IO.Directory.CreateSymbolicLink(Updates, _outside);

        PackageDownload.CleanFolder(Updates, null, TimeSpan.Zero, DateTime.UtcNow.AddDays(30));

        Assert.Single(System.IO.Directory.EnumerateFiles(_outside));
    }

    [Fact]
    public void Marker_log_path_must_be_this_tasks_log_in_the_updates_folder()
    {
        System.IO.Directory.CreateDirectory(Updates);
        var taskId = Guid.NewGuid();
        var own = InstallMarker.LogPathFor(Updates, taskId);
        File.WriteAllText(own, "log");
        var secret = Path.Combine(_outside, "secret.txt");
        File.WriteAllText(secret, "secret");

        InstallMarker Marker(string logPath) => new(taskId, "1.2.0", logPath, DateTime.UtcNow);

        Assert.Equal(Path.GetFullPath(own), Marker(own).SafeLogPath(Updates));
        Assert.Null(Marker(secret).SafeLogPath(Updates));
        Assert.Null(Marker(Path.Combine(Updates, "..", "..", Path.GetFileName(_outside), "secret.txt")).SafeLogPath(Updates));
        Assert.Null(Marker(InstallMarker.LogPathFor(Updates, Guid.NewGuid())).SafeLogPath(Updates)); // another task's
        Assert.Null(Marker("").SafeLogPath(Updates));

        // The right name, but a link to somewhere else.
        File.Delete(own);
        File.CreateSymbolicLink(own, secret);
        Assert.Null(Marker(own).SafeLogPath(Updates));
    }

    [Fact]
    public void A_marker_that_is_a_link_is_ignored()
    {
        System.IO.Directory.CreateDirectory(Updates);
        var elsewhere = Path.Combine(_outside, "marker.txt");
        new InstallMarker(Guid.NewGuid(), "1.2.0", "x", DateTime.UtcNow).Write(elsewhere);
        var path = Path.Combine(Updates, InstallMarker.FileName);
        File.CreateSymbolicLink(path, elsewhere);

        Assert.NotNull(InstallMarker.TryRead(elsewhere));
        Assert.Null(InstallMarker.TryRead(path));
    }
}

public sealed class KerberosSpnPolicyTests
{
    [Theory]
    [InlineData("DomainNotice/notice.example.local", "DomainNotice/notice.example.local")]
    [InlineData("  DomainNotice/notice.example.local  ", "DomainNotice/notice.example.local")]
    [InlineData("domainnotice/NOTICE.example.local", "domainnotice/NOTICE.example.local")]
    [InlineData("DOMAINNOTICE/server", "DOMAINNOTICE/server")]
    [InlineData("DomainNotice/10.0.0.5", "DomainNotice/10.0.0.5")]
    [InlineData("DomainNotice/notice.example.local:5150", "DomainNotice/notice.example.local:5150")]
    public void Accepts_domainnotice_service_principals(string spn, string expected) =>
        Assert.Equal(expected, KerberosSpnPolicy.Accept(spn));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("HTTP/notice.example.local")]
    [InlineData("ldap/dc1.example.local")]
    [InlineData("cifs/fileserver")]
    [InlineData("host/pc1")]
    [InlineData("DomainNotice")]
    [InlineData("DomainNotice/")]
    [InlineData("DomainNoticeX/notice.example.local")]
    [InlineData("XDomainNotice/notice.example.local")]
    [InlineData("DomainNotice/notice.example.local/extra")]
    [InlineData("DomainNotice/dc1.example.local/example.local")]
    [InlineData("DomainNotice/notice.example.local@EXAMPLE.LOCAL")]
    [InlineData("DomainNotice/notice example")]
    [InlineData("DomainNotice/notice\\x")]
    [InlineData("ldap/DomainNotice/x")]
    public void Refuses_other_service_classes_and_malformed_names(string? spn) =>
        Assert.Null(KerberosSpnPolicy.Accept(spn));

    [Fact]
    public void Refused_names_are_logged_shortened_and_without_control_characters()
    {
        Assert.Equal("ldap/dc?x", KerberosSpnPolicy.ForLog("ldap/dc\nx"));
        Assert.Equal(123, KerberosSpnPolicy.ForLog(new string('a', 500)).Length);
        Assert.Equal("", KerberosSpnPolicy.ForLog(null));
    }
}
