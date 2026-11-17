namespace DomainNotice.Agent.Logic;

/// <summary>
/// Link checks for the agent's data folders. A junction or symbolic link in <c>%ProgramData%\DomainNotice</c> could send
/// the service's (LocalSystem) file operations somewhere else, so every path is checked before use and links are only
/// ever removed, never followed. Platform-neutral (FileAttributes.ReparsePoint is also set for links on Unix).
/// </summary>
public static class FolderGuard
{
    /// <summary>Most levels <see cref="DeleteTree"/> goes down; the agent's folders are flat.</summary>
    public const int MaxDepth = 16;

    /// <summary>True when the path itself (not its target) is a junction, symbolic link or other reparse point.</summary>
    public static bool IsLink(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    /// <summary>
    /// Null when every given directory exists, is a real directory and none of them is a link; otherwise the reason.
    /// Pass the folder in use and its parents up to the data root.
    /// </summary>
    public static string? CheckDirectories(params string[] directories)
    {
        foreach (var directory in directories)
        {
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(directory);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return $"{directory} does not exist.";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return $"{directory} cannot be checked: {ex.Message}";
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0) return $"{directory} is a junction or link.";
            if ((attributes & FileAttributes.Directory) == 0) return $"{directory} is not a folder.";
        }
        return null;
    }

    /// <summary>
    /// Deletes a file, a link (the link only, never its target) or a directory tree without descending into links.
    /// Missing paths are fine. Throws on failure.
    /// </summary>
    public static void DeleteTree(string path) => DeleteTree(path, 0);

    private static void DeleteTree(string path, int depth)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return;
        }

        var isDirectory = (attributes & FileAttributes.Directory) != 0;
        if ((attributes & FileAttributes.ReadOnly) != 0) File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            // Removes the link itself: Directory.Delete (non-recursive) on a junction or directory link, File.Delete on a file link.
            if (isDirectory) System.IO.Directory.Delete(path, recursive: false);
            else File.Delete(path);
            return;
        }

        if (!isDirectory)
        {
            File.Delete(path);
            return;
        }

        if (depth >= MaxDepth) throw new IOException($"{path} is nested too deeply to delete.");
        foreach (var entry in System.IO.Directory.EnumerateFileSystemEntries(path))
            DeleteTree(entry, depth + 1);
        System.IO.Directory.Delete(path, recursive: false);
    }
}

/// <summary>
/// Which owners and access entries are acceptable on the data folders and the files in them: only LocalSystem and
/// BUILTIN\Administrators. Works on SID strings so the rule is testable away from Windows.
/// </summary>
public static class FolderTrust
{
    public const string LocalSystemSid = "S-1-5-18";
    public const string AdministratorsSid = "S-1-5-32-544";

    public static bool IsTrustedSid(string? sid) =>
        string.Equals(sid, LocalSystemSid, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(sid, AdministratorsSid, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when the owner is SYSTEM or Administrators and no allow entry names anyone else. Anything else may have
    /// been created or opened by another account, and is replaced rather than re-used.
    /// </summary>
    public static bool IsTrusted(string? ownerSid, IEnumerable<string> allowedSids) =>
        IsTrustedSid(ownerSid) && allowedSids.All(IsTrustedSid);
}
