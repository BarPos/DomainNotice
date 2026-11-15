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

public sealed record VerifyResult(bool Ok, string? Error);

/// <summary>
/// Assembles an update package from binary chunks into <c>&lt;dir&gt;\&lt;taskId&gt;.part</c>, resuming from the
/// bytes already on disk, then checks size and SHA-256 and renames it to <c>&lt;taskId&gt;.msi</c> (plan 11.3).
/// Not thread-safe; the server link reads frames one at a time.
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
        var download = new PackageDownload(command, directory);
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
    /// Checks size and SHA-256. On success the file is renamed to <see cref="PackagePath"/>; on a mismatch it is
    /// deleted so the next attempt starts over.
    /// </summary>
    public VerifyResult Verify()
    {
        CloseFile();
        var info = new FileInfo(PartPath);
        if (!info.Exists || info.Length != Size)
        {
            Discard();
            return new VerifyResult(false, $"Package size is {(info.Exists ? info.Length : 0)} bytes, expected {Size}.");
        }

        string actual;
        using (var stream = new FileStream(PartPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            actual = Convert.ToHexStringLower(SHA256.HashData(stream));

        if (!string.Equals(actual, Sha256, StringComparison.Ordinal))
        {
            Discard();
            return new VerifyResult(false, $"Package SHA-256 is {actual}, expected {Sha256}.");
        }

        File.Move(PartPath, PackagePath, overwrite: true);
        return new VerifyResult(true, null);
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
        if (!System.IO.Directory.Exists(directory)) return;
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
