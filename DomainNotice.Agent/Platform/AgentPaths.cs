using System.Security.AccessControl;
using System.Security.Principal;

namespace DomainNotice.Agent.Platform;

public static class AgentConstants
{
    public const string ServiceName = "DomainNotice.Agent";
    public const string EventSource = "DomainNotice.Agent";
    /// <summary>Logger category that also goes to the Windows Event Log at Information level (start, stop, connection).</summary>
    public const string ServiceEventsCategory = "DomainNotice.Agent.ServiceEvents";
}

/// <summary>
/// <c>%ProgramData%\DomainNotice</c> with <c>logs\</c> and <c>updates\</c>. The folder is limited to SYSTEM and
/// Administrators with inheritance broken (plan 3.2); the MSI sets this too, the service re-applies it on start.
/// </summary>
public sealed class AgentPaths
{
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

    /// <summary>Creates the folders and applies the protected SYSTEM + Administrators ACL. Returns an error text or null.</summary>
    public string? EnsureCreatedAndSecured()
    {
        try
        {
            var root = System.IO.Directory.CreateDirectory(Root);
            root.SetAccessControl(ProtectedDirectorySecurity());
            System.IO.Directory.CreateDirectory(Logs);
            System.IO.Directory.CreateDirectory(Updates);
            return null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PrivilegeNotHeldException or SystemException)
        {
            return $"Could not create or secure {Root}: {ex.Message}";
        }
    }

    public static DirectorySecurity ProtectedDirectorySecurity()
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }
}
