using System.Security.AccessControl;
using System.Security.Principal;
using DomainNotice.Agent.Logic;
using Microsoft.Win32.SafeHandles;

namespace DomainNotice.Agent.Platform;

public static class AgentConstants
{
    public const string ServiceName = "DomainNotice.Agent";
    public const string EventSource = "DomainNotice.Agent";
    /// <summary>Logger category that also goes to the Windows Event Log at Information level (start, stop, connection).</summary>
    public const string ServiceEventsCategory = "DomainNotice.Agent.ServiceEvents";
}

/// <summary>Outcome of <see cref="AgentPaths.EnsureCreatedAndSecured"/>: an error (updates and file logging off) and what was repaired.</summary>
public sealed record FolderSecurityResult(string? Error, IReadOnlyList<string> Repairs);

/// <summary>
/// <c>%ProgramData%\DomainNotice</c> with <c>logs\</c> and <c>updates\</c>, used by the service as LocalSystem. Owned by
/// SYSTEM, limited to SYSTEM and Administrators with inheritance broken (plan 3.2). The service re-secures the folders
/// at every start and does not trust what another account may have created there first (see
/// <see cref="EnsureCreatedAndSecured"/>); when that fails, agent updates and file logging stay off.
/// </summary>
public sealed class AgentPaths
{
    private static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private const int ErrorSharingViolation = 32;

    public AgentPaths(string root)
    {
        Root = root;
        Logs = Path.Combine(root, "logs");
        Updates = Path.Combine(root, "updates");
    }

    public static AgentPaths Default { get; } =
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DomainNotice"));

    public string Root { get; }
    public string Logs { get; }
    public string Updates { get; }
    public string InstallMarker => Path.Combine(Updates, Logic.InstallMarker.FileName);

    /// <summary>Why the folders are not safe to use (updates and file logging are then off), or null once secured.</summary>
    public string? InsecureReason { get; private set; } = "The data folder has not been secured yet.";

    /// <summary>
    /// Null when the folders were secured at start and <paramref name="folder"/> and the root are still real folders
    /// (not junctions or links); otherwise the reason not to touch anything in it. Checked before every use.
    /// </summary>
    public string? CheckInUse(string folder) => InsecureReason ?? FolderGuard.CheckDirectories(Root, folder);

    /// <summary>
    /// Makes root, logs and updates real folders owned by SYSTEM with the protected SYSTEM + Administrators DACL:
    /// <list type="number">
    /// <item>A junction, link or file in their place is removed (the link only, never its target).</item>
    /// <item>A folder with another owner, or with access for anyone else, may have been created by a standard user
    /// before the install (who could keep open handles into it): it is moved out of the way and a new one created.</item>
    /// <item>Inside logs and updates, links, subfolders, hard-linked files and files of other owners are deleted.</item>
    /// <item>Then owner and DACL are written (owner first) and the result is checked.</item>
    /// </list>
    /// Any failure is returned as an error; the caller then turns off updates and file logging.
    /// </summary>
    public FolderSecurityResult EnsureCreatedAndSecured()
    {
        var repairs = new List<string>();
        string? error;
        try
        {
            ReplaceIfUntrusted(Root, Path.GetDirectoryName(Root)!, repairs);
            if (!Directory.Exists(Root)) new DirectoryInfo(Root).Create(ProtectedDirectorySecurity());
            if (!IsTrustedDirectory(Root)) throw new IOException($"{Root} changed while it was being secured.");

            foreach (var folder in new[] { Logs, Updates })
            {
                // Moved out of the root, so applying the root DACL below never propagates into what they hold.
                ReplaceIfUntrusted(folder, Path.GetDirectoryName(Root)!, repairs);
                if (Directory.Exists(folder)) CleanEntries(folder, repairs);
            }
            RemoveLinks(Root, 0, repairs);

            ApplyDirectorySecurity(Root);
            foreach (var folder in new[] { Logs, Updates })
            {
                if (!Directory.Exists(folder)) new DirectoryInfo(folder).Create(ProtectedDirectorySecurity());
                ApplyDirectorySecurity(folder);
            }

            error = Verify(Root) ?? Verify(Logs) ?? Verify(Updates);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PrivilegeNotHeldException or SystemException)
        {
            error = $"Could not create or secure {Root}: {ex.Message}";
        }

        InsecureReason = error;
        return new FolderSecurityResult(error, repairs);
    }

    /// <summary>Removes a link or file at <paramref name="path"/>, or moves an untrusted folder to <paramref name="asideIn"/>.</summary>
    private static void ReplaceIfUntrusted(string path, string asideIn, List<string> repairs)
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

        if ((attributes & FileAttributes.ReparsePoint) != 0 || (attributes & FileAttributes.Directory) == 0)
        {
            FolderGuard.DeleteTree(path); // the link itself, or the file
            repairs.Add($"Removed {path}: it was a junction, link or file instead of a folder.");
            return;
        }

        if (IsTrustedDirectory(path)) return;

        // Moving needs no access to the folder itself (SYSTEM may delete children of the parent) and fails while
        // someone holds it open without delete sharing, which is then reported as an error.
        var aside = Path.Combine(asideIn, "DomainNotice.untrusted-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.Move(path, aside);
        repairs.Add($"Moved {path} to {aside}: it was owned by or open to an account other than SYSTEM and Administrators.");
        try
        {
            // Best effort, with the access the old folder gives SYSTEM. Its permissions are not rewritten: that would
            // propagate into whatever it holds (a hard link there is the same file as its target).
            FolderGuard.DeleteTree(aside);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SystemException)
        {
            repairs.Add($"Could not delete {aside}: {ex.Message}");
        }
    }

    /// <summary>Inside logs or updates: keeps only plain files owned by SYSTEM or Administrators with one name.</summary>
    private static void CleanEntries(string directory, List<string> repairs)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(entry);
            string? reason = null;
            if ((attributes & FileAttributes.ReparsePoint) != 0) reason = "a junction or link";
            else if ((attributes & FileAttributes.Directory) != 0) reason = "an unexpected folder";
            else if (!IsTrustedFile(entry, out var why)) reason = why;
            if (reason is null) continue;

            FolderGuard.DeleteTree(entry);
            repairs.Add($"Deleted {entry}: {reason}.");
        }
    }

    /// <summary>Deletes every link below <paramref name="directory"/> (never following one) before DACLs are propagated.</summary>
    private static void RemoveLinks(string directory, int depth, List<string> repairs)
    {
        if (depth >= FolderGuard.MaxDepth) throw new IOException($"{directory} is nested too deeply.");
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                FolderGuard.DeleteTree(entry);
                repairs.Add($"Deleted {entry}: a junction or link.");
            }
            else if ((attributes & FileAttributes.Directory) != 0)
            {
                RemoveLinks(entry, depth + 1, repairs);
            }
        }
    }

    private static bool IsTrustedDirectory(string path)
    {
        try
        {
            var security = new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
            return IsTrusted(security);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PrivilegeNotHeldException or SystemException)
        {
            return false;
        }
    }

    private static bool IsTrustedFile(string path, out string? reason)
    {
        try
        {
            if (!IsTrusted(new FileInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access)))
            {
                reason = "owned by or open to an account other than SYSTEM and Administrators";
                return false;
            }

            SafeFileHandle handle;
            try
            {
                handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            }
            catch (IOException ex) when ((ex.HResult & 0xFFFF) == ErrorSharingViolation)
            {
                // Held open without read sharing: right after a self-update msiexec may still hold the package or its
                // log. Owner and permissions were checked above, so it is kept rather than failing the start.
                reason = null;
                return true;
            }

            using (handle)
            {
                if (NativeMethods.GetLinkCount(handle) != 1)
                {
                    reason = "a hard link to another file";
                    return false;
                }
            }

            reason = null;
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PrivilegeNotHeldException or SystemException)
        {
            reason = "cannot be checked (" + ex.Message + ")";
            return false;
        }
    }

    private static bool IsTrusted(FileSystemSecurity security)
    {
        var owner = security.GetOwner(typeof(SecurityIdentifier))?.Value;
        var allowed = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Where(rule => rule.AccessControlType == AccessControlType.Allow)
            .Select(rule => rule.IdentityReference.Value);
        return FolderTrust.IsTrusted(owner, allowed);
    }

    /// <summary>Owner first (a folder whose DACL left SYSTEM out still lets SYSTEM take ownership), then the DACL.</summary>
    private static void ApplyDirectorySecurity(string directory)
    {
        var info = new DirectoryInfo(directory);
        var owner = new DirectorySecurity();
        owner.SetOwner(LocalSystem);
        info.SetAccessControl(owner); // only the modified section (owner) is written
        info.SetAccessControl(ProtectedDirectorySecurity()); // only the DACL, protected
    }

    private static string? Verify(string directory)
    {
        if (FolderGuard.CheckDirectories(directory) is { } problem) return problem;
        var security = new DirectoryInfo(directory).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        if (!security.AreAccessRulesProtected) return $"{directory} still inherits permissions from its parent.";
        if (!IsTrusted(security)) return $"{directory} is still owned by or open to an account other than SYSTEM and Administrators.";
        return null;
    }

    public static DirectorySecurity ProtectedDirectorySecurity()
    {
        var security = new DirectorySecurity();
        security.SetOwner(LocalSystem);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(LocalSystem, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(Administrators, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }
}
