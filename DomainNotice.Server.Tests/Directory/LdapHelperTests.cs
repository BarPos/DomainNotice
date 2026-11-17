using DomainNotice.Server.Abstractions;
using DomainNotice.Server.Directory;
using DomainNotice.Server.Options;

namespace DomainNotice.Server.Tests.Directory;

public class LdapHelperTests
{
    [Theory]
    [InlineData("plain", "plain")]
    [InlineData(@"a\b", @"a\5cb")]
    [InlineData("a*b", @"a\2ab")]
    [InlineData("(x)", @"\28x\29")]
    [InlineData("nul\0", @"nul\00")]
    [InlineData("*)(objectClass=*", @"\2a\29\28objectClass=\2a")]
    [InlineData("Zażółć", "Zażółć")]
    public void Escape_follows_rfc4515(string input, string expected) => Assert.Equal(expected, LdapFilters.Escape(input));

    [Fact]
    public void Filters_escape_values_and_use_chain_rule()
    {
        Assert.Equal(@"(&(objectClass=group)(|(cn=a\2a)(sAMAccountName=a\2a)))", LdapFilters.GroupByName(" a* "));
        Assert.Equal(@"(&(objectCategory=person)(objectClass=user)(sAMAccountName=j\29doe))", LdapFilters.UserBySamAccountName("j)doe"));
        Assert.Equal("(memberOf:1.2.840.113556.1.4.1941:=CN=A\\28x\\29,DC=e)", LdapFilters.MemberOfAnyInChain(["CN=A(x),DC=e"]));
        Assert.Equal("(|(memberOf:1.2.840.113556.1.4.1941:=CN=A,DC=e)(memberOf:1.2.840.113556.1.4.1941:=CN=B,DC=e))",
            LdapFilters.MemberOfAnyInChain(["CN=A,DC=e", "CN=B,DC=e"]));
        Assert.Throws<ArgumentException>(() => LdapFilters.MemberOfAnyInChain([]));
        Assert.EndsWith("(uSNChanged>=1235))", LdapFilters.SyncedObjectsChangedAfter(1234));
    }

    [Fact]
    public void Dn_split_and_parent_respect_escapes()
    {
        const string dn = @"CN=Smith\, John,OU=Sales,DC=example,DC=local";
        Assert.Equal([@"CN=Smith\, John", "OU=Sales", "DC=example", "DC=local"], DistinguishedNames.Split(dn));
        Assert.Equal("OU=Sales,DC=example,DC=local", DistinguishedNames.Parent(dn));
        Assert.Equal("Smith, John", DistinguishedNames.FirstRdnValue(dn));
        Assert.Equal("DC=local", DistinguishedNames.Parent("DC=example,DC=local"));
        Assert.Null(DistinguishedNames.Parent("DC=local"));
        Assert.Null(DistinguishedNames.Parent(""));
        Assert.Equal("é", DistinguishedNames.Unescape(@"\c3\a9"));
        Assert.Equal(@"cn=a\, b,ou=x,dc=e", DistinguishedNames.Normalize(@"CN=a\, b , OU=X,DC=E"));
    }

    [Theory]
    [InlineData("OU=Sales,DC=example,DC=local", "DC=example,DC=local", true)]
    [InlineData("dc=example, dc=local", "DC=Example,DC=Local", true)]
    [InlineData("CN=x,OU=Sales,DC=example,DC=local", "ou=sales,dc=example,dc=local", true)]
    [InlineData("DC=badexample,DC=local", "DC=example,DC=local", false)]
    [InlineData("OU=x,DC=other,DC=local", "DC=example,DC=local", false)]
    [InlineData("DC=local", "DC=example,DC=local", false)]
    [InlineData(@"CN=a\,DC=example,DC=local", "DC=evil,DC=example,DC=local", false)]
    [InlineData(@"CN=x\,DC=example,DC=local", "DC=example,DC=local", false)]
    [InlineData("", "DC=example,DC=local", false)]
    [InlineData("OU=x,DC=example,DC=local", "", false)]
    [InlineData("OU=x\0,DC=example,DC=local", "DC=example,DC=local", false)]
    public void IsUnderBase_compares_whole_rdns(string dn, string baseDn, bool expected) =>
        Assert.Equal(expected, DistinguishedNames.IsUnderBase(dn, baseDn));

    [Theory]
    [InlineData("jdoe", "DC=example,DC=local", "jdoe@example.local")]
    [InlineData(" jdoe ", "dc=corp, dc=example, dc=com", "jdoe@corp.example.com")]
    [InlineData("jdoe@other.com", "DC=example,DC=local", "jdoe@other.com")]
    [InlineData(@"EXAMPLE\jdoe", "DC=example,DC=local", @"EXAMPLE\jdoe")]
    [InlineData("CN=svc,OU=x,DC=example,DC=local", "DC=example,DC=local", "CN=svc,OU=x,DC=example,DC=local")]
    [InlineData("jdoe", "OU=Staff,O=Org", "jdoe")]
    public void Bind_name_formatting(string user, string baseDn, string expected) =>
        Assert.Equal(expected, BindNames.Format(user, baseDn));

    [Theory]
    [InlineData(@"EXAMPLE\JDoe", "jdoe")]
    [InlineData(@"OTHER\jdoe", "jdoe")]
    [InlineData(" jdoe ", "jdoe")]
    [InlineData("JDoe@example.local", "upn:jdoe@example.local")] // a UPN keeps its own key
    public void Rate_limit_key_is_shared_across_spellings(string user, string expected) =>
        Assert.Equal(expected, BindNames.RateLimitKey(user));

    [Fact]
    public void AuthzId_parsing()
    {
        Assert.Equal((@"EXAMPLE\jdoe", null), BindNames.ParseAuthzId(@"u:EXAMPLE\jdoe"));
        Assert.Equal((null, "CN=x,DC=e"), BindNames.ParseAuthzId("dn:CN=x,DC=e"));
        Assert.Equal((null, null), BindNames.ParseAuthzId(""));
    }

    [Fact]
    public void Sid_and_guid_decoding()
    {
        // S-1-5-21-1004336348-1177238915-682003330-512 (Domain Admins style).
        var sid = new byte[] { 1, 5, 0, 0, 0, 0, 0, 5, 21, 0, 0, 0 }
            .Concat(BitConverter.GetBytes(1004336348u)).Concat(BitConverter.GetBytes(1177238915u))
            .Concat(BitConverter.GetBytes(682003330u)).Concat(BitConverter.GetBytes(512u)).ToArray();
        Assert.Equal("S-1-5-21-1004336348-1177238915-682003330-512", LdapValues.DecodeSid(sid));
        Assert.Null(LdapValues.DecodeSid([1, 2, 0]));
        var g = Guid.NewGuid();
        Assert.Equal(g, LdapValues.DecodeGuid(g.ToByteArray()));
        Assert.Null(LdapValues.DecodeGuid([1, 2, 3]));
        Assert.True(LdapValues.IsDisabled(0x202));
        Assert.False(LdapValues.IsDisabled(0x200));
        Assert.False(LdapValues.IsDisabled(null));
    }

    [Fact]
    public void Group_names_may_be_cn_or_dn()
    {
        Assert.True(DistinguishedNames.LooksLikeDn("CN=DomainNotice Admins,OU=Groups,DC=example,DC=local"));
        Assert.False(DistinguishedNames.LooksLikeDn("DomainNotice Admins"));
        Assert.False(DistinguishedNames.LooksLikeDn("=x"));
    }

    [Fact]
    public async Task Role_mapping_admin_wins_then_viewer()
    {
        static Func<Task<List<string>>> L(params string[] dns) => () => Task.FromResult(dns.ToList());
        Func<IReadOnlyCollection<string>, Task<bool>> MemberOf(params string[] groups) =>
            dns => Task.FromResult(dns.Any(groups.Contains));

        Assert.Equal(ConsoleRoles.Admin, await DomainRoles.ResolveAsync(L("A"), L("V"), MemberOf("A", "V")));
        Assert.Equal(ConsoleRoles.Viewer, await DomainRoles.ResolveAsync(L("A"), L("V"), MemberOf("V")));
        Assert.Null(await DomainRoles.ResolveAsync(L("A"), L("V"), MemberOf("X")));
        Assert.Equal(ConsoleRoles.Viewer, await DomainRoles.ResolveAsync(L(), L("V"), MemberOf("V")));
        var calls = 0;
        Assert.Null(await DomainRoles.ResolveAsync(L(), L(), _ => { calls++; return Task.FromResult(true); }));
        Assert.Equal(0, calls); // empty lists never query (and never match)
    }

    [Fact]
    public void Group_pick_needs_exactly_one_group_matching_sam_or_cn()
    {
        Assert.Equal("CN=a,OU=1", DomainRoles.PickGroupDn(["CN=a,OU=1"]));
        // The same group returned twice (spelling differs) is still one group.
        Assert.Equal("CN=a,OU=1", DomainRoles.PickGroupDn(["CN=a,OU=1", "cn=A, ou=1"]));
        // A look-alike group (its sAMAccountName equals the configured cn of the real one) makes the name ambiguous:
        // it grants nothing instead of taking over.
        Assert.Null(DomainRoles.PickGroupDn(["CN=DomainNotice Admins,OU=Groups", "CN=Fake,OU=Delegated"]));
        Assert.Null(DomainRoles.PickGroupDn([]));
        Assert.Equal("(&(objectClass=group)(|(cn=DomainNotice Admins)(sAMAccountName=DomainNotice Admins)))",
            LdapFilters.GroupByName(" DomainNotice Admins "));
    }

    [Theory]
    [InlineData(@"CORP\alice", "CORP", null, "alice")]
    [InlineData(@"corp\Alice", "CORP", null, "Alice")]
    [InlineData(@"CORP\alice", "CORP", @"corp\alice", "alice")]
    [InlineData(@"CORP\alice", "CORP", "alice@corp.test", "alice")]
    [InlineData(@"CHILD\Administrator", "CORP", "Administrator", null)]   // another domain's account bound
    [InlineData(null, "CORP", @"OTHER\jdoe", null)]                     // typed prefix of another domain
    [InlineData(@"CORP\jdoe", "CORP", @"OTHER\jdoe", null)]
    [InlineData(null, "CORP", "jdoe", null)]                             // no "Who am I?" answer: fail closed
    [InlineData(@"CORP\alice", null, "alice", null)]                     // NetBIOS name unknown: fail closed
    [InlineData("alice", "CORP", "alice", null)]                         // answer without a domain
    [InlineData(@"A\B\c", "A", "c", null)]
    public void Identity_check_allows_only_the_configured_domain(string? whoAmI, string? netBios, string? typed, string? expectedSam)
    {
        var (sam, refusal) = DomainRoles.CheckIdentity(whoAmI, netBios, typed ?? "x");
        Assert.Equal(expectedSam, sam);
        Assert.Equal(expectedSam is null, refusal is not null);
    }

    [Theory]
    [InlineData("DC=corp,DC=test", "DC=corp,DC=test")]
    [InlineData("OU=Staff, OU=Sales,DC=corp,DC=test", "DC=corp,DC=test")]
    [InlineData("OU=Staff,DC=child,DC=corp,DC=test", "DC=child,DC=corp,DC=test")]
    [InlineData("OU=Staff,O=Org", "")]
    public void Domain_dn_of_base_dn(string baseDn, string expected) =>
        Assert.Equal(expected, DomainNetBiosName.DomainDn(baseDn));

    [Fact]
    public void NetBios_name_comes_from_the_exact_crossRef()
    {
        (string?, string?)[] refs =
        [
            ("DC=corp,DC=test", "CORP"),
            ("DC=child,DC=corp,DC=test", "CHILD"),
            ("CN=Configuration,DC=corp,DC=test", null),
        ];
        Assert.Equal("CORP", DomainNetBiosName.Pick(refs, "dc=corp, dc=test"));
        Assert.Equal("CHILD", DomainNetBiosName.Pick(refs, "DC=child,DC=corp,DC=test"));
        Assert.Null(DomainNetBiosName.Pick(refs, "DC=test")); // never a suffix match
        Assert.Null(DomainNetBiosName.Pick(refs, ""));
        Assert.Null(DomainNetBiosName.Pick([("DC=corp,DC=test", "CORP"), ("DC=corp,DC=test", "OTHER")], "DC=corp,DC=test"));
    }

    [Theory]
    [InlineData("alice", true)]
    [InlineData(@"CORP\alice", true)]
    [InlineData("alice@corp.test", true)]
    [InlineData("CN=alice,OU=Sales,DC=corp,DC=test", false)]
    [InlineData("cn=Alice, ou=Sales, dc=corp, dc=test", false)]
    [InlineData("a=b", false)]
    [InlineData(@"\alice", false)]
    [InlineData(@"CORP\", false)]
    [InlineData(@"A\B\c", false)]
    [InlineData("", false)]
    public void Sign_in_names_refuse_dns(string name, bool accepted) =>
        Assert.Equal(accepted, BindNames.IsAcceptedSignInName(name));

    [Fact]
    public void Unknown_security_value_is_warned_about_but_not_refused()
    {
        var o = new DirectoryOptions { Hosts = ["dc1"], BaseDn = "DC=x,DC=y", Security = (DirectorySecurity)3 };
        Assert.Null(LdapSecurityPolicy.ValidateConnection(o, isDevelopment: false));
        Assert.Contains("plain LDAP", LdapSecurityPolicy.UnknownSecurityWarning(o));
        Assert.Null(LdapSecurityPolicy.UnknownSecurityWarning(new DirectoryOptions { Security = DirectorySecurity.StartTls }));
    }

    [Fact]
    public void Security_policy_rules()
    {
        var o = DirectoryTestContext.ConfiguredOptions();
        Assert.Null(LdapSecurityPolicy.ValidateServiceAccount(o, isDevelopment: false));

        o.Security = DirectorySecurity.None;
        Assert.Contains("Development", LdapSecurityPolicy.ValidateConnection(o, false));
        Assert.Null(LdapSecurityPolicy.ValidateConnection(o, true));

        o = DirectoryTestContext.ConfiguredOptions();
        o.AcceptInvalidCertificate = true;
        Assert.Contains("AcceptInvalidCertificate", LdapSecurityPolicy.ValidateConnection(o, false));
        Assert.Null(LdapSecurityPolicy.ValidateConnection(o, true));

        o = DirectoryTestContext.ConfiguredOptions();
        o.TrustedCertificate = "/nonexistent/ca.pem";
        Assert.Contains("does not exist", LdapSecurityPolicy.ValidateConnection(o, false));

        o = DirectoryTestContext.ConfiguredOptions();
        o.BindPasswordSource = "appsettings";
        Assert.Contains("BindPasswordSource", LdapSecurityPolicy.ValidateServiceAccount(o, false));
        o.BindPasswordSource = "Environment:";
        Assert.NotNull(LdapSecurityPolicy.ValidateServiceAccount(o, false));
        o.BindPasswordSource = "Environment:X";
        Assert.Null(LdapSecurityPolicy.ValidateServiceAccount(o, false));
        o.BindUser = "";
        Assert.Contains("BindUser", LdapSecurityPolicy.ValidateServiceAccount(o, false));
        Assert.Null(LdapSecurityPolicy.ValidateConnection(o, false)); // sign-in does not need the service account

        Assert.NotNull(LdapSecurityPolicy.ValidateConnection(new DirectoryOptions(), true));
    }

    [Fact]
    public void Trusted_certificate_loads_pem_and_der()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var req = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=Test CA", rsa,
            System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var dir = System.IO.Directory.CreateTempSubdirectory("dn-cert");
        var pem = Path.Combine(dir.FullName, "ca.pem");
        var der = Path.Combine(dir.FullName, "ca.cer");
        File.WriteAllText(pem, cert.ExportCertificatePem());
        File.WriteAllBytes(der, cert.RawData);
        var o = DirectoryTestContext.ConfiguredOptions();
        o.TrustedCertificate = pem;
        Assert.Null(LdapSecurityPolicy.ValidateConnection(o, false));
        o.TrustedCertificate = der;
        Assert.Null(LdapSecurityPolicy.ValidateConnection(o, false));
        File.WriteAllText(der, "garbage");
        Assert.Contains("could not be read", LdapSecurityPolicy.ValidateConnection(o, false));
        dir.Delete(true);
    }

    [Fact]
    public void Ports_and_host_entries()
    {
        var o = new DirectoryOptions { Security = DirectorySecurity.Ldaps };
        Assert.Equal(636, LdapSecurityPolicy.EffectivePort(o));
        o.Security = DirectorySecurity.StartTls;
        Assert.Equal(389, LdapSecurityPolicy.EffectivePort(o));
        o.Port = 3268;
        Assert.Equal(3268, LdapSecurityPolicy.EffectivePort(o));
        Assert.Equal(("dc1.example.local", 1636), LdapSecurityPolicy.ParseHost("dc1.example.local:1636", 636));
        Assert.Equal(("dc1", 636), LdapSecurityPolicy.ParseHost(" dc1 ", 636));
        Assert.Equal(("fe80::1", 636), LdapSecurityPolicy.ParseHost("fe80::1", 636));
    }

    [Fact]
    public void Classification_of_object_classes()
    {
        Assert.Equal(Data.Entities.DirectoryObjectKind.Computer, DirectorySyncEngine.Classify(["top", "person", "user", "computer"]));
        Assert.Equal(Data.Entities.DirectoryObjectKind.User, DirectorySyncEngine.Classify(["top", "person", "organizationalPerson", "user"]));
        Assert.Equal(Data.Entities.DirectoryObjectKind.Group, DirectorySyncEngine.Classify(["top", "group"]));
        Assert.Equal(Data.Entities.DirectoryObjectKind.OrganizationalUnit, DirectorySyncEngine.Classify(["top", "organizationalUnit"]));
        Assert.Equal(Data.Entities.DirectoryObjectKind.Container, DirectorySyncEngine.Classify(["top", "builtinDomain"]));
        Assert.Equal(Data.Entities.DirectoryObjectKind.Domain, DirectorySyncEngine.Classify(["top", "domain", "domainDNS"]));
        Assert.Null(DirectorySyncEngine.Classify(["top", "person", "organizationalPerson", "contact"]));
        Assert.Null(DirectorySyncEngine.Classify(["top", "foreignSecurityPrincipal"]));
    }
}
