using System.Security.Cryptography;
using DomainNotice.Shared;
using DomainNotice.Shared.Protocol;

namespace DomainNotice.Agent.Logic;

public enum UpdateDecision
{
    /// <summary>The target is newer than this agent: download and install.</summary>
    Install,
    /// <summary>Same version: nothing to do (the server marks the task done from Hello).</summary>
    AlreadyCurrent,
    /// <summary>The target is older: refused, downgrades are not done.</summary>
    Older,
    /// <summary>The version string cannot be parsed.</summary>
    Invalid,
}

public static class UpdatePolicy
{
    public static UpdateDecision Decide(string? targetVersion, string currentVersion)
    {
        if (!AppVersion.TryParse(targetVersion, out var target) || !AppVersion.TryParse(currentVersion, out var current))
            return UpdateDecision.Invalid;
        var cmp = target.CompareTo(current);
        return cmp > 0 ? UpdateDecision.Install : cmp == 0 ? UpdateDecision.AlreadyCurrent : UpdateDecision.Older;
    }

    /// <summary>Validates the UpdateAgent fields that the download depends on. Returns an error or null.</summary>
    public static string? Validate(UpdateAgent command)
    {
        if (command.TaskId == Guid.Empty) return "The update task has no id.";
        if (command.Size <= 0) return "The package size is missing.";
        if (command.Size > PackageDownload.MaxPackageBytes) return $"The package is larger than {PackageDownload.MaxPackageBytes} bytes.";
        if (!IsSha256Hex(command.Sha256)) return "The package SHA-256 is missing or malformed.";
        return null;
    }

    public static bool IsSha256Hex(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);
}

public enum ChunkResult
{
    Accepted,
    /// <summary>The last byte arrived; call <see cref="PackageDownload.Verify"/>.</summary>
    Complete,
    /// <summary>The chunk belongs to another transfer; ignore it.</summary>
    WrongTransfer,
    /// <summary>The chunk does not start at the current offset; request again from <see cref="PackageDownload.Offset"/>.</summary>
    OutOfOrder,
    /// <summary>The chunk goes past the announced size; the download failed.</summary>
    TooLarge,
}

/// <summary>
/// Outcome of <see cref="PackageDownload.Verify"/>. On success <see cref="Package"/> is the verified package, open for
/// reading with read-only sharing: while it is open nobody can write, replace or delete the file. The caller keeps it
/// open until msiexec has been started, then disposes it.
/// </summary>
public sealed record VerifyResult(bool Ok, string? Error, FileStream? Package = null);

/// <summary>
/// Assembles an update package from binary chunks into <c>&lt;dir&gt;\&lt;taskId&gt;.part</c>, resuming from the
/// bytes already on disk, then renames it to <c>&lt;taskId&gt;.msi</c>, opens that without write or delete sharing and
/// checks size and SHA-256 from the open handle (plan 11.3), so what was checked is what msiexec reads. Junctions or
/// links in place of the folder or the files are refused. Not thread-safe; the server link reads frames one at a time.
/// </summary>
public sealed class PackageDownload : IDisposable
{
    /// <summary>Sanity cap on the package size (the MSI is far smaller).</summary>
    public const long MaxPackageBytes = 2L * 1024 * 1024 * 1024;

    private FileStream? _file;

    private PackageDownload(UpdateAgent command, string directory)
    {
        TaskId = command.TaskId;
        Version = command.Version;
        Size = command.Size;
        Sha256 = command.Sha256.ToLowerInvariant();
        PartPath = Path.Combine(directory, TaskId.ToString("D") + ".part");
        PackagePath = Path.Combine(directory, TaskId.ToString("D") + ".msi");
    }

    public Guid TaskId { get; }
    public string Version { get; }
    public long Size { get; }
    public string Sha256 { get; }
    public string PartPath { get; }
    public string PackagePath { get; }
    public long Offset { get; private set; }
    public bool IsComplete => Offset == Size;

    /// <summary>True when the command describes the same package (a re-sent UpdateAgent after reconnect).</summary>
    public bool Matches(UpdateAgent command) =>
        command.TaskId == TaskId && command.Size == Size &&
        string.Equals(command.Sha256, Sha256, StringComparison.OrdinalIgnoreCase);

    /// <summary>Opens the .part file of this task, keeping what is already there (resume).</summary>
    public static PackageDownload OpenOrResume(string directory, UpdateAgent command)
    {
        System.IO.Directory.CreateDirectory(directory);
        if (FolderGuard.CheckDirectories(directory) is { } problem) throw new IOException(problem);
        var download = new PackageDownload(command, directory);
        RemoveLink(download.PartPath);
        RemoveLink(download.PackagePath);
        if (File.Exists(download.PackagePath))
        {
            // Verified earlier (e.g. the install was interrupted): move it back for a fresh check.
            File.Move(download.PackagePath, download.PartPath, overwrite: true);
        }

        download._file = new FileStream(download.PartPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        if (download._file.Length > download.Size) download._file.SetLength(0);
        download.Offset = download._file.Length;
        download._file.Seek(download.Offset, SeekOrigin.Begin);
        return download;
    }

    public ChunkResult Append(PackageChunk chunk)
    {
        if (chunk.TransferId != TaskId) return ChunkResult.WrongTransfer;
        if (chunk.Offset != Offset) return ChunkResult.OutOfOrder;
        if (Offset + chunk.Data.Length > Size) return ChunkResult.TooLarge;
        var file = _file ?? throw new ObjectDisposedException(nameof(PackageDownload));
        file.Write(chunk.Data.Span);
        file.Flush();
        Offset += chunk.Data.Length;
        return IsComplete ? ChunkResult.Complete : ChunkResult.Accepted;
    }

    /// <summary>
    /// Renames the file to <see cref="PackagePath"/>, opens it with <see cref="FileShare.Read"/> only and checks size
    /// and SHA-256 from that handle. On success the open handle is returned in <see cref="VerifyResult.Package"/>
    /// (dispose it after msiexec started); on any failure the file is deleted so the next attempt starts over.
    /// </summary>
    public VerifyResult Verify()
    {
        CloseFile();
        var directory = Path.GetDirectoryName(PartPath)!;
        if (FolderGuard.CheckDirectories(directory) is { } problem) return Fail(problem);
        if (FolderGuard.IsLink(PartPath)) return Fail($"{PartPath} is a link.");
        if (!File.Exists(PartPath)) return Fail($"Package size is 0 bytes, expected {Size}.");

        // Rename first, then open without write/delete sharing: from here on the content cannot change under us.
        File.Move(PartPath, PackagePath, overwrite: true);
        FileStream? package = null;
        try
        {
            package = new FileStream(PackagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (FolderGuard.IsLink(PackagePath)) return Fail($"{PackagePath} is a link.", package);
            if (package.Length != Size) return Fail($"Package size is {package.Length} bytes, expected {Size}.", package);

            var actual = Convert.ToHexStringLower(SHA256.HashData(package));
            if (!string.Equals(actual, Sha256, StringComparison.Ordinal))
                return Fail($"Package SHA-256 is {actual}, expected {Sha256}.", package);

            package.Seek(0, SeekOrigin.Begin);
            return new VerifyResult(true, null, package);
        }
        catch
        {
            package?.Dispose();
            throw;
        }
    }

    private VerifyResult Fail(string error, FileStream? open = null)
    {
        open?.Dispose();
        Discard();
        return new VerifyResult(false, error);
    }

    /// <summary>Deletes the partial file (and a verified package, if any).</summary>
    public void Discard()
    {
        CloseFile();
        Offset = 0;
        TryDelete(PartPath);
        TryDelete(PackagePath);
    }

    /// <summary>Deletes leftovers of other tasks (and files older than <paramref name="maxAge"/>) in the folder.</summary>
    public static void CleanFolder(string directory, Guid? keepTaskId, TimeSpan maxAge, DateTime nowUtc)
    {
        // Never through a junction or link: that would delete files somewhere else.
        if (FolderGuard.CheckDirectories(directory) is not null) return;
        foreach (var path in System.IO.Directory.EnumerateFiles(directory))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var keep = keepTaskId is { } id && name.Equals(id.ToString("D"), StringComparison.OrdinalIgnoreCase);
            var ext = Path.GetExtension(path);
            var isPackage = ext.Equals(".part", StringComparison.OrdinalIgnoreCase) || ext.Equals(".msi", StringComparison.OrdinalIgnoreCase);
            var old = nowUtc - File.GetLastWriteTimeUtc(path) > maxAge;
            if (!keep && (isPackage || old)) TryDelete(path);
        }
    }

    private void CloseFile()
    {
        _file?.Dispose();
        _file = null;
    }

    /// <summary>Deletes a link at <paramref name="path"/> (the link only), so it is never written through.</summary>
    private static void RemoveLink(string path)
    {
        if (FolderGuard.IsLink(path)) FolderGuard.DeleteTree(path);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public void Dispose() => CloseFile();
}
