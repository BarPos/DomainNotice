namespace DomainNotice.Server.Data.Entities;

public enum DirectoryObjectKind
{
    Domain = 0,
    OrganizationalUnit = 1,
    Container = 2,
    User = 3,
    Computer = 4,
    Group = 5,
}

/// <summary>A synced AD object (plan 7.5). Key = objectGUID.</summary>
public class DirectoryObject
{
    public Guid ObjectGuid { get; set; }
    public DirectoryObjectKind Kind { get; set; }
    public string DistinguishedName { get; set; } = "";
    /// <summary>Lowercased DN for suffix (OU subtree) matching: the DN exactly as AD returns it (no spaces after
    /// commas), ToLowerInvariant. Objects under an OU: <c>DistinguishedNameLower.EndsWith("," + ouDnLower)</c>.</summary>
    public string DistinguishedNameLower { get; set; } = "";
    /// <summary>objectGUID of the parent container; null for the BaseDn object itself.</summary>
    public Guid? ParentGuid { get; set; }
    /// <summary>cn / ou / name.</summary>
    public string Name { get; set; } = "";
    public string? SamAccountName { get; set; }
    /// <summary>Lowercase sAMAccountName (computers include the trailing $).</summary>
    public string? SamAccountNameLower { get; set; }
    public string? DisplayName { get; set; }
    public string? UserPrincipalName { get; set; }
    public string? DnsHostName { get; set; }
    public string? ObjectSid { get; set; }
    public bool Enabled { get; set; } = true;
    public long UsnChanged { get; set; }
    public DateTime LastSyncedUtc { get; set; }
    /// <summary>Gone from AD (tombstoned or out of BaseDn). Kept so stored targets still show a name.</summary>
    public bool Deleted { get; set; }
}

/// <summary>Direct group membership (member attribute). Only members that are synced objects (under BaseDn);
/// primaryGroupID membership (e.g. Domain Users) is not included.</summary>
public class DirectoryMembership
{
    public Guid GroupGuid { get; set; }
    public Guid MemberGuid { get; set; }
}

/// <summary>Transitive (nested) membership, recomputed by the sync job. Targeting reads this table.
/// Every (group, member) pair reachable through nested groups, including the nested groups themselves and the
/// direct members; a group is never listed as its own member (also not inside a membership cycle).</summary>
public class DirectoryTransitiveMembership
{
    public Guid GroupGuid { get; set; }
    public Guid MemberGuid { get; set; }
}

public enum DirectorySyncKind
{
    Full = 0,
    Incremental = 1,
}

public class DirectorySyncRun
{
    public long Id { get; set; }
    public DirectorySyncKind Kind { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public DateTime? FinishedAtUtc { get; set; }
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? Host { get; set; }
    public int ObjectsChanged { get; set; }
    public long HighestUsn { get; set; }
    public string? RequestedBy { get; set; }
}
