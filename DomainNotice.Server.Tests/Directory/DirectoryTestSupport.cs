using System.Runtime.CompilerServices;
using DomainNotice.Server.Abstractions;
using DomainNotice.Server.Common;
using DomainNotice.Server.Directory;
using DomainNotice.Server.Options;
using DomainNotice.Server.Tests.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace DomainNotice.Server.Tests.Directory;

/// <summary>Hand-written controllable clock (UTC local zone unless set).</summary>
public sealed class ManualTimeProvider(DateTime startUtc) : TimeProvider
{
    private DateTime _now = DateTime.SpecifyKind(startUtc, DateTimeKind.Utc);
    private readonly Lock _gate = new();
    public TimeZoneInfo Zone { get; set; } = TimeZoneInfo.Utc;
    public override TimeZoneInfo LocalTimeZone => Zone;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate) return new DateTimeOffset(_now);
    }

    public void Advance(TimeSpan by)
    {
        lock (_gate) _now += by;
    }
}

public sealed class TestHostEnvironment(string name = "Production") : IHostEnvironment
{
    public string EnvironmentName { get; set; } = name;
    public string ApplicationName { get; set; } = "DomainNotice.Tests";
    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}

/// <summary>An in-memory directory served through <see cref="ILdapSource"/>.</summary>
public sealed class FakeLdapSource : ILdapSource
{
    public const string BaseDn = "DC=example,DC=local";
    public List<LdapDirectoryEntry> Entries { get; } = [];
    public long HighestUsn { get; set; } = 1000;
    public string? DnsHostName { get; set; } = "DC1.example.local";
    public string ConfiguredHost { get; set; } = "dc1.example.local";
    /// <summary>Throw after yielding this many entries (simulates a failed page).</summary>
    public int? FailAfter { get; set; }
    public Exception? OpenError { get; set; }
    public List<long?> Searches { get; } = [];
    /// <summary>What the crossRef lookup returns (null = not found).</summary>
    public string? NetBiosName { get; set; } = "EXAMPLE";

    public Task<ILdapSourceSession> OpenAsync(CancellationToken ct)
    {
        if (OpenError is not null) throw OpenError;
        return Task.FromResult<ILdapSourceSession>(new Session(this));
    }

    /// <summary>Replaces an entry (same GUID) with a modified copy and a new uSNChanged.</summary>
    public LdapDirectoryEntry Update(Guid guid, Func<LdapDirectoryEntry, LdapDirectoryEntry> change)
    {
        var i = Entries.FindIndex(e => e.ObjectGuid == guid);
        HighestUsn++;
        var updated = change(Entries[i]);
        updated = Copy(updated, usn: HighestUsn);
        Entries[i] = updated;
        return updated;
    }

    public static LdapDirectoryEntry Copy(LdapDirectoryEntry e, string? dn = null, string? cn = null, string? displayName = null,
        IReadOnlyList<string>? members = null, int? uac = null, long? usn = null) => new()
    {
        ObjectGuid = e.ObjectGuid,
        DistinguishedName = dn ?? e.DistinguishedName,
        ObjectClasses = e.ObjectClasses,
        Cn = cn ?? e.Cn,
        Ou = e.Ou is null ? null : cn ?? e.Ou,
        Name = cn ?? e.Name,
        SamAccountName = e.SamAccountName,
        DisplayName = displayName ?? e.DisplayName,
        UserPrincipalName = e.UserPrincipalName,
        DnsHostName = e.DnsHostName,
        ObjectSid = e.ObjectSid,
        UserAccountControl = uac ?? e.UserAccountControl,
        Members = members ?? e.Members,
        UsnChanged = usn ?? e.UsnChanged,
    };

    private long NextUsn() => ++HighestUsn;

    public LdapDirectoryEntry AddDomain() => Add(new LdapDirectoryEntry
    {
        ObjectGuid = Guid.NewGuid(), DistinguishedName = BaseDn, ObjectClasses = ["top", "domain", "domainDNS"],
        Name = "example", UsnChanged = NextUsn(),
    });

    public LdapDirectoryEntry AddOu(string name, string parentDn) => Add(new LdapDirectoryEntry
    {
        ObjectGuid = Guid.NewGuid(), DistinguishedName = $"OU={name},{parentDn}", ObjectClasses = ["top", "organizationalUnit"],
        Ou = name, Name = name, UsnChanged = NextUsn(),
    });

    public LdapDirectoryEntry AddUser(string sam, string parentDn, bool disabled = false) => Add(new LdapDirectoryEntry
    {
        ObjectGuid = Guid.NewGuid(), DistinguishedName = $"CN={sam},{parentDn}",
        ObjectClasses = ["top", "person", "organizationalPerson", "user"], Cn = sam, Name = sam, SamAccountName = sam,
        DisplayName = sam.ToUpperInvariant(), UserPrincipalName = sam + "@example.local", ObjectSid = "S-1-5-21-1-2-3-" + Random.Shared.Next(1000, 99999),
        UserAccountControl = disabled ? 0x202 : 0x200, UsnChanged = NextUsn(),
    });

    public LdapDirectoryEntry AddComputer(string name, string parentDn) => Add(new LdapDirectoryEntry
    {
        ObjectGuid = Guid.NewGuid(), DistinguishedName = $"CN={name},{parentDn}",
        ObjectClasses = ["top", "person", "organizationalPerson", "user", "computer"], Cn = name, Name = name,
        SamAccountName = name + "$", DnsHostName = name.ToLowerInvariant() + ".example.local", UserAccountControl = 0x1000,
        UsnChanged = NextUsn(),
    });

    public LdapDirectoryEntry AddGroup(string name, string parentDn, params LdapDirectoryEntry[] members) => Add(new LdapDirectoryEntry
    {
        ObjectGuid = Guid.NewGuid(), DistinguishedName = $"CN={name},{parentDn}", ObjectClasses = ["top", "group"],
        Cn = name, Name = name, SamAccountName = name, Members = members.Select(m => m.DistinguishedName).ToList(),
        UsnChanged = NextUsn(),
    });

    private LdapDirectoryEntry Add(LdapDirectoryEntry e)
    {
        Entries.Add(e);
        return e;
    }

    private sealed class Session(FakeLdapSource source) : ILdapSourceSession
    {
        public string Host => source.ConfiguredHost;

        public Task<LdapRootDse> ReadRootDseAsync(CancellationToken ct) =>
            Task.FromResult(new LdapRootDse(source.HighestUsn, source.DnsHostName));

        public async IAsyncEnumerable<LdapDirectoryEntry> SearchObjectsAsync(long? usnChangedAfter, [EnumeratorCancellation] CancellationToken ct)
        {
            source.Searches.Add(usnChangedAfter);
            var n = 0;
            foreach (var e in source.Entries.ToList())
            {
                if (usnChangedAfter is { } u && e.UsnChanged <= u) continue;
                if (source.FailAfter is { } f && n >= f) throw new DirectoryConnectException("Simulated page failure.");
                n++;
                await Task.Yield();
                yield return e;
            }
        }

        public Task<string?> ReadNetBiosNameAsync(CancellationToken ct) => Task.FromResult(source.NetBiosName);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>Builds the real services over a test database.</summary>
internal sealed class DirectoryTestContext
{
    public required TestDatabase Db { get; init; }
    public required ManualTimeProvider Time { get; init; }
    public required ISettingsStore Settings { get; init; }
    public required IAuditLog Audit { get; init; }

    public static async Task<DirectoryTestContext> CreateAsync(PostgresFixture pg)
    {
        var db = await pg.CreateDatabaseAsync();
        var time = new ManualTimeProvider(new DateTime(2026, 11, 15, 10, 0, 0, DateTimeKind.Utc));
        return new DirectoryTestContext
        {
            Db = db,
            Time = time,
            Settings = new SettingsStore(db, new EphemeralDataProtectionProvider(), time),
            Audit = new AuditLog(db, time, NullLogger<AuditLog>.Instance),
        };
    }

    public DirectorySyncEngine Engine(ILdapSource source) =>
        new(Db, source, Settings, Time, NullLogger<DirectorySyncEngine>.Instance);

    public static DirectoryOptions ConfiguredOptions() => new()
    {
        Hosts = ["dc1.example.local", "dc2.example.local"],
        BaseDn = FakeLdapSource.BaseDn,
        BindUser = "svc-domainnotice",
        BindPasswordSource = "Database",
        AdminGroups = ["DomainNotice Admins"],
    };
}
